using GripTrader.Core.Bot;
using Binance.Common;
using GripTrader.Core.Models;
using Binance.Spot;
using GripTrader.Core.Utils;
using System;
using System.Threading;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Log;

namespace GripTrader.Core.Exchange
{
    public class BinanceConnectorExecutor : IOrderExecutor
    {
        private readonly IBinanceTradeClient _trade;
        private readonly int _recvWindowMs;
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly Dictionary<string, TradingPair> _tradingPairs; // keys lower-case
        private readonly IQuoteSource _quotes;
        private readonly HttpClient _http; // so we can dispose

        // serverTime - localTime, in ms, learned from GET /api/v3/time. Applied to
        // the timestamps of the hand-rolled signed calls (myTrades / account) so a
        // drifting VPS clock doesn't trip Binance -1021. NOTE: the Binance.Spot SDK
        // stamps order/query/cancel timestamps from the local clock internally with
        // no offset hook, so those still rely on the host clock being NTP-synced.
        private long _serverTimeOffsetMs;

        public BinanceConnectorExecutor(
            string apiKey,
            string apiSecret,
            bool useTestnet,
            int recvWindowMs,
            Dictionary<string, TradingPair> tradingPairs,
            IQuoteSource quotes)
        {
            _recvWindowMs = ClampRecvWindow(recvWindowMs);
            _apiKey = apiKey;
            _apiSecret = apiSecret;
            _tradingPairs = tradingPairs ?? throw new ArgumentNullException(nameof(tradingPairs));
            _quotes = quotes ?? throw new ArgumentNullException(nameof(quotes));

            var baseUrl = useTestnet ? "https://testnet.binance.vision" : "https://api.binance.com";
            _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            _trade = new SpotAccountTradeClient(new SpotAccountTrade(
                httpClient: _http,
                signatureService: new BinanceHmac(apiSecret),
                apiKey: apiKey,
                baseUrl: baseUrl));
        }

        /// <summary>
        /// Test seam: injects a fake <see cref="IBinanceTradeClient"/> so the
        /// order/query/cancel error-path control flow can be exercised offline.
        /// The signed REST helpers (server-time, myTrades, account) hang off
        /// <see cref="_http"/>, which is created but never hit by the paths these
        /// tests drive; credentials are unused.
        /// </summary>
        internal BinanceConnectorExecutor(
            IBinanceTradeClient trade,
            int recvWindowMs,
            Dictionary<string, TradingPair> tradingPairs,
            IQuoteSource quotes)
        {
            _recvWindowMs = ClampRecvWindow(recvWindowMs);
            _apiKey = string.Empty;
            _apiSecret = string.Empty;
            _tradingPairs = tradingPairs ?? throw new ArgumentNullException(nameof(tradingPairs));
            _quotes = quotes ?? throw new ArgumentNullException(nameof(quotes));
            _trade = trade ?? throw new ArgumentNullException(nameof(trade));
            _http = new HttpClient();
        }

        /// <summary>
        /// Clamps a configured recvWindow into Binance's accepted range. Values
        /// below 1000 ms fall back to the 5000 ms default; values above 60000 ms
        /// are capped at 60000 — Binance rejects recvWindow &gt; 60000 outright
        /// (the value was previously only floored, so an over-large setting would
        /// make every signed request fail). (B14)
        /// </summary>
        internal static int ClampRecvWindow(int recvWindowMs)
        {
            if (recvWindowMs < 1000) return 5000;
            if (recvWindowMs > 60000) return 60000;
            return recvWindowMs;
        }

        /// <summary>serverTime - localTime, the offset added to outgoing signed timestamps.</summary>
        internal static long ComputeServerTimeOffset(long serverTimeMs, long localUnixMs) => serverTimeMs - localUnixMs;

        /// <summary>Local Unix-ms clock adjusted by the learned server-time offset.</summary>
        private long Timestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Interlocked.Read(ref _serverTimeOffsetMs);

        /// <summary>
        /// Fetches Binance server time (public, unsigned) and caches the offset
        /// applied to the executor's hand-rolled signed requests. Best-effort: on
        /// any failure the previous offset (default 0 = local clock) is kept.
        /// Call at startup and periodically, or after a -1021 timestamp rejection.
        /// </summary>
        /// <summary>The current server-time offset in ms (serverTime - localTime).</summary>
        internal long ServerTimeOffsetMs => Interlocked.Read(ref _serverTimeOffsetMs);

