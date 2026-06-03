using Newtonsoft.Json;

namespace GripTrader.Core.Models
{
    public class CombinedStreamEventDto
    {
        [JsonProperty("stream")]
        public string? Stream { get; set; }


        [JsonProperty("data")]
        public TickerResponseDto? Data { get; set; }

    }
}
