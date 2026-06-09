using GripTrader.Core.Models;

namespace GripTrader.Core.Abstractions
{
    /// <summary>
    /// One leg's per-tick state on the multi-symbol seam: the last-trade fill quote
    /// that drives order matching and the full mark <see cref="MarkBar"/> (O/H/L/C)
    /// that drives PnL marking and the intra-bar liquidation probe — plus a staleness
    /// flag. A small <c>readonly struct</c> so the feeder's reused, fixed-leg-order
    /// buffer is a contiguous <see cref="MultiTickLeg"/>[] (no per-tick dictionary).
    /// <para>
    /// <see cref="IsStale"/> is <c>true</c> when this leg had <b>no fresh bar</b> at
    /// the merged timestamp: it then carries the leg's <b>last-known</b> fill quote and
    /// mark, so the executor's open position keeps marking and liquidating, but the
    /// strategy MUST exclude a stale leg from the spread/z computation (no forward-fill
    /// into the spread — the #1 missing-bar leak). When any participating leg is stale
    /// the feeder also reports <c>closeLegIndex = -1</c>.
    /// </para>
    /// <para>
    /// The <see cref="MultiTickLeg"/>[] is exposed as an
    /// <c>IReadOnlyList&lt;MultiTickLeg&gt;</c> on the seam and is a
    /// <b>feeder-owned, reused buffer — DO NOT RETAIN</b> it past the
    /// <c>OnBacktestTickAsync</c> call (copy out anything you need).
    /// </para>
    /// <para>All prices are <see cref="decimal"/> (decimal-only money math).</para>
    /// </summary>
    public readonly struct MultiTickLeg
    {
        /// <summary>Symbol of this leg (matches the fixed leg order in the feeder).</summary>
        public readonly string Symbol;

        /// <summary>Last-trade fill quote driving order matching (bid == ask on kline data).</summary>
        public readonly BidAsk FillQuote;

        /// <summary>Full mark OHLC bar: Close marks PnL; High/Low feed the liquidation probe.</summary>
        public readonly MarkBar Mark;

        /// <summary>True when this leg had no fresh bar at the merged T (quote/mark are last-known; exclude from the spread).</summary>
        public readonly bool IsStale;

        public MultiTickLeg(string symbol, BidAsk fillQuote, MarkBar mark, bool isStale)
        {
            Symbol = symbol;
            FillQuote = fillQuote;
            Mark = mark;
            IsStale = isStale;
        }

        public override string ToString()
            => $"{Symbol} fill {FillQuote.Bid}/{FillQuote.Ask} {Mark}{(IsStale ? " STALE" : "")}";
    }
}
