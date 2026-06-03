using Newtonsoft.Json;

namespace GripTrader.Core.Models
{
    public class TickerResponseDto
    {
        [JsonProperty("u")]
        public decimal OrderBookUpdateId { get; set; }

        [JsonProperty("s")]
        public string? Symbol { get; set; }

        [JsonProperty("b")]
        public decimal BestBidPrice { get; set; }

        [JsonProperty("B")]
        public decimal BestBidQty { get; set; }

        [JsonProperty("a")]
        public decimal BestAskPrice { get; set; }

        [JsonProperty("A")]
        public decimal BestAskQty { get; set; }
    }
}