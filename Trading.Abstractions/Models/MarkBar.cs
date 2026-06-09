namespace GripTrader.Core.Models
{
    /// <summary>
    /// A mark-price OHLC bar for one symbol over one interval, distinct from the
    /// last-trade fill quote (<see cref="BidAsk"/>). Binance marks unrealized PnL
    /// and triggers liquidation off the <b>mark</b> price, not the last trade —
    /// so a perp backtest needs both: the fill quote drives order matching, the
    /// mark bar drives PnL marking (<see cref="Close"/>) and the liquidation probe
    /// (<see cref="High"/> for shorts, <see cref="Low"/> for longs — the adverse
    /// intra-bar extreme).
    /// <para>
    /// All prices are <see cref="decimal"/> (decimal-only money math).
    /// <see cref="TimestampMs"/> is Unix milliseconds and is assumed
    /// <b>already epoch-normalized</b> (the <c>&gt;10^13 ? /1000</c> rule) by the
    /// feeder — the executor performs no epoch math.
    /// </para>
    /// </summary>
    public readonly struct MarkBar
    {
        public readonly decimal Open;
        public readonly decimal High;
        public readonly decimal Low;
        public readonly decimal Close;

        /// <summary>Source timestamp of the bar in Unix milliseconds (already normalized by the feeder).</summary>
        public readonly long TimestampMs;

        public MarkBar(decimal open, decimal high, decimal low, decimal close, long timestampMs)
        {
            Open = open;
            High = high;
            Low = low;
            Close = close;
            TimestampMs = timestampMs;
        }

        public override string ToString()
        {
            return $"Mark O {Open} H {High} L {Low} C {Close} @ {TimestampMs}";
        }
    }
}
