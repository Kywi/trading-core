namespace GripTrader.Core.Models
{
    public struct BidAsk
    {
        public readonly decimal Bid;
        public readonly decimal Ask;

        /// <summary>
        /// Source timestamp of the quote in Unix milliseconds. Set by the
        /// historical feeder during backtests so trade events can be stamped
        /// with bar/tick time rather than wall-clock. Live websocket path
        /// leaves this at 0; consumers fall back to <c>DateTime.UtcNow</c>.
        /// </summary>
        public readonly long TimestampMs;

        public BidAsk(decimal bid, decimal ask)
        {
            Bid = bid;
            Ask = ask;
            TimestampMs = 0;
        }

        public BidAsk(decimal bid, decimal ask, long timestampMs)
        {
            Bid = bid;
            Ask = ask;
            TimestampMs = timestampMs;
        }

        public override string ToString()
        {
            return $" Bid {Bid} \t\t| Ask {Ask}\n";
        }
    }
}
