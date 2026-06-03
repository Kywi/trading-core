namespace GripTrader.Core.Models
{
    // for quantity (baseAsset) the precision will be stepSize.
    // for quoteQty (quoteAsset) it will be tickSize.
    public readonly struct TradingPair
    {
        public string Base { get; }
        public string Quote { get; }

        // Price (useful for future LIMIT orders)
        public decimal TickSize { get; }
        public decimal MinPrice { get; }
        public decimal MaxPrice { get; }

        // Quantity (use MARKET_LOT_SIZE if it exists, else LOT_SIZE)
        public decimal StepSize { get; }
        public decimal MinQty { get; }
        public decimal MaxQty { get; }

        // Notional (quote value); 0 means "not applied to MARKET" for this symbol
        public decimal MinNotional { get; }
        public decimal MaxNotional { get; } // optional

        public TradingPair(string @base, string quote, decimal tickSize, decimal minPrice, decimal maxPrice, decimal minQty, decimal maxQty, decimal stepSize, decimal minNotional, decimal maxNotional)
        {
            Base = @base;
            Quote = quote;
            StepSize = stepSize;
            MinQty = minQty;
            MaxQty = maxQty;
            MinNotional = minNotional;
            MaxNotional = maxNotional;
            TickSize = tickSize;
            MinPrice = minPrice;
            MaxPrice = maxPrice;
        }

        public override bool Equals(object? obj)
        {
            return obj != null && Equals((TradingPair)obj);
        }

        public bool Equals(TradingPair other)
        {
            return other.Base == Base && other.Quote == Quote;
        }

        public override int GetHashCode()
        {
            return System.HashCode.Combine(Base.GetHashCode(), Quote.GetHashCode());
        }

        public override string ToString()
        {
            return $"[Base={Base}, Quote={Quote}, StepSize={StepSize}, MinQty={MinQty}, MaxQty={MaxQty}]";
        }
    }
}