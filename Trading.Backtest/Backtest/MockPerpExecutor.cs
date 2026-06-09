using GripTrader.Core.Bot;
using GripTrader.Core.Models;
using GripTrader.Core.Settings;
using GripTrader.Core.Utils;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GripTrader.Core.Backtest
{
    /// <summary>
    /// In-memory USDⓈ-M perpetual-futures mock executor implementing
    /// <see cref="IOrderExecutor"/> <b>from scratch</b>. It is NOT a subclass or
    /// copy of <see cref="MockBinanceExecutor"/> (which is sealed, buy-only/long-only,
    /// computes spot equity, and nets commission out of the base asset). It shares
    /// only the <see cref="IOrderExecutor"/> contract and the
    /// <c>GripTrader.Core.Models</c> value types.
    ///
    /// <para><b>One instance per leg (per symbol).</b> A pair holds its two legs on
    /// different symbols, so the harness owns one executor per leg. Under isolated
    /// margin (the default) this keeps liquidation strictly per-leg; cross-margin
    /// account aggregation is performed by the multi-symbol feeder/harness summing
    /// each leg's <see cref="EquityBreakdown"/>, never inside this single-symbol
    /// executor.</para>
    ///
    /// <para><b>Modeling assumptions (documented so the backtest-vs-live gap stays
    /// explicit):</b></para>
    /// <list type="bullet">
    ///   <item><b>Direction via sign convention</b> (Binance one-way mode): one
    ///   signed net <see cref="decimal"/> position per symbol — <c>isBuy=true</c>
    ///   ⇒ +qty, <c>isBuy=false</c> ⇒ −qty. A short is a negative net; closing is
    ///   the opposite-side fill toward zero. No <c>positionSide</c>/<c>reduceOnly</c>
    ///   is added to the seam. Hedge mode is out of scope.</item>
    ///   <item><b>Fees are notional, quote-side:</b> <c>fee = |fillPrice × qty| ×
    ///   rate</c> debits the quote wallet; the signed position changes by exactly
    ///   <c>±qty</c> — the fee never touches size (the literal inverse of the spot
    ///   base-asset netting). Market fills pay taker; resting-limit fills pay maker.</item>
    ///   <item><b>Slippage</b> is one-sided on <b>market</b> fills only (longs lift
    ///   <c>Ask</c>, shorts hit <c>Bid</c>); since kline <c>Bid == Ask</c> carries
    ///   no spread it stands in for half-spread + impact.</item>
    ///   <item><b>Margin:</b> initial = <c>notional / leverage</c>; maintenance =
    ///   <c>notional × maintMarginRatio − maintAmount</c>, clamped ≥ 0, where
    ///   <c>maintAmount</c> is an absolute USDT decimal (Binance <c>cum</c>), NOT a
    ///   fraction. A single conservative static bracket from
    ///   <see cref="PerpExecutorConfig"/> (high ratio, amount 0) — no historical
    ///   brackets exist; this errs toward more-frequent liquidation.</item>
    ///   <item><b>Liquidation</b> is probed on the <b>mark</b> price against the
    ///   adverse intra-bar extreme (<c>mark.High</c> for shorts, <c>mark.Low</c>
    ///   for longs) — or the close under <see cref="LiquidationProbe.CloseOnly"/>.
    ///   A leg is liquidated when allocated equity (allocated margin + leg
    ///   unrealized + leg realized funding) &lt; maintenance; the full leg is
    ///   closed at the modeled liquidation price, realized PnL booked,
    ///   <see cref="WasLiquidated"/> set. On 1h bars this is an upper-bound
    ///   heuristic (synthetic wicks).</item>
    ///   <item><b>Funding</b> is applied event-by-event at the timestamps the
    ///   feeder supplies (interval-agnostic; never inferred): <c>Δwallet =
    ///   −signedSize × mark × rate</c> (rate&gt;0 ⇒ longs pay shorts). Tracked
    ///   <b>separately</b> in <see cref="CumFunding"/> / realized-funding — never
    ///   folded into price PnL — though it does move the wallet so it feeds
    ///   margin/liquidation.</item>
    ///   <item><b>Determinism:</b> decimal-only money math (no <c>double</c>/
    ///   <c>float</c>), no wall-clock/RNG/<c>Task.Delay</c>; time comes only from
    ///   <c>mark.TimestampMs</c> and funding timestamps. The executor does no epoch
    ///   math — it consumes already-normalized timestamps. <see cref="Task.FromResult"/>
    ///   on the async surface. Same data ⇒ byte-identical state.</item>
    /// </list>
    /// </summary>
    public sealed class MockPerpExecutor : IOrderExecutor
    {
        private enum PerpOrderStatus { New, Filled, Canceled }

        private sealed class PerpOrder
        {
            public long OrderId;
            public string Symbol = "";
            public decimal Price;
            public decimal Quantity;
            public bool IsBuy;
            public PerpOrderStatus Status;
            public decimal ExecutedQuantity;
            public decimal AveragePrice;
            public string? ClientOrderId;
        }

        private readonly object _lock = new();

        private readonly decimal _leverage;
        private readonly MarginMode _marginMode;
        private readonly decimal _maintMarginRatio;
        private readonly decimal _maintAmount;
        private readonly decimal _takerFeeRate;
        private readonly decimal _makerFeeRate;
        private readonly decimal _slippagePct;
        private readonly LiquidationProbe _liquidationProbe;
        private readonly decimal _initialWallet;
        private readonly TradingPair? _filters;

        // Free quote-asset balance NOT currently locked as initial margin.
        private decimal _wallet;
        // Initial margin currently locked against the open position (isolated allocation).
        private decimal _allocatedMargin;

        // Signed net position: >0 long, <0 short, 0 flat.
        private decimal _netQty;
        // VWAP entry of the currently-open signed position (absolute price).
        private decimal _avgEntryPrice;

        private decimal _realizedPricePnl;
        private decimal _realizedFunding;
        // Funding accrued on the CURRENT open position only (reset on flat/flip, scaled
        // on partial reduce). The per-leg liquidation decision uses THIS, not the
        // lifetime _realizedFunding, so funding realized on a previously-closed position
        // cannot contaminate the current position's allocated equity.
        private decimal _positionFunding;
        private decimal _cumFeesQuote;
        private decimal _cumSlippageCost;

        // Latest fill quote (last trade) and mark bar, set each ProcessTick.
        private decimal _latestBid;
        private decimal _latestAsk;
        private MarkBar _latestMark;
        private bool _haveMark;

        private bool _wasLiquidated;

        private long _nextOrderId;
        private readonly Dictionary<long, PerpOrder> _activeOrders = new();
        private readonly Dictionary<long, PerpOrder> _completedOrders = new();
        private readonly Queue<long> _completedOrderInsertion = new();
        private const int CompletedOrdersCap = 1024;

        /// <summary>
        /// Construct a single-leg perp mock executor from a strategy-agnostic
        /// <see cref="PerpExecutorConfig"/>. <paramref name="exchangeFilters"/> is
        /// optional LOT_SIZE / PRICE_FILTER / NOTIONAL enforcement, applied via the
        /// shared <see cref="MathHelpers"/> exactly as the spot mock does.
        /// </summary>
        public MockPerpExecutor(PerpExecutorConfig config, TradingPair? exchangeFilters = null)
        {
            _leverage = config.Leverage <= 0m ? 1m : config.Leverage;
            _marginMode = config.MarginMode;
            _maintMarginRatio = config.MaintMarginRatio < 0m ? 0m : config.MaintMarginRatio;
            _maintAmount = config.MaintAmount < 0m ? 0m : config.MaintAmount;
            _takerFeeRate = config.TakerFeeRate < 0m ? 0m : config.TakerFeeRate;
            _makerFeeRate = config.MakerFeeRate < 0m ? 0m : config.MakerFeeRate;
            _slippagePct = config.SlippagePct < 0m ? 0m : config.SlippagePct;
            _liquidationProbe = config.LiquidationProbe;
            _initialWallet = config.InitialWalletQuote;
            _wallet = config.InitialWalletQuote;
            _filters = exchangeFilters;
        }

        // ---- Read-only state for the harness / tests -----------------------

        /// <summary>Signed net position (&gt;0 long, &lt;0 short, 0 flat).</summary>
        public decimal NetQty { get { lock (_lock) return _netQty; } }

        /// <summary>VWAP entry of the currently-open position; 0 when flat.</summary>
        public decimal AvgEntryPrice { get { lock (_lock) return _avgEntryPrice; } }

        /// <summary>Free quote wallet not locked as initial margin.</summary>
        public decimal Wallet { get { lock (_lock) return _wallet; } }

        /// <summary>Initial margin currently locked against the open position.</summary>
        public decimal AllocatedMargin { get { lock (_lock) return _allocatedMargin; } }

        /// <summary>Cumulative realized PnL from price moves only (funding excluded).</summary>
        public decimal RealizedPricePnl { get { lock (_lock) return _realizedPricePnl; } }

        /// <summary>
        /// Cumulative realized funding (separate accounting), never folded into
        /// <see cref="RealizedPricePnl"/>. Negative = net paid, positive = net received.
        /// </summary>
        public decimal RealizedFunding { get { lock (_lock) return _realizedFunding; } }

        /// <summary>Alias for <see cref="RealizedFunding"/> — cumulative net funding wallet impact.</summary>
        public decimal CumFunding { get { lock (_lock) return _realizedFunding; } }

        /// <summary>Cumulative notional fees paid in quote terms (maker + taker).</summary>
        public decimal CumFeesQuote { get { lock (_lock) return _cumFeesQuote; } }

        /// <summary>Cumulative slippage cost on market fills (quote terms).</summary>
        public decimal CumSlippageCost { get { lock (_lock) return _cumSlippageCost; } }

        /// <summary>True once this leg has been liquidated at least once.</summary>
        public bool WasLiquidated { get { lock (_lock) return _wasLiquidated; } }

        /// <summary>
        /// Margin mode for this leg. The single-symbol executor always liquidates
        /// per-leg (isolated semantics); under <see cref="MarginMode.Cross"/> the
        /// multi-symbol harness reads this to know it must aggregate legs' equity for
        /// the account-level liquidation decision (this executor does not aggregate).
        /// </summary>
        public MarginMode MarginMode => _marginMode;

        // --------------------------------------------------------------------

        /// <summary>
        /// Maintenance margin for a notional band:
        /// <c>notional × maintMarginRatio − maintAmount</c>, clamped ≥ 0.
        /// <paramref name="notional"/> = <c>mark × |size|</c>.
        /// </summary>
        private decimal MaintenanceFor(decimal notional)
        {
            var maint = notional * _maintMarginRatio - _maintAmount;
            return maint < 0m ? 0m : maint;
        }

        /// <summary>Initial margin for a notional band: <c>notional / leverage</c>.</summary>
        private decimal InitialFor(decimal notional) => notional / _leverage;

        /// <summary>
        /// Perp tick. Order is executor-before-strategy: (1) update fill quote +
        /// mark, (2) match resting limits on BOTH sides, (3) liquidation probe on
        /// the adverse mark extreme. Funding is applied separately via
        /// <see cref="ApplyFunding"/> (the feeder applies due funding events before
        /// this tick). <paramref name="fillQuote"/> (last trade) drives matching;
        /// <c>mark.Close</c> marks unrealized PnL; <c>mark.High</c>/<c>mark.Low</c>
        /// drive the liquidation probe. The executor performs no epoch math.
        /// </summary>
        public void ProcessTick(string symbol, BidAsk fillQuote, MarkBar mark)
        {
            lock (_lock)
            {
                _latestBid = fillQuote.Bid;
                _latestAsk = fillQuote.Ask;
                _latestMark = mark;
                _haveMark = true;

                MatchRestingOrders();
                ProbeLiquidation();
            }
        }

        // Caller holds _lock. Fills resting limits on both sides:
        //   buy  fills when Ask <= price  (net += qty)
        //   sell fills when Bid >= price  (net -= qty) — the path MockBinanceExecutor lacks.
        private void MatchRestingOrders()
        {
            List<long>? filled = null;
            foreach (var kv in _activeOrders)
            {
                var order = kv.Value;
                var hit = order.IsBuy
                    ? _latestAsk <= order.Price
                    : _latestBid >= order.Price;
                if (!hit)
                    continue;

                // Resting limits pay the maker rate and fill at the resting price.
                if (!TryApplyFill(order.Price, order.Quantity, order.IsBuy, _makerFeeRate))
                    continue; // insufficient free margin — leave resting

                order.Status = PerpOrderStatus.Filled;
                order.ExecutedQuantity = order.Quantity;
                order.AveragePrice = order.Price;
                (filled ??= new()).Add(kv.Key);
            }

            if (filled != null)
            {
                foreach (var id in filled)
                {
                    AddCompletedOrder(id, _activeOrders[id]);
                    _activeOrders.Remove(id);
                }
            }
        }

        // Caller holds _lock. Applies a fill of `qty` at `fillPrice` on the given
        // side, paying `feeRate` notional fee in quote. Returns false (no state
        // change) when the fill would drive free margin negative. The signed
        // position changes by EXACTLY ±qty — the fee never touches size.
        private bool TryApplyFill(decimal fillPrice, decimal qty, bool isBuy, decimal feeRate)
        {
            if (qty <= 0m || fillPrice <= 0m)
                return false;

            var signedDelta = isBuy ? qty : -qty;
            var fee = fillPrice * qty * feeRate; // notional fee, always >= 0

            // Determine how this fill splits into a reducing portion (closes some
            // of the opposite-signed open position, booking realized PnL and
            // releasing initial margin) and an increasing portion (opens/extends
            // exposure in the new net direction, requiring fresh initial margin).
            var newNet = _netQty + signedDelta;

            decimal closingQty = 0m;     // absolute qty that reduces existing exposure
            decimal openingQty = 0m;     // absolute qty that increases exposure
            if (_netQty == 0m)
            {
                openingQty = qty;
            }
            else if ((_netQty > 0m) == isBuy)
            {
                // Same direction as the open position ⇒ pure increase.
                openingQty = qty;
            }
            else
            {
                // Opposite direction ⇒ reduces, possibly flipping through zero.
                var absNet = _netQty < 0m ? -_netQty : _netQty;
                if (qty <= absNet)
                {
                    closingQty = qty;
                }
                else
                {
                    closingQty = absNet;
                    openingQty = qty - absNet;
                }
            }

            // Initial margin required for the opening portion at the fill price.
            var addMargin = openingQty > 0m ? InitialFor(fillPrice * openingQty) : 0m;
            // Initial margin released by the closing portion (proportional to the
            // closed fraction of the current position).
            decimal releaseMargin = 0m;
            decimal realizedDelta = 0m;
            if (closingQty > 0m)
            {
                var absNet = _netQty < 0m ? -_netQty : _netQty;
                var closedFraction = closingQty / absNet;
                releaseMargin = _allocatedMargin * closedFraction;

                // Realized price PnL on the closed portion vs VWAP entry.
                // Long close: (fill - entry) × closedQty; short close: (entry - fill) × closedQty.
                realizedDelta = _netQty > 0m
                    ? (fillPrice - _avgEntryPrice) * closingQty
                    : (_avgEntryPrice - fillPrice) * closingQty;
            }

            // Free-margin check: fee + new initial margin must be covered by the
            // free wallet plus the margin released and realized PnL booked by this
            // fill. (Reject fills that would drive free margin negative.)
            var freeAfter = _wallet + releaseMargin + realizedDelta - fee - addMargin;
            if (freeAfter < 0m)
                return false;

            // Commit. Slippage cost (already baked into fillPrice for market orders
            // by the caller) is tracked by the caller; here we only move money.
            _wallet = freeAfter;
            _allocatedMargin += addMargin - releaseMargin;
            if (_allocatedMargin < 0m)
                _allocatedMargin = 0m;

            _realizedPricePnl += realizedDelta;
            _cumFeesQuote += fee;

            // Update VWAP entry / net size.
            if (openingQty > 0m && closingQty == 0m && _netQty != 0m && (_netQty > 0m) == isBuy)
            {
                // Same-direction extension: blend VWAP.
                var absNet = _netQty < 0m ? -_netQty : _netQty;
                var newAbs = absNet + openingQty;
                _avgEntryPrice = (_avgEntryPrice * absNet + fillPrice * openingQty) / newAbs;
            }
            else if (openingQty > 0m && closingQty > 0m)
            {
                // Flipped through zero: the new position is the opening portion at fill price.
                _avgEntryPrice = fillPrice;
            }
            else if (openingQty > 0m && _netQty == 0m)
            {
                // Opened from flat.
                _avgEntryPrice = fillPrice;
            }
            // Pure reduction (closingQty>0, openingQty==0): VWAP entry unchanged;
            // if it fully closes, reset entry below.

            // Attribute funding to the CURRENT position only (no lifetime leak into the
            // per-position liquidation decision): reset on flat/flip, scale on partial
            // reduce. The funding cash already moved to the wallet in ApplyFunding.
            if (newNet == 0m || (closingQty > 0m && openingQty > 0m))
            {
                _positionFunding = 0m;
            }
            else if (closingQty > 0m)
            {
                var preAbs = _netQty < 0m ? -_netQty : _netQty;
                var remAbs = newNet < 0m ? -newNet : newNet;
                _positionFunding = preAbs > 0m ? _positionFunding * (remAbs / preAbs) : 0m;
            }

            _netQty = newNet;
            if (_netQty == 0m)
                _avgEntryPrice = 0m;

            return true;
        }

        // Caller holds _lock. Liquidate the full leg if allocated equity falls
        // below maintenance at the adverse mark extreme (or the close).
        private void ProbeLiquidation()
        {
            if (!_haveMark || _netQty == 0m)
                return;

            var isLong = _netQty > 0m;
            decimal probePrice = _liquidationProbe == LiquidationProbe.CloseOnly
                ? _latestMark.Close
                : (isLong ? _latestMark.Low : _latestMark.High);

            if (probePrice <= 0m)
                return;

            var absNet = isLong ? _netQty : -_netQty;
            var notional = probePrice * absNet;
            var maint = MaintenanceFor(notional);

            // Allocated equity = allocated initial margin + leg unrealized PnL at the
            // probe price + funding accrued on THIS position (per-position, not the
            // lifetime accumulator — funding realized on a previously-closed position
            // must not prop up this position's liquidation equity).
            var unrealized = _netQty * (probePrice - _avgEntryPrice);
            var equity = _allocatedMargin + unrealized + _positionFunding;

            if (equity < maint)
            {
                // Liquidate the full leg at the adverse-extreme (modeled liquidation)
                // price. Book the realized loss vs VWAP entry and charge a taker fee on
                // the liquidated notional — a forced close is at least as costly as a
                // voluntary one, never cheaper. Closing at the adverse extreme rather
                // than the maintenance-touch price can book a loss exceeding the
                // allocated margin: intentional and conservative (over-states the
                // hazard, never PnL).
                var realizedDelta = isLong
                    ? (probePrice - _avgEntryPrice) * absNet
                    : (_avgEntryPrice - probePrice) * absNet;
                var liqFee = notional * _takerFeeRate;

                _realizedPricePnl += realizedDelta;
                _cumFeesQuote += liqFee;
                _wallet += _allocatedMargin + realizedDelta - liqFee;
                _allocatedMargin = 0m;
                _netQty = 0m;
                _avgEntryPrice = 0m;
                _positionFunding = 0m;
                _wasLiquidated = true;
            }
        }

        /// <summary>
        /// Apply a single funding event for this symbol at <paramref name="timestampMs"/>.
        /// <c>Δwallet = − signedSize × markPrice × fundingRate</c> (rate&gt;0 ⇒ longs
        /// pay shorts). Interval-agnostic: the executor applies exactly the events
        /// the feeder hands it, never inferring an interval. Funding is tracked in
        /// <see cref="RealizedFunding"/>/<see cref="CumFunding"/> SEPARATELY from
        /// price PnL, but does move the wallet (so it feeds margin/liquidation).
        /// </summary>
        /// <returns><c>true</c> if funding actually settled (an open position existed and
        /// the rate/mark were valid); <c>false</c> if it was a no-op — so a caller can
        /// report only the funding that moved a wallet (e.g. the feeder's dueFunding).</returns>
        public bool ApplyFunding(string symbol, decimal markPrice, decimal fundingRate, long timestampMs)
        {
            lock (_lock)
            {
                // No-op when there is no open position to settle against (e.g. funding due
                // on the bar that first opens the position — funding runs before the fill),
                // or the rate/mark is degenerate.
                if (_netQty == 0m || markPrice <= 0m || fundingRate == 0m)
                    return false;

                var deltaWallet = -_netQty * markPrice * fundingRate;
                _wallet += deltaWallet;
                _realizedFunding += deltaWallet;   // lifetime accumulator (reporting / attribution)
                _positionFunding += deltaWallet;   // current position only (feeds the liquidation decision)
                return true;
            }
        }

        // ---- IOrderExecutor ------------------------------------------------

        public Task<PlaceMarketResult> PlaceMarketAsync(string symbol, decimal quantity, bool isBuy)
        {
            lock (_lock)
            {
                if (quantity <= 0m)
                    return Task.FromResult(default(PlaceMarketResult));

                // One-sided market slippage: longs lift the Ask, shorts hit the Bid.
                var basePrice = isBuy ? _latestAsk : _latestBid;
                if (basePrice <= 0m)
                    return Task.FromResult(default(PlaceMarketResult));

                var fillPrice = isBuy
                    ? basePrice * (1m + _slippagePct)
                    : basePrice * (1m - _slippagePct);

                if (_filters is TradingPair f)
                {
                    quantity = MathHelpers.ClampQuantity(f, quantity, fillPrice);
                    if (quantity <= 0m)
                        return Task.FromResult(default(PlaceMarketResult));
                }

                // Market fills pay the taker rate. The slippage cost (vs the
                // unslipped quote) is accumulated for honest reporting.
                var slipPerUnit = isBuy ? fillPrice - basePrice : basePrice - fillPrice;

                if (!TryApplyFill(fillPrice, quantity, isBuy, _takerFeeRate))
                    return Task.FromResult(default(PlaceMarketResult));

                _cumSlippageCost += slipPerUnit * quantity;

                var id = ++_nextOrderId;
                AddCompletedOrder(id, new PerpOrder
                {
                    OrderId = id,
                    Symbol = symbol,
                    Price = fillPrice,
                    Quantity = quantity,
                    IsBuy = isBuy,
                    Status = PerpOrderStatus.Filled,
                    ExecutedQuantity = quantity,
                    AveragePrice = fillPrice,
                });

                return Task.FromResult(new PlaceMarketResult { Price = fillPrice, Quantity = quantity });
            }
        }

        public Task<PlaceLimitResult> PlaceLimitAsync(string symbol, decimal quantity, decimal price, bool isBuy, string? newClientOrderId = null)
        {
            lock (_lock)
            {
                if (_filters is TradingPair f)
                {
                    price = MathHelpers.ClampPrice(f, price);
                    quantity = MathHelpers.ClampQuantity(f, quantity, price);
                    if (price <= 0m || quantity <= 0m)
                        return Task.FromResult(default(PlaceLimitResult));
                }

                if (quantity <= 0m || price <= 0m)
                    return Task.FromResult(default(PlaceLimitResult));

                // Resting order: no money moves until it matches in ProcessTick.
                // (Margin sufficiency is re-checked at fill time.)
                var id = ++_nextOrderId;
                _activeOrders[id] = new PerpOrder
                {
                    OrderId = id,
                    Symbol = symbol,
                    Price = price,
                    Quantity = quantity,
                    IsBuy = isBuy,
                    Status = PerpOrderStatus.New,
                    ClientOrderId = newClientOrderId,
                };

                return Task.FromResult(new PlaceLimitResult { OrderId = id, Price = price, Quantity = quantity });
            }
        }

        public Task<bool> CancelOrderAsync(string symbol, long orderId)
        {
            lock (_lock)
            {
                if (_activeOrders.TryGetValue(orderId, out var order))
                {
                    order.Status = PerpOrderStatus.Canceled;
                    AddCompletedOrder(orderId, order);
                    _activeOrders.Remove(orderId);
                    return Task.FromResult(true);
                }
                return Task.FromResult(false);
            }
        }

        public Task<OrderQueryResult> QueryOrderAsync(string symbol, long orderId)
        {
            lock (_lock)
            {
                if (_activeOrders.TryGetValue(orderId, out var order)
                    || _completedOrders.TryGetValue(orderId, out order))
                {
                    return Task.FromResult(BuildQueryResult(order));
                }
                return Task.FromResult(new OrderQueryResult { Status = OrderStatus.Unknown });
            }
        }

        public Task<OrderQueryResult> QueryOrderByClientIdAsync(string symbol, string clientOrderId)
        {
            if (string.IsNullOrEmpty(clientOrderId))
                return Task.FromResult(new OrderQueryResult { Status = OrderStatus.Unknown });

            lock (_lock)
            {
                foreach (var order in _activeOrders.Values)
                {
                    if (string.Equals(order.ClientOrderId, clientOrderId, System.StringComparison.Ordinal))
                        return Task.FromResult(BuildQueryResult(order));
                }
                foreach (var order in _completedOrders.Values)
                {
                    if (string.Equals(order.ClientOrderId, clientOrderId, System.StringComparison.Ordinal))
                        return Task.FromResult(BuildQueryResult(order));
                }
                return Task.FromResult(new OrderQueryResult { Status = OrderStatus.Unknown });
            }
        }

        private static OrderQueryResult BuildQueryResult(PerpOrder order)
        {
            var status = order.Status switch
            {
                PerpOrderStatus.New => OrderStatus.New,
                PerpOrderStatus.Filled => OrderStatus.Filled,
                PerpOrderStatus.Canceled => OrderStatus.Canceled,
                _ => OrderStatus.Unknown,
            };
            return new OrderQueryResult
            {
                Status = status,
                ExecutedQuantity = order.ExecutedQuantity,
                AveragePrice = order.AveragePrice,
                OrderId = order.OrderId,
            };
        }

        /// <summary>
        /// Total account equity for this single leg at <paramref name="markPrice"/>:
        /// <c>wallet + allocatedMargin + signedSize × (mark − entry)</c>. Funding is
        /// already folded into the wallet (it moves real money) but remains
        /// separately reported via <see cref="RealizedFunding"/>. For a short
        /// (<c>signedSize &lt; 0</c>), a mark BELOW entry is a profit.
        /// </summary>
        public Task<decimal> GetCurrentTotalEquityAsync(string symbol, decimal markPrice)
        {
            lock (_lock)
            {
                var unrealized = _netQty * (markPrice - _avgEntryPrice);
                var equity = _wallet + _allocatedMargin + unrealized;
                return Task.FromResult(equity);
            }
        }

        /// <summary>
        /// Per-leg <see cref="EquityBreakdown"/> at <paramref name="markPrice"/>:
        /// the allocated-equity figure the liquidation decision uses
        /// (<c>allocatedMargin + leg unrealized + realized funding</c>) and the leg
        /// maintenance margin (<c>notional × ratio − amount</c>, clamped ≥ 0). This
        /// is the override of the default-preserving seam member; the harness sums
        /// these across legs for cross-margin account aggregation.
        /// </summary>
        public Task<EquityBreakdown> GetEquityBreakdownAsync(string symbol, decimal markPrice)
        {
            lock (_lock)
            {
                if (_netQty == 0m)
                    return Task.FromResult(new EquityBreakdown(_wallet, 0m));

                var absNet = _netQty < 0m ? -_netQty : _netQty;
                var notional = markPrice * absNet;
                var maint = MaintenanceFor(notional);
                var unrealized = _netQty * (markPrice - _avgEntryPrice);
                var allocatedEquity = _allocatedMargin + unrealized + _positionFunding;
                return Task.FromResult(new EquityBreakdown(allocatedEquity, maint));
            }
        }

        // Insert into _completedOrders with FIFO eviction. Caller holds _lock.
        private void AddCompletedOrder(long id, PerpOrder order)
        {
            _completedOrders[id] = order;
            _completedOrderInsertion.Enqueue(id);
            while (_completedOrderInsertion.Count > CompletedOrdersCap)
            {
                var evict = _completedOrderInsertion.Dequeue();
                _completedOrders.Remove(evict);
            }
        }

        /// <summary>Reset all state so the executor can be reused for another run.</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _activeOrders.Clear();
                _completedOrders.Clear();
                _completedOrderInsertion.Clear();
                _nextOrderId = 0;
                _wallet = _initialWallet;
                _allocatedMargin = 0m;
                _netQty = 0m;
                _avgEntryPrice = 0m;
                _realizedPricePnl = 0m;
                _realizedFunding = 0m;
                _positionFunding = 0m;
                _cumFeesQuote = 0m;
                _cumSlippageCost = 0m;
                _latestBid = 0m;
                _latestAsk = 0m;
                _latestMark = default;
                _haveMark = false;
                _wasLiquidated = false;
            }
        }
    }
}
