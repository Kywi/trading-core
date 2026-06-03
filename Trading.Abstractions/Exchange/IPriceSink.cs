using GripTrader.Core.Models;

namespace GripTrader.Core.Exchange
{
    /// <summary>
    /// Generic sink for real‑time best bid/ask updates. Implement this in the
    /// strategy layer (e.g. GridBot/QuoteSource) or in tests to inject synthetic ticks.
    /// </summary>
    public interface IPriceSink
    {
        void OnPrice(string symbol, BidAsk bidAsk);
    }
}
