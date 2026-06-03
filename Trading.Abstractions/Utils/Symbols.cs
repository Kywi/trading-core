using System;

namespace GripTrader.Core.Utils
{
    /// <summary>
    /// Centralised case-normalisation for trading-pair symbols. Two
    /// distinct conventions exist in this codebase:
    ///
    /// - <see cref="Wire"/> (UPPER-case): what Binance REST and WebSocket
    ///   APIs expect on the wire. Use whenever the symbol is sent to or
    ///   echoed back from Binance.
    /// - <see cref="Key"/> (lower-case): the dictionary key used in
    ///   <c>Dictionary&lt;string, TradingPair&gt;</c> built by
    ///   <c>PublicService</c>. Use whenever you look the pair up.
    ///
    /// Mixing the two silently misses the lookup (the default Dictionary
    /// uses ordinal case-sensitive comparison), which is the historical
    /// source of "Symbols were not found in the supported trading pairs"
    /// at startup when the user typed an upper-case symbol.
    /// </summary>
    public static class Symbols
    {
        public static string Wire(string symbol) =>
            string.IsNullOrEmpty(symbol) ? string.Empty : symbol.ToUpperInvariant();

        public static string Key(string symbol) =>
            string.IsNullOrEmpty(symbol) ? string.Empty : symbol.ToLowerInvariant();
    }
}
