namespace GripTrader.Core.Models
{
    /// <summary>
    /// The two figures the liquidation decision is made from, returned by
    /// <c>IOrderExecutor.GetEquityBreakdownAsync</c>: the position's current
    /// <see cref="Equity"/> and its <see cref="MaintenanceMargin"/>.
    /// <para>
    /// For a perp leg under isolated margin, <see cref="Equity"/> is the
    /// allocated wallet plus the leg's unrealized price PnL plus its realized
    /// funding, and <see cref="MaintenanceMargin"/> is the notional-based
    /// maintenance requirement; a leg is liquidatable when
    /// <see cref="Equity"/> &lt; <see cref="MaintenanceMargin"/>. The account-level
    /// (cross) aggregation — summing <c>(Equity, MaintenanceMargin)</c> across
    /// legs — is performed by the multi-symbol feeder/harness that owns the
    /// per-leg executors, not by the single-symbol executor itself.
    /// </para>
    /// <para>
    /// The default <c>IOrderExecutor</c> implementation returns
    /// <c>(GetCurrentTotalEquityAsync(...), 0m)</c> — a spot executor has no
    /// maintenance margin — so existing implementers stay byte-identical.
    /// All money math is <see cref="decimal"/>.
    /// </para>
    /// </summary>
    public readonly struct EquityBreakdown
    {
        public readonly decimal Equity;
        public readonly decimal MaintenanceMargin;

        public EquityBreakdown(decimal equity, decimal maintenanceMargin)
        {
            Equity = equity;
            MaintenanceMargin = maintenanceMargin;
        }

        public override string ToString()
        {
            return $"Equity {Equity} | Maint {MaintenanceMargin}";
        }
    }
}
