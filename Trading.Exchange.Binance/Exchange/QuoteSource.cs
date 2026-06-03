using GripTrader.Core.Bot;
using GripTrader.Core.Models;
using System.Collections.Concurrent;

namespace GripTrader.Core.Exchange
{
    public class QuoteSource : IPriceSink, IQuoteSource
    {
        private readonly ConcurrentDictionary<string, BidAsk> _symbolsData = new ConcurrentDictionary<string, BidAsk>();

        public void OnPrice(string symbol, BidAsk bidAsk)
        {
            _symbolsData[symbol] = bidAsk;
        }

        public bool TryGetBestBidAsk(string symbol, out decimal bid, out decimal ask)
        {
            if (!_symbolsData.TryGetValue(symbol, out var bidAsk))
            {
                bid = 0;
                ask = 0;
                return false;
            }

            bid = bidAsk.Bid;
            ask = bidAsk.Ask;
            return true;
        }
    }
}