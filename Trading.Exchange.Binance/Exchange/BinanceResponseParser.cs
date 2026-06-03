using System;
using System.Globalization;
using GripTrader.Core.Models;
using Newtonsoft.Json.Linq;

namespace GripTrader.Core.Exchange
{
    /// <summary>
    /// Pure parsing of Binance REST JSON responses into the engine's result
    /// models. Extracted from <see cref="BinanceConnectorExecutor"/> so the
    /// money-relevant math — base-asset commission netting, average-price,
    /// account-equity summation, and order-status mapping — is unit-testable
    /// without a network connection or the Binance SDK.
    ///
    /// These methods perform no I/O and no logging. <see cref="JObject.Parse"/>
    /// / <see cref="JArray.Parse"/> throw on syntactically invalid JSON; callers
    /// invoke these inside their existing try/catch so a malformed body is logged
    /// and falls back exactly as it did before this class was extracted. Missing
    /// fields do NOT throw — they degrade to the same safe defaults as the
    /// original inline code.
    /// </summary>
    internal static class BinanceResponseParser
    {
        /// <summary>
        /// Parses a MARKET <c>NewOrder</c> (FULL) response. Returns the average
        /// fill price and executed quantity; on a buy, base-asset commissions in
        /// the <c>fills</c> array are netted out of the quantity so it matches the
        /// balance Binance actually credits. Returns <c>default</c> (zeroed) when
        /// nothing executed.
        /// </summary>
        internal static PlaceMarketResult ParseMarketOrder(string json, string baseAsset, bool isBuy)
        {
            var parsed = JObject.Parse(json);
            var executed = parsed.Value<string>("executedQty");
            var cummQuote = parsed.Value<string>("cummulativeQuoteQty");

            if (decimal.TryParse(executed, NumberStyles.Any, CultureInfo.InvariantCulture, out var execQty)
                && execQty > 0m)
            {
                decimal avg = 0m;
                if (decimal.TryParse(cummQuote, NumberStyles.Any, CultureInfo.InvariantCulture, out var quote) && quote > 0m)
                    avg = quote / execQty;

                // On a buy, subtract any base-asset commission from the fills so that
                // EntryQuantity stored in state matches the net balance credited by
                // Binance. (Sell commissions are taken in the quote asset and do not
                // affect qty.)
                if (isBuy && parsed["fills"] is JArray fills)
                {
                    foreach (var fill in fills)
                    {
                        var commAsset = fill.Value<string>("commissionAsset") ?? string.Empty;
                        if (!commAsset.Equals(baseAsset, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var commStr = fill.Value<string>("commission") ?? "0";
                        if (decimal.TryParse(commStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var comm))
                            execQty -= comm;
                    }
                    if (execQty < 0m) execQty = 0m;
                }

                return new PlaceMarketResult { Price = avg, Quantity = execQty };
            }

            return default;
        }

        /// <summary>
        /// Extracts the exchange-assigned <c>orderId</c> from a <c>NewOrder</c>
        /// response. Returns 0 when absent — the caller treats that as a failed
        /// placement.
        /// </summary>
        internal static long ParseNewOrderId(string json)
            => JObject.Parse(json).Value<long>("orderId");

        /// <summary>
        /// Parses a <c>QueryOrder</c> response into the status, gross executed
        /// quantity, average price, and order id, plus the raw <c>side</c> string
        /// (the caller uses it to decide whether a follow-up myTrades commission
        /// netting is required for buys). Quantity is GROSS here — commission
        /// netting needs a separate myTrades call and is applied by the caller.
        /// </summary>
        internal static (OrderQueryResult result, string side) ParseQueryOrder(string json)
        {
            var parsed = JObject.Parse(json);
            var statusText = parsed.Value<string>("status") ?? string.Empty;
            var status = statusText.ToUpperInvariant() switch
            {
                "NEW" => OrderStatus.New,
                "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
                "FILLED" => OrderStatus.Filled,
                "CANCELED" => OrderStatus.Canceled,
                "REJECTED" => OrderStatus.Rejected,
                "EXPIRED" => OrderStatus.Expired,
                _ => OrderStatus.Unknown,
            };

            var resolvedOrderId = parsed.Value<long?>("orderId") ?? 0L;

            decimal avgPrice = 0m;
            var executed = parsed.Value<string>("executedQty");
            var cummQuote = parsed.Value<string>("cummulativeQuoteQty");
            var side = parsed.Value<string>("side") ?? string.Empty;

            if (decimal.TryParse(executed, NumberStyles.Any, CultureInfo.InvariantCulture, out var execQty)
                && execQty > 0m
                && decimal.TryParse(cummQuote, NumberStyles.Any, CultureInfo.InvariantCulture, out var quote)
                && quote > 0m)
            {
                avgPrice = quote / execQty;
            }

            var result = new OrderQueryResult
            {
                Status = status,
                ExecutedQuantity = execQty,
                AveragePrice = avgPrice,
                OrderId = resolvedOrderId,
            };
            return (result, side);
        }

        /// <summary>
        /// Sums the base-asset commission across all trades in a <c>myTrades</c>
        /// response. Trades whose commission was charged in a different asset
        /// (e.g. BNB or the quote asset) are ignored. Returns 0 when there are no
        /// matching trades.
        /// </summary>
        internal static decimal SumBaseAssetCommission(string json, string baseAsset)
        {
            var trades = JArray.Parse(json);
            decimal total = 0m;
            foreach (var trade in trades)
            {
                var commAsset = trade.Value<string>("commissionAsset") ?? string.Empty;
                if (!commAsset.Equals(baseAsset, StringComparison.OrdinalIgnoreCase))
                    continue;
                var commStr = trade.Value<string>("commission") ?? "0";
                if (decimal.TryParse(commStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var comm))
                    total += comm;
            }
            return total;
        }

        /// <summary>
        /// Computes total account equity in the quote asset from an
        /// <c>/api/v3/account</c> response: <c>quote + base * markPrice</c>, where
        /// each asset total is <c>free + locked</c> (pending limit buys lock quote
        /// until they fill/cancel — ignoring locked would understate equity).
        /// Returns 0 when the <c>balances</c> array is missing.
        /// </summary>
        internal static decimal ComputeEquity(string json, string baseAsset, string quoteAsset, decimal markPrice)
        {
            var root = JObject.Parse(json);
            if (root["balances"] is not JArray balances)
                return 0m;

            decimal baseTotal = 0m;
            decimal quoteTotal = 0m;

            foreach (var token in balances)
            {
                var asset = token.Value<string>("asset");
                if (string.IsNullOrWhiteSpace(asset))
                    continue;

                var freeText = token.Value<string>("free") ?? "0";
                if (!decimal.TryParse(freeText, NumberStyles.Any, CultureInfo.InvariantCulture, out var free))
                    free = 0m;

                var lockedText = token.Value<string>("locked") ?? "0";
                if (!decimal.TryParse(lockedText, NumberStyles.Any, CultureInfo.InvariantCulture, out var locked))
                    locked = 0m;

                var total = free + locked;

                if (asset.Equals(baseAsset, StringComparison.OrdinalIgnoreCase))
                    baseTotal = total;
                else if (asset.Equals(quoteAsset, StringComparison.OrdinalIgnoreCase))
                    quoteTotal = total;
            }

            return quoteTotal + (baseTotal * markPrice);
        }

        /// <summary>
        /// Reads <c>serverTime</c> (Unix ms) from a <c>/api/v3/time</c> response.
        /// Returns 0 when absent so the caller keeps its previous offset.
        /// </summary>
        internal static long ParseServerTime(string json)
            => JObject.Parse(json).Value<long?>("serverTime") ?? 0L;
    }
}
