namespace GripTrader.Core.Abstractions
{
    /// <summary>
    /// One funding settlement for a single symbol, surfaced on the multi-tick seam
    /// so a strategy can attribute PnL to funding (Phase-4 reversion-vs-funding split).
    /// <para>
    /// The feeder applies these to the per-leg executors via
    /// <c>MockPerpExecutor.ApplyFunding(symbol, markPrice, rate, ts)</c> BEFORE the
    /// bar's tick, so the wallet move feeds this tick's liquidation decision. The
    /// fields line up one-to-one with that call: <see cref="MarkPrice"/> is the mark
    /// at the funding timestamp (funding settles on mark), <see cref="Rate"/> is the
    /// signed funding rate (positive ⇒ longs pay shorts), and <see cref="TimestampMs"/>
    /// is the funding <c>calc_time</c> already epoch-normalized by the feeder
    /// (<c>&gt;10^13 ? /1000</c>) — the executor performs no epoch math.
    /// </para>
    /// <para>
    /// <see cref="IntervalHours"/> is read <b>straight from the funding archive's
    /// <c>funding_interval_hours</c> column</b> (e.g. 8 or 4, dynamic per symbol) —
    /// never inferred from consecutive-timestamp deltas. It is carried for reporting /
    /// attribution only; the executor is interval-agnostic and applies exactly the
    /// events it is handed.
    /// </para>
    /// <para>All money/rate fields are <see cref="decimal"/> (decimal-only money math).</para>
    /// </summary>
    public readonly struct FundingEvent
    {
        /// <summary>Symbol this funding settles against.</summary>
        public readonly string Symbol;

        /// <summary>Signed funding rate as a decimal fraction (positive ⇒ longs pay shorts).</summary>
        public readonly decimal Rate;

        /// <summary>Mark price at the funding timestamp (funding settles on mark).</summary>
        public readonly decimal MarkPrice;

        /// <summary>Funding <c>calc_time</c> in Unix milliseconds, already epoch-normalized by the feeder.</summary>
        public readonly long TimestampMs;

        /// <summary>Funding interval in hours, read straight from the archive column (never inferred).</summary>
        public readonly int IntervalHours;

        public FundingEvent(string symbol, decimal rate, decimal markPrice, long timestampMs, int intervalHours)
        {
            Symbol = symbol;
            Rate = rate;
            MarkPrice = markPrice;
            TimestampMs = timestampMs;
            IntervalHours = intervalHours;
        }

        public override string ToString()
            => $"Funding {Symbol} rate {Rate} mark {MarkPrice} @ {TimestampMs} ({IntervalHours}h)";
    }
}
