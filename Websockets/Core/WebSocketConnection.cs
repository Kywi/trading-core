using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Websockets.Utils;

namespace Websockets.Core
{
    public class WebSocketConnection : IWebSocketConnection
    {
        private const int ReceiveBufferSize = 8192;

        private readonly string _baseUrl;
        private readonly SemaphoreLocker _sendLock;
        private ClientWebSocket? _socket;

        public WebSocketConnection(string baseUrl)
        {
            _sendLock = new SemaphoreLocker();
            _socket = new ClientWebSocket();
            _baseUrl = baseUrl;
        }

        public void Dispose()
        {
            _socket?.Abort();
            _socket?.Dispose();
            _socket = null;
            _sendLock.Dispose();
        }

        public async Task Connect(CancellationToken cancellationToken = default)
        {
            await _socket!.ConnectAsync(new Uri(_baseUrl), cancellationToken).ConfigureAwait(false);
        }

        public async Task Close(CancellationToken cancellationToken = default)
        {
            if (_socket is null)
                return;

            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed by the client", cancellationToken).ConfigureAwait(false);
        }

        public async Task Send(string text, CancellationToken cancellationToken = default)
        {
            if (!IsOpen())
                return;

            var buffer = new ArraySegment<byte>(Encoding.UTF8.GetBytes(text));
            await _sendLock.LockAsync(
                () => _socket!.SendAsync(buffer, WebSocketMessageType.Text, true, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        public bool IsOpen()
        {
            return _socket is { State: WebSocketState.Open };
        }

        public async Task<string> ReceiveMessage(CancellationToken cancellationToken = default)
        {
            var rented = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
            try
            {
                while (IsOpen())
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    var segment = new ArraySegment<byte>(rented);
                    do
                    {
                        result = await _socket!.ReceiveAsync(segment, cancellationToken).ConfigureAwait(false);
                        ms.Write(rented, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await Close(cancellationToken).ConfigureAwait(false);
                        return "";
                    }

                    return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                }

                return "";
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
