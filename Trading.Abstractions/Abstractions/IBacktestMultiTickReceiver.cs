using System.Collections.Generic;
using System.Threading.Tasks;

namespace GripTrader.Core.Abstractions
{
    /// <summary>
    /// The multi-symbol sibling of <see cref="IBacktestTickReceiver"/> (which is NOT
    /// reshaped). The multi-symbol feeder pushes one synchronized N-leg tick into
    /// whatever multi-asset strategy is under test, with no knowledge of the concrete
    /// strategy type. A market-neutral pairs strategy implements this; the seam carries
    /// no pairs concept (no spread/z/hedge) — only a fixed-leg-order list of per-symbol
    /// ticks, a bar-close flag, the funding events already applied, and per-leg
    /// staleness.
    /// <para>
    /// <b>isUptrend is deliberately absent</b> from this seam — a market-neutral pair
    /// has no single long-filter.
    /// </para>
    /// <para>
    /// <b>Hot-path no-retain contract:</b> <paramref name="legs"/> and
    /// <paramref name="dueFunding"/> are <b>feeder-owned, REUSED buffers</b> — the same
    /// instances are handed back on every tick, re-filled in place. The receiver MUST
    /// NOT retain either reference past the returned task; copy out anything it needs to
    /// keep. This avoids a per-tick allocation on the replay hot path.
    /// </para>
    /// </summary>
    public interface IBacktestMultiTickReceiver
    {
        /// <summary>
        /// Called exactly once per merged timestamp T, AFTER the feeder has (1) applied
        /// every due <see cref="FundingEvent"/> to the per-leg executors, (2) run each
        /// leg's <c>ProcessTick</c> in fixed leg order, and (3) computed the advisory
        /// cross-margin flag — so any resting-limit fill or liquidation from this tick
        /// is already visible when the strategy queries the executors.
        /// </summary>
        /// <param name="legs">
        /// Per-leg ticks in <b>fixed leg order</b> (index 0..N-1, the construction
        /// order). Feeder-owned, REUSED buffer — <b>DO NOT RETAIN</b>. A leg with
        /// <c>IsStale==true</c> carries its last-known quote/mark and must be excluded
        /// from the spread.
        /// </param>
        /// <param name="closeLegIndex">
        /// <c>&gt;=0</c> ⇒ a bar-close-aligned tick where the spread signal may be
        /// computed; the value is the close/anchor leg index (leg 0 in the aligned
        /// single-interval case). <c>-1</c> ⇒ no leg closed cleanly here (an
        /// incomplete or stale close set) ⇒ the strategy must not touch the spread.
        /// </param>
        /// <param name="dueFunding">
        /// The funding events that ACTUALLY SETTLED (moved a wallet) on the executors
        /// BEFORE this call (for attribution) — events that were due but no-op'd, e.g.
        /// funding on the bar that first opens a position, are NOT listed. A reused list,
        /// empty on the common tick. <b>DO NOT RETAIN.</b>
        /// </param>
        /// <param name="accountLiquidatable">
        /// Advisory cross-margin flag: <c>true</c> only under
        /// <c>MarginMode.Cross</c> when ΣEquity &lt; ΣMaintenance across legs.
        /// Always <c>false</c> under isolated margin (each leg liquidates itself in its
        /// own <c>ProcessTick</c>). Core does NOT force-close — the position lifecycle
        /// is strategy-specific.
        /// </param>
        Task OnBacktestTickAsync(
            IReadOnlyList<MultiTickLeg> legs,
            int closeLegIndex,
            IReadOnlyList<FundingEvent> dueFunding,
            bool accountLiquidatable);
    }
}
