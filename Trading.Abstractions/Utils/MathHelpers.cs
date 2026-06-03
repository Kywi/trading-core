using GripTrader.Core.Models;
using System;

namespace GripTrader.Core.Utils
{
    public class MathHelpers
    {
        /// <summary>
        /// Floors <paramref name="value"/> down to the nearest multiple of
        /// <paramref name="step"/>.
        /// <para>
        /// Returns <paramref name="value"/> unchanged when <paramref name="step"/>
        /// is zero or negative. This is intentional: Binance can omit
        /// <c>PRICE_FILTER</c> on some MARKET-only symbols, in which case
        /// <see cref="Models.TradingPair.TickSize"/> is 0 and the helper must
        /// pass the value through. <see cref="ClampQuantity"/> and
        /// <see cref="ClampPrice"/> are the public entry points; both reject
        /// the order at higher levels (MinQty / MinPrice / MinNotional) so a
        /// pass-through at zero step does not bypass safety.
        /// </para>
        /// </summary>
        public static decimal FloorToStep(decimal value, decimal step)
        {
            if (step <= 0m) return value;
            var n = Math.Floor(value / step);
            return n * step;
        }

        // Clamp to LOT_SIZE / MARKET_LOT_SIZE and NOTIONAL.
        // Adjust names to your TradingPair model: MinQty, MaxQty, StepSize, MinNotional (if you have it).
        public static decimal ClampQuantity(TradingPair rules, decimal requestedQty, decimal priceEstimate)
        {
            // 1) Lot size
            var q = FloorToStep(requestedQty, rules.StepSize);
            if (q < rules.MinQty) return 0m;
            if (q > rules.MaxQty) q = FloorToStep(rules.MaxQty, rules.StepSize);

            // 2) Min notional (if you carry it in TradingPair; else skip or use 0)
            if (rules.MinNotional > 0m && priceEstimate > 0m)
            {
                // Small safety factor avoids borderline rejections
                if (q * priceEstimate < rules.MinNotional * 1.01m)
                    return 0m;
            }

            // 3) Max notional (quote-value ceiling). Symmetric with the MinNotional
            // gate: floor the quantity down so q*price <= MaxNotional rather than
            // letting Binance reject the order at submit time. If flooring drops it
            // below MinQty there's no valid size, so reject.
            if (rules.MaxNotional > 0m && priceEstimate > 0m && q * priceEstimate > rules.MaxNotional)
            {
                q = FloorToStep(rules.MaxNotional / priceEstimate, rules.StepSize);
                if (q < rules.MinQty) return 0m;
            }
            return q;
        }

        // Floor price to the nearest tick and enforce PRICE_FILTER bounds.
        public static decimal ClampPrice(TradingPair rules, decimal requestedPrice)
        {
            var p = FloorToStep(requestedPrice, rules.TickSize);
            if (rules.MinPrice > 0m && p < rules.MinPrice) return 0m;
            if (rules.MaxPrice > 0m && p > rules.MaxPrice) p = FloorToStep(rules.MaxPrice, rules.TickSize);
            return p;
        }
    }
}
