using GripTrader.Core.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace GripTrader.Core.Http
{
    public class PublicService
    {
        private readonly HttpClient _httpClient;

        public PublicService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<Dictionary<string, TradingPair>> RequestForSupportedPairs()
        {
            var response = await _httpClient.GetAsync("https://api.binance.com/api/v3/exchangeInfo");
            // Binance maintenance windows return an HTML error page or a 5xx
            // JSON body; without this throw we silently parsed the bogus
            // response into zero pairs and the bot started up with no rules,
            // failing later with a confusing "Symbol not found" instead of
            // surfacing the underlying exchangeInfo failure.
            response.EnsureSuccessStatusCode();
            var exchangeInfo = JObject.Parse(await response.Content.ReadAsStringAsync());
            var symbols = (JArray?)exchangeInfo["symbols"];
            var pairs = new Dictionary<string, TradingPair>();
            if (symbols == null) return pairs;

            foreach (var s in symbols)
            {
                // 1) must support MARKET on SPOT and be trading
                if (!string.Equals((string?)s["status"], "TRADING", StringComparison.Ordinal)) continue;
                var orderTypes = s["orderTypes"]?.Values<string>()?.ToList();
                if (orderTypes == null || !orderTypes.Contains("MARKET")) continue;
                var permissions = s["permissions"]?.Values<string>()?.ToList(); // optional in some payloads
                if (permissions != null && permissions.Count != 0 && !permissions.Contains("SPOT")) continue;

                var baseAsset = s["baseAsset"]!.ToString().ToLowerInvariant();
                var quoteAsset = s["quoteAsset"]!.ToString().ToLowerInvariant();

                var filters = s["filters"]!.ToList();

                // PRICE_FILTER (optional for MARKET)
                var priceFilter = filters.FirstOrDefault(f => f["filterType"]!.Value<string>() == "PRICE_FILTER");
                var tickSize = priceFilter != null ? priceFilter["tickSize"]!.Value<decimal>() : 0m;
                var minPrice = priceFilter != null ? priceFilter["minPrice"]!.Value<decimal>() : 0m;
                var maxPrice = priceFilter != null ? priceFilter["maxPrice"]!.Value<decimal>() : 0m;

                // MARKET orders must satisfy BOTH LOT_SIZE and MARKET_LOT_SIZE.
                // Merge to the most restrictive value so the quantity is valid for either order type.
                var mlot = filters.FirstOrDefault(f => f["filterType"]!.Value<string>() == "MARKET_LOT_SIZE");
                var lot = filters.FirstOrDefault(f => f["filterType"]!.Value<string>() == "LOT_SIZE");
                if (mlot == null && lot == null) continue; // cannot trade without quantity rules

                decimal stepSize, minQty, maxQty;
                if (mlot != null && lot != null)
                {
                    stepSize = Math.Max(lot["stepSize"]!.Value<decimal>(), mlot["stepSize"]!.Value<decimal>());
                    minQty = Math.Max(lot["minQty"]!.Value<decimal>(), mlot["minQty"]!.Value<decimal>());
                    maxQty = Math.Min(lot["maxQty"]!.Value<decimal>(), mlot["maxQty"]!.Value<decimal>());
                }
                else
                {
                    var qtyFilter = (mlot ?? lot)!;
                    stepSize = qtyFilter["stepSize"]!.Value<decimal>();
                    minQty = qtyFilter["minQty"]!.Value<decimal>();
                    maxQty = qtyFilter["maxQty"]!.Value<decimal>();
                }

                // NOTIONAL (preferred) or MIN_NOTIONAL (legacy)
                decimal minNotional = 0m, maxNotional = 0m;
                var notional = filters.FirstOrDefault(f => f["filterType"]!.Value<string>() == "NOTIONAL");
                if (notional != null)
                {
                    var applyMinToMarket = notional["applyMinToMarket"]?.Value<bool>() ?? false;
                    var applyMaxToMarket = notional["applyMaxToMarket"]?.Value<bool>() ?? false;
                    if (applyMinToMarket) minNotional = notional["minNotional"]!.Value<decimal>();
                    if (applyMaxToMarket) maxNotional = notional["maxNotional"]!.Value<decimal>();
                }
                else
                {
                    var minNotionalFilter = filters.FirstOrDefault(f => f["filterType"]!.Value<string>() == "MIN_NOTIONAL");
                    if (minNotionalFilter != null)
                    {
                        var applyToMarket = minNotionalFilter["applyToMarket"]?.Value<bool>() ?? false;
                        if (applyToMarket) minNotional = minNotionalFilter["minNotional"]!.Value<decimal>();
                    }
                }

                var key = (baseAsset + quoteAsset);
                pairs[key] = new TradingPair(baseAsset, quoteAsset, tickSize, minPrice, maxPrice, minQty, maxQty,
                    stepSize, minNotional, maxNotional);
            }
            return pairs;
        }
    }
}