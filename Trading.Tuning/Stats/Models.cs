using System;

namespace GripTrader.Tuner.Stats
{
    /// <summary>
    /// One round-trip trade — match of a TradeOpened to its TradeClosed (or
    /// full BagCleaned). Partial bag-cleanings are recorded separately as
    /// <see cref="CleaningRecord"/>; they don't terminate a position.
    /// </summary>
    public sealed class TradeRecord
    {
        public int PositionId { get; set; }
        public long OpenTimestampMs { get; set; }
        public long? CloseTimestampMs { get; set; }
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal? ExitPrice { get; set; }
        public decimal? RealizedProfit { get; set; }
        public string OpenSource { get; set; } = "";
        public string? CloseSource { get; set; }
        public bool ClosedByBagCleaning { get; set; }

        public bool IsClosed => CloseTimestampMs.HasValue;
        public long? HoldMs => CloseTimestampMs - OpenTimestampMs;
    }

    /// <summary>Single point of the equity curve, sampled either at trade events or daily boundaries.</summary>
    public sealed class EquityPoint
    {
        public long TimestampMs { get; set; }
        public decimal RealizedProfit { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal ColdStorage { get; set; }
        public decimal CleanedLosses { get; set; }
        public int OpenPositions { get; set; }
        public string Reason { get; set; } = "";

        /// <summary>
        /// Mark-to-market total P&amp;L: realized + unrealized. Cold storage is a
        /// PARTITION of realized profit (GridBot moves swept profit into cold
        /// storage WITHOUT deducting it from <c>_totalRealizedProfit</c>), so it
        /// is already inside <see cref="RealizedProfit"/> and must NOT be added
        /// again — doing so double-counted swept profit and inflated every
        /// downstream return/risk metric whenever profit-sweep was enabled.
        /// Cleaned losses are likewise already netted out of realized profit.
        /// </summary>
        public decimal NetPnL => RealizedProfit + UnrealizedPnL;
    }

    public sealed class CleaningRecord
    {
        public int PositionId { get; set; }
        public long TimestampMs { get; set; }
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal CleaningCost { get; set; }
        public string Source { get; set; } = "";
    }

    public static class TimeUtil
    {
        public static DateTime FromUnixMs(long ms) =>
            DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

        public static string FormatIso(long ms) =>
            FromUnixMs(ms).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    }
}
