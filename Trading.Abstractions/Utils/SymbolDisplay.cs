using System;

namespace GripTrader.Core.Utils
{
    /// <summary>
    /// Pure, host-agnostic helpers for presenting a concatenated trading-pair
    /// symbol (Binance has no separator, e.g. "ETHBTC"). Extracted from the
    /// WinUI page so the logic is unit-testable and reachable from the Node
    /// runner too.
    /// </summary>
    public static class SymbolDisplay
    {
        // Checked longest-first so a longer quote asset wins over any shorter
        // suffix it might otherwise contain. These are the quote assets the app
        // actually trades against; extend as needed.
        private static readonly string[] KnownQuoteAssets =
        {
            "FDUSD", "USDT", "USDC", "BUSD", "TUSD", "DAI",
            "EUR", "GBP", "TRY", "BTC", "ETH", "BNB"
        };

        /// <summary>
        /// Best-effort quote-asset extraction. Case-insensitive (symbols are
        /// persisted lower-case in this app, which is exactly why the old
        /// ordinal <c>EndsWith("USDT")</c> always missed). Returns an empty
        /// string when no known quote suffix matches — callers decide the
        /// fallback label.
        /// </summary>
        public static string QuoteAsset(string? symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return string.Empty;
            var s = symbol.Trim().ToUpperInvariant();
            foreach (var q in KnownQuoteAssets)
            {
                // s.Length > q.Length so the base asset is non-empty (a symbol
                // that is *only* the quote asset isn't a real pair).
                if (s.Length > q.Length && s.EndsWith(q, StringComparison.Ordinal))
                    return q;
            }
            return string.Empty;
        }

        /// <summary>
        /// Number of fractional digits implied by a price tick size
        /// (0.01 -> 2, 0.00001 -> 5). Returns 0 for whole-number ticks and
        /// clamps to a sane [0, 8] range. Use this to format prices instead of
        /// the base-asset quantity precision.
        /// </summary>
        public static int PriceDecimals(decimal tickSize)
        {
            if (tickSize <= 0m) return 2; // unknown tick -> sensible default for quote pairs
            var decimals = 0;
            var t = tickSize;
            while (t < 1m && decimals < 8)
            {
                t *= 10m;
                decimals++;
            }
            return decimals;
        }
    }
}
