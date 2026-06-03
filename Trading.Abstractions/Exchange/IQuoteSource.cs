namespace GripTrader.Core.Exchange
{
    public interface IQuoteSource
    {
        bool TryGetBestBidAsk(string symbol, out decimal bid, out decimal ask);
    }
}