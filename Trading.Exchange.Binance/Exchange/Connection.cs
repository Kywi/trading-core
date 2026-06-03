using GripTrader.Core.Models;
using GripTrader.Core.Utils;
using Log;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Websockets.Core;

namespace GripTrader.Core.Exchange
{
    public class Connection
    {
        private Manager? _websocket;
        private readonly Dictionary<string, TradingPair> _supportedTradingPairs;
        private readonly IEnumerable<IPriceSink> _sinks;

        public Connection(IEnumerable<IPriceSink> sinks, Dictionary<string, TradingPair> supportedTradingPairs)
        {
            _sinks = sinks ?? throw new ArgumentNullException(nameof(sinks));
            _supportedTradingPairs = supportedTradingPairs ?? throw new ArgumentNullException(nameof(supportedTradingPairs));
        }

        public void Dispose()
        {
            if (_websocket is null) 
                return;

            _websocket.MessageReceived -= WebsocketOnMessageReceived;
            _websocket.Dispose();
        }

        public async Task StartAsync()
        {
            var streams = _supportedTradingPairs.Select(tp => $"{tp.Value.Base}{tp.Value.Quote}@bookTicker");

            string url = "wss://stream.binance.com:9443/stream?streams=" + string.Join('/', streams);

            //url = url.Substring(0, url.Length - 1);
            _websocket = new Manager(url, false);
            _websocket.MessageReceived += WebsocketOnMessageReceived;

            await _websocket.Start();
        }

        private void WebsocketOnMessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            Dispatch(e.Message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// <summary>
        /// Deserializes one combined-stream bookTicker message and fans the
        /// resulting best bid/ask out to every <see cref="IPriceSink"/>. A
        /// message that cannot be deserialized, or that carries no data payload,
        /// is dropped without touching the sinks. Extracted from the websocket
        /// callback so the parse + fan-out is unit-testable without a live socket;
        /// <paramref name="timestampMs"/> is injected so the live path stamps
        /// wall-clock while tests stay deterministic.
        /// </summary>
        internal void Dispatch(string message, long timestampMs)
        {
            CombinedStreamEventDto? update;
            try
            {
                update = JsonConvert.DeserializeObject<CombinedStreamEventDto>(message);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Cannot deserialize message: {ex}");
                return;
            }

            if (update?.Data == null)
                return;

            var prices = new BidAsk(update.Data.BestBidPrice, update.Data.BestAskPrice, timestampMs);
            foreach (var priceSink in _sinks)
            {
                priceSink.OnPrice(Symbols.Key(update.Data.Symbol!), prices);
            }
        }
    }
}
