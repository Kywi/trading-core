using GripTrader.Core.Bot;
using GripTrader.Core.Models;
using GripTrader.Core.Utils;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GripTrader.Core.Backtest
{
    internal enum MockOrderStatus
    {
        New,
        Filled,
        Canceled
    }

    internal sealed class MockOrder
    {
        public long OrderId;
        public string Symbol = "";
        public decimal Price;
        public decimal Quantity;
        public bool IsBuy;
        public MockOrderStatus Status;
        public decimal ExecutedQuantity;
        public decimal AveragePrice;
        public string? ClientOrderId;
    }

    /// <summary>
    /// In-memory fake matching engine that implements <see cref="IOrderExecutor"/>.
    /// Limit Buy orders are filled when <c>currentAsk &lt;= orderPrice</c>.
    /// Market orders execute instantly at the latest known Bid (sell) or Ask (buy).
    /// Returned prices are raw (no fee adjustment) so GridBot's own 0.1% fee
    /// calculation stays correct. Fees are tracked internally for reporting.
    /// <para>
    /// Orders are split into two dictionaries: <c>_activeOrders</c> (status
    /// <see cref="MockOrderStatus.New"/>) and <c>_completedOrders</c> (Filled
    /// or Canceled). <see cref="ProcessTick"/> only iterates active orders,
    /// keeping the per-tick cost proportional to the number of open limit
    /// orders rather than the total number of orders ever placed.
    /// </para>
    /// </summary>
    public sealed class MockBinanceExecutor : IOrderExecutor
    {
        // Per-side commission. Sourced from GridConfig.CommissionRate so backtest
        // fees track live (BNB discount / VIP tiers), instead of a hard-coded 0.1%.
        private readonly decimal _makerFeeRate;
        private readonly decimal _takerFeeRate;

        private readonly object _lock = new();
        private long _nextOrderId;
        private decimal _latestBid;
        private decimal _latestAsk;
        private readonly Dictionary<long, MockOrder> _activeOrders = new();
        private readonly Dictionary<long, MockOrder> _completedOrders = new();
        // FIFO of completed-order ids so a long backtest doesn't grow
        // _completedOrders without bound. GridBot only queries the most recent
        // pending order, which is always within this window.
        private readonly Queue<long> _completedOrderInsertion = new();
        private const int CompletedOrdersCap = 1024;
        private decimal _quoteBalance;
        private decimal _baseBalance;

        private readonly decimal _initialBankroll;
        private readonly decimal _slippagePct;

        // Optional exchange filters (LOT_SIZE / PRICE_FILTER / NOTIONAL). When set,
        // orders are floored/rejected exactly as the live BinanceConnectorExecutor
        // does, so a backtest can't fill an order the real exchange would bounce.
        // Null (the default) preserves the historical no-filter behaviour.
        private readonly TradingPair? _filters;

        /// <summary>
        /// Construct an in-memory matching engine.
        /// </summary>
        /// <param name="initialBankroll">Starting quote-asset balance in USDT.</param>
        /// <param name="slippagePct">
        /// One-sided market-order slippage as a decimal fraction (e.g. <c>0.001m</c>
        /// = 10 bps). Buys fill at <c>ask × (1 + slippage)</c>, sells fill at
        /// <c>bid × (1 − slippage)</c>. Limit orders are unaffected — they fill
        /// at the exact price you set, like on a real exchange. Default 0
        /// matches the historical behaviour, but for honest tuning a non-zero
        /// value (5–20 bps for liquid majors) is recommended.
        /// </param>
        public MockBinanceExecutor(decimal initialBankroll = 1_000_000m, decimal slippagePct = 0m, decimal commissionRate = 0.001m,
                                   TradingPair? exchangeFilters = null)
        {
            _initialBankroll = initialBankroll;
            _quoteBalance = initialBankroll;
            _slippagePct = slippagePct < 0m ? 0m : slippagePct;
            var fee = commissionRate < 0m ? 0m : commissionRate;
            _makerFeeRate = fee;
            _takerFeeRate = fee;
            _filters = exchangeFilters;
        }

        public decimal TotalMakerFeesPaid { get; private set; }
        public decimal TotalTakerFeesPaid { get; private set; }
        public int TotalOrdersPlaced { get; private set; }
        public int TotalOrdersFilled { get; private set; }
        public int TotalOrdersCanceled { get; private set; }

        /// <summary>
        /// Evaluate all pending Limit Buy orders against the current tick.
        /// If <paramref name="currentAsk"/> &lt;= order price the order is marked FILLED
        /// and a 0.1 % maker fee is recorded. Filled orders are moved to the
        /// completed dictionary so they no longer participate in future scans.
        /// </summary>
        public void ProcessTick(decimal currentBid, decimal currentAsk)
        {
            lock (_lock)
            {
                _latestBid = currentBid;
                _latestAsk = currentAsk;

                List<long>? filled = null;
                foreach (var kv in _activeOrders)
                {
                    var order = kv.Value;
                    if (order.IsBuy && currentAsk <= order.Price)
                    {
                        // Match live behaviour: Binance debits the full quote
                        // notional but credits base = grossQty * (1-fee). Mirror
                        // that by subtracting the base-asset commission from
                        // the credited quantity AND from the value reported as
                        // ExecutedQuantity, so backtests see the same net
                        // position size as live.
                        var commission = order.Quantity * _makerFeeRate;
                        var netQty = order.Quantity - commission;
                        order.Status = MockOrderStatus.Filled;
                        order.ExecutedQuantity = netQty;
                        order.AveragePrice = order.Price;
                        _baseBalance += netQty;
                        TotalMakerFeesPaid += order.Price * order.Quantity * _makerFeeRate;
                        TotalOrdersFilled++;
                        (filled ??= new()).Add(kv.Key);
                    }
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
        }

        // Insert into _completedOrders with FIFO eviction so the dictionary
        // never grows beyond CompletedOrdersCap entries. Caller must hold _lock.
        private void AddCompletedOrder(long id, MockOrder order)
        {
            _completedOrders[id] = order;
            _completedOrderInsertion.Enqueue(id);
            while (_completedOrderInsertion.Count > CompletedOrdersCap)
            {
                var evict = _completedOrderInsertion.Dequeue();
                _completedOrders.Remove(evict);
            }
        }

        public Task<PlaceLimitResult> PlaceLimitAsync(string symbol, decimal quantity, decimal price, bool isBuy, string? newClientOrderId = null)
        {
            lock (_lock)
            {
                // Apply exchange filters exactly as the live executor would: floor
                // price to the tick and quantity to the lot/notional, rejecting
                // (default result) when nothing valid remains.
                if (_filters is TradingPair f)
                {
                    price = MathHelpers.ClampPrice(f, price);
                    quantity = MathHelpers.ClampQuantity(f, quantity, price);
                    if (price <= 0m || quantity <= 0m)
                        return Task.FromResult(default(PlaceLimitResult));
                }

                if (isBuy)
                {
                    var notional = price * quantity;
                    if (_quoteBalance < notional)
                        return Task.FromResult(default(PlaceLimitResult));

                    _quoteBalance -= notional;
                }

                var id = ++_nextOrderId;
                _activeOrders[id] = new MockOrder
                {
                    OrderId = id,
                    Symbol = symbol,
                    Price = price,
                    Quantity = quantity,
                    IsBuy = isBuy,
                    Status = MockOrderStatus.New,
                    ClientOrderId = newClientOrderId,
                };
                TotalOrdersPlaced++;

                return Task.FromResult(new PlaceLimitResult { OrderId = id, Price = price, Quantity = quantity });
            }
        }

        public Task<PlaceMarketResult> PlaceMarketAsync(string symbol, decimal quantity, bool isBuy)
        {
            lock (_lock)
            {
                // Apply one-sided market slippage on top of the quoted side:
                // buys lift the offer (pay more), sells hit the bid (receive
                // less). Limit fills in ProcessTick still execute at the
                // exact resting price, matching real-exchange semantics.
                var fillPrice = isBuy
                    ? _latestAsk * (1m + _slippagePct)
                    : _latestBid * (1m - _slippagePct);
                decimal returnedQty;

                // Market orders carry no price, but the quantity must still pass
                // LOT_SIZE / MIN_NOTIONAL — a sub-filter market sell (e.g. a tiny
                // bag-cleaning slice) is rejected live, so the mock rejects it too.
                if (_filters is TradingPair f)
                {
                    quantity = MathHelpers.ClampQuantity(f, quantity, fillPrice);
                    if (quantity <= 0m)
                        return Task.FromResult(default(PlaceMarketResult));
                }

                if (isBuy)
                {
                    var notional = fillPrice * quantity;
                    if (_quoteBalance < notional)
                        return Task.FromResult(default(PlaceMarketResult));

                    // Live executor returns net base = grossQty * (1-fee).
                    var commission = quantity * _takerFeeRate;
                    var netQty = quantity - commission;
                    _quoteBalance -= notional;
                    _baseBalance += netQty;
                    returnedQty = netQty;
                }
                else
                {
                    if (_baseBalance < quantity)
                        return Task.FromResult(default(PlaceMarketResult));

                    // Sell commission is taken in the quote asset, so the base
                    // sold is exactly `quantity` but the quote credited is
                    // `quantity * fillPrice * (1-fee)`. PlaceMarketResult.Quantity
                    // mirrors live (the base side, unaffected by sell commission).
                    _baseBalance -= quantity;
                    _quoteBalance += fillPrice * quantity * (1m - _takerFeeRate);
                    returnedQty = quantity;
                }

                TotalTakerFeesPaid += fillPrice * quantity * _takerFeeRate;
                TotalOrdersFilled++;
                TotalOrdersPlaced++;

                var id = ++_nextOrderId;
                AddCompletedOrder(id, new MockOrder
                {
                    OrderId = id,
                    Symbol = symbol,
                    Price = fillPrice,
                    Quantity = quantity,
                    IsBuy = isBuy,
                    Status = MockOrderStatus.Filled,
                    ExecutedQuantity = returnedQty,
                    AveragePrice = fillPrice,
                });

                return Task.FromResult(new PlaceMarketResult { Price = fillPrice, Quantity = returnedQty });
            }
        }

        public Task<bool> CancelOrderAsync(string symbol, long orderId)
        {
            lock (_lock)
            {
                if (_activeOrders.TryGetValue(orderId, out var order))
                {
                    if (order.IsBuy)
                        _quoteBalance += order.Price * order.Quantity;

                    order.Status = MockOrderStatus.Canceled;
                    TotalOrdersCanceled++;
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

        private static OrderQueryResult BuildQueryResult(MockOrder order)
        {
            var status = order.Status switch
            {
                MockOrderStatus.New => OrderStatus.New,
                MockOrderStatus.Filled => OrderStatus.Filled,
                MockOrderStatus.Canceled => OrderStatus.Canceled,
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

        public Task<decimal> GetCurrentTotalEquityAsync(string symbol, decimal markPrice)
        {
            lock (_lock)
            {
                var equity = _quoteBalance + (_baseBalance * markPrice);
                return Task.FromResult(equity);
            }
        }

        /// <summary>
        /// Clear all orders and counters so the executor can be reused for
        /// another backtest run.
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _activeOrders.Clear();
                _completedOrders.Clear();
                _completedOrderInsertion.Clear();
                _nextOrderId = 0;
                _latestBid = 0m;
                _latestAsk = 0m;
                _quoteBalance = _initialBankroll;
                _baseBalance = 0m;
                TotalMakerFeesPaid = 0m;
                TotalTakerFeesPaid = 0m;
                TotalOrdersPlaced = 0;
                TotalOrdersFilled = 0;
                TotalOrdersCanceled = 0;
            }
        }
    }
}