        public async Task<bool> SyncServerTimeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var resp = await _http.GetAsync("/api/v3/time", cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Logger.LogError($"ConnectorExecutor: server-time fetch failed Status={(int)resp.StatusCode}; keeping offset {Interlocked.Read(ref _serverTimeOffsetMs)}ms");
                    return false;
                }

                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var serverTime = BinanceResponseParser.ParseServerTime(json);
                if (serverTime <= 0)
                    return false;

                var offset = ComputeServerTimeOffset(serverTime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Interlocked.Exchange(ref _serverTimeOffsetMs, offset);
                Logger.LogInformation($"ConnectorExecutor: server-time offset = {offset}ms (serverTime={serverTime})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "ConnectorExecutor: server-time sync failed; using local clock");
                return false;
            }
        }

        /// <summary>Re-sync the server-time offset in the background when a request reports -1021.</summary>
        private void ResyncIfClockError(int code)
        {
            if (code == -1021)
            {
                Logger.LogError("ConnectorExecutor: -1021 timestamp outside recvWindow; re-syncing server time.");
                _ = SyncServerTimeAsync();
            }
        }

        public async Task<PlaceMarketResult> PlaceMarketAsync(string symbol, decimal quantity, bool isBuy)
        {
            try
            {
                // 1) Lookup rules
                var key = Symbols.Key(symbol);
                TradingPair rules;
                if (!_tradingPairs.TryGetValue(key, out rules))
                {
                    Logger.LogError($"ConnectorExecutor: no rules for {symbol}");
                    return default;
                }

                // 2) Get price estimate for notional check
                decimal bid, ask;
                if (!_quotes.TryGetBestBidAsk(symbol, out bid, out ask))
                {
                    Logger.LogError($"ConnectorExecutor: no quote for {symbol}");
                    return default;
                }
                var priceEstimate = isBuy ? ask : bid;

                // 3) Clamp quantity
                var clamped = MathHelpers.ClampQuantity(rules, quantity, priceEstimate);
                if (clamped <= 0m)
                {
                    Logger.LogError($"ConnectorExecutor: qty rejected by filters. Requested={quantity} Clamped={clamped}");
                    return default;
                }

                // 4) Place MARKET
                var json = await _trade.NewOrderAsync(
                                wireSymbol: Symbols.Wire(symbol),
                                isBuy: isBuy,
                                isMarket: true,
                                quantity: clamped,
                                price: null,
                                newClientOrderId: null,
                                recvWindow: _recvWindowMs);

                return BinanceResponseParser.ParseMarketOrder(json, rules.Base ?? string.Empty, isBuy);
            }
            catch (BinanceClientException bex)
            {
                Logger.LogError($"ConnectorExecutor: market order rejected {symbol} side={(isBuy ? "BUY" : "SELL")} qty={quantity} code={bex.Code} status={bex.StatusCode} msg={bex.Message}");
                ResyncIfClockError(bex.Code);
                return default;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"ConnectorExecutor: order failed {symbol}");
                return default;
            }
        }

