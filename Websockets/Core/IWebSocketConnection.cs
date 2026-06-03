using System;
using System.Threading;
using System.Threading.Tasks;

namespace Websockets.Core
{
    /// <summary>
    /// Transport abstraction over a single websocket connection. Extracted so the
    /// reconnect loop and idle/heartbeat watchdog in <see cref="Manager"/> — and
    /// the heartbeat in <see cref="Pinger"/> — can be driven by a fake in tests
    /// instead of a real socket. Implemented in production by
    /// <see cref="WebSocketConnection"/>.
    /// </summary>
    public interface IWebSocketConnection : IDisposable
    {
        Task Connect(CancellationToken cancellationToken = default);
        Task<string> ReceiveMessage(CancellationToken cancellationToken = default);
        Task Send(string text, CancellationToken cancellationToken = default);
        bool IsOpen();
    }
}