        public async Task<PlaceLimitResult> PlaceLimitAsync(string symbol, decimal quantity, decimal price, bool isBuy, string? newClientOrderId = null)
        {
            try
            {
                var key = Symbols.Key(symbol);
                if (!_tradingPairs.TryGetValue(key, out var rules))
                {
                    Logger.LogError($"ConnectorExecutor: no rules for {symbol}");
                    return default;
                }

                var clampedPrice = MathHelpers.ClampPrice(rules, price);
                if (clampedPrice <= 0m)
                {
                    Logger.LogError($"ConnectorExecutor: price rejected by filters. Requested={price}");
                    return default;
                }

                var clamped = MathHelpers.ClampQuantity(rules, quantity, clampedPrice);
                if (clamped <= 0m)
                {
                    Logger.LogError($"ConnectorExecutor: qty rejected by filters. Requested={quantity} Clamped={clamped}");
                    return default;
                }

                var json = await _trade.NewOrderAsync(
                                wireSymbol: Symbols.Wire(symbol),
                                isBuy: isBuy,
                                isMarket: false,
                                quantity: clamped,
                                price: clampedPrice,
                                newClientOrderId: newClientOrderId,
                                recvWindow: _recvWindowMs);

                var orderId = BinanceResponseParser.ParseNewOrderId(json);
                if (orderId <= 0)
                {
                    Logger.LogError($"ConnectorExecutor: limit order returned no orderId");
                    return default;
                }

                return new PlaceLimitResult { OrderId = orderId, Price = clampedPrice, Quantity = clamped };
            }
            catch (BinanceClientException bex)
            {
                Logger.LogError($"ConnectorExecutor: limit order rejected {symbol} side={(isBuy ? "BUY" : "SELL")} qty={quantity} price={price} code={bex.Code} status={bex.StatusCode} msg={bex.Message}");
                ResyncIfClockError(bex.Code);
                return default;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"ConnectorExecutor: limit order failed {symbol}");
                return default;
            }
        }

        public async Task<OrderQueryResult> QueryOrderAsync(string symbol, long orderId)
        {
            try
            {
                var json = await _trade.QueryOrderByIdAsync(Symbols.Wire(symbol), orderId, _recvWindowMs);
                return await ParseQueryOrderResponseAsync(symbol, json).ConfigureAwait(false);
            }
            catch (BinanceClientException bex)
            {
                Logger.LogError($"ConnectorExecutor: query order rejected {symbol} orderId={orderId} code={bex.Code} status={bex.StatusCode} msg={bex.Message}");
                ResyncIfClockError(bex.Code);
                return default;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"ConnectorExecutor: query order failed {symbol} orderId={orderId}");
                return default;
            }
        }

        /// <summary>Binance error code for "Order does not exist".</summary>
        internal const int OrderNotFoundCode = -2013;

        /// <summary>
        /// Maps a Binance client-error code to a query status. ONLY the
        /// "order does not exist" code (-2013) means the order is affirmatively
        /// absent (<see cref="OrderStatus.Unknown"/>); every other client code
        /// (rate limit -1003, bad timestamp -1021, etc.) is transient and must
        /// not be read as "never placed", or the caller would mint a duplicate.
        /// </summary>
        internal static OrderStatus ClassifyByErrorCode(int code)
            => code == OrderNotFoundCode ? OrderStatus.Unknown : OrderStatus.TransientError;

        public async Task<OrderQueryResult> QueryOrderByClientIdAsync(string symbol, string clientOrderId)
        {
            try
            {
                var json = await _trade.QueryOrderByClientIdAsync(Symbols.Wire(symbol), clientOrderId, _recvWindowMs);
                return await ParseQueryOrderResponseAsync(symbol, json).ConfigureAwait(false);
            }
            catch (BinanceClientException bex)
            {
                // -2013 = "order does not exist" => the placement never reached
                // the exchange; safe to report Unknown so the caller mints fresh.
                // Any other client error is transient and must NOT be mistaken
                // for "not found" (that would duplicate the buy).
                var status = ClassifyByErrorCode(bex.Code);
                Logger.LogWarning($"ConnectorExecutor: query by clientOrderId {symbol} clientOrderId={clientOrderId} code={bex.Code} => {status} ({bex.Message})");
                ResyncIfClockError(bex.Code);
                return new OrderQueryResult { Status = status };
            }
            catch (Exception ex)
            {
                // Network timeout / server error / deserialization — transient.
                // Returning Unknown here would risk a duplicate order, so the
                // caller keeps the clientOrderId and retries the query.
                Logger.LogWarning($"ConnectorExecutor: transient failure querying clientOrderId {symbol} clientOrderId={clientOrderId} ({ex.Message})");
                return new OrderQueryResult { Status = OrderStatus.TransientError };
            }
        }

        private async Task<OrderQueryResult> ParseQueryOrderResponseAsync(string symbol, string json)
        {
            var (result, side) = BinanceResponseParser.ParseQueryOrder(json);

            // For BUY orders, the gross executedQty from /api/v3/order does NOT
            // include fee data - we must call /api/v3/myTrades separately to
            // sum the base-asset commissions and net the quantity. Without this
            // step, EntryQuantity would overstate the actual balance credited
            // by Binance and a later sell of the full quantity would fail with
            // "insufficient balance". Sells take commission in the quote asset
            // so the base quantity is unaffected - skip the extra call there.
            if (result.ExecutedQuantity > 0m && result.OrderId > 0 && side.Equals("BUY", StringComparison.OrdinalIgnoreCase))
            {
                var commission = await FetchBuyCommissionInBaseAsync(symbol, result.OrderId).ConfigureAwait(false);
                if (commission > 0m)
                {
                    var net = result.ExecutedQuantity - commission;
                    if (net < 0m) net = 0m;
                    result.ExecutedQuantity = net;
                }
            }

            return result;
        }

        /// <summary>
        /// Sums the base-asset commission across all trades belonging to an
        /// order via /api/v3/myTrades. Returns 0 if no trades, the call fails,
        /// or rules can't be resolved - callers fall back to the gross
        /// quantity in those cases (same behaviour as before this method
        /// existed).
        /// </summary>
        private async Task<decimal> FetchBuyCommissionInBaseAsync(string symbol, long orderId)
        {
            try
            {
                var key = Symbols.Key(symbol);
                if (!_tradingPairs.TryGetValue(key, out var rules))
                    return 0m;

                var baseAsset = rules.Base?.ToUpperInvariant() ?? string.Empty;
                if (string.IsNullOrEmpty(baseAsset))
                    return 0m;

                var timestamp = Timestamp();
                var query = $"symbol={Symbols.Wire(symbol)}&orderId={orderId}&timestamp={timestamp}&recvWindow={_recvWindowMs}";
                var signature = SignatureHelper.Sign(query, _apiSecret);

                using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v3/myTrades?{query}&signature={signature}");
                req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Logger.LogError($"ConnectorExecutor: myTrades query failed {symbol} orderId={orderId} Status={(int)resp.StatusCode}");
                    return 0m;
                }

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return BinanceResponseParser.SumBaseAssetCommission(json, baseAsset);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"ConnectorExecutor: myTrades fetch failed {symbol} orderId={orderId}");
                return 0m;
            }
        }

        public async Task<bool> CancelOrderAsync(string symbol, long orderId)
        {
            try
            {
                await _trade.CancelOrderAsync(Symbols.Wire(symbol), orderId, _recvWindowMs);
                return true;
            }
            catch (BinanceClientException bex)
            {
                Logger.LogError($"ConnectorExecutor: cancel order rejected {symbol} orderId={orderId} code={bex.Code} status={bex.StatusCode} msg={bex.Message}");
                ResyncIfClockError(bex.Code);
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"ConnectorExecutor: cancel order failed {symbol} orderId={orderId}");
                return false;
            }
        }

        public async Task<decimal> GetCurrentTotalEquityAsync(string symbol, decimal markPrice)
        {
            try
            {
                var key = Symbols.Key(symbol);
                if (!_tradingPairs.TryGetValue(key, out var rules))
                {
                    Logger.LogError($"ConnectorExecutor: no rules for {symbol} while fetching equity");
                    return 0m;
                }

                var timestamp = Timestamp();
                var query = $"timestamp={timestamp}&recvWindow={_recvWindowMs}";
                var signature = SignatureHelper.Sign(query, _apiSecret);

                using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v3/account?{query}&signature={signature}");
                req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);

                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    Logger.LogError($"ConnectorExecutor: equity query failed for {symbol}. Status={(int)resp.StatusCode}");
                    return 0m;
                }

                // Account total per asset = free + locked. Pending limit buys
                // lock quote until the order fills or cancels; ignoring locked
                // would under-state equity by exactly the open-orders notional
                // and shrink dynamic-compounding sizing.
                var json = await resp.Content.ReadAsStringAsync();
                return BinanceResponseParser.ComputeEquity(json, rules.Base ?? string.Empty, rules.Quote ?? string.Empty, markPrice);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogError(ex, $"ConnectorExecutor: equity HTTP error for {symbol}");
                return 0m;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"ConnectorExecutor: equity query unexpected error for {symbol}");
                return 0m;
            }
        }

        public void Dispose() { _http.Dispose(); }
    }
}