using System;
using System.Threading;
using System.Threading.Tasks;
using Log;

namespace Websockets.Core
{
    public class Manager : IDisposable
    {
        internal static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
        internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Next reconnect delay: doubles the current backoff, capped at
        /// <see cref="MaxBackoff"/>. Reset to <see cref="InitialBackoff"/> on every
        /// successful connect. Exposed so the exponential-with-cap schedule — which
        /// keeps a flapping feed from hammering the exchange into a ban — can be
        /// unit-tested without driving the live reconnect loop.
        /// </summary>
        internal static TimeSpan NextBackoff(TimeSpan current)
            => TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, current.TotalSeconds * 2));

        private readonly string _url;
        private readonly bool _ifPingerEnabled;
        private readonly string _pingMessage;
        private readonly double _pingInterval;
        private readonly TimeSpan _staleReceiveTimeout;
        private readonly Func<string, IWebSocketConnection> _connectionFactory;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _stateLock = new();

        private Pinger? _pinger;
        private IWebSocketConnection? _connection;
        private bool _disposed;

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<EventArgs>? OnConnected;

        /// <param name="staleReceiveTimeout">
        /// Idle/heartbeat watchdog: if no message arrives within this window the
        /// receive is treated as a half-open connection and the reconnect loop is
        /// triggered. Defaults to 90s; pass <see cref="TimeSpan.Zero"/> (or
        /// negative) to disable for legitimately quiet streams. Binance's
        /// bookTicker updates near-continuously, so a 90s gap means the socket is
        /// dead (common on VPS NAT timeouts) — without this, ReceiveAsync would
        /// block forever and the feed would freeze with no reconnect.
        /// </param>
        public Manager(string url, bool ifPingerEnabled = true, string pingMessage = "", double pingInterval = 20000,
                       TimeSpan? staleReceiveTimeout = null)
            : this(url, ifPingerEnabled, pingMessage, pingInterval, staleReceiveTimeout, connectionFactory: null)
        {
        }

        /// <summary>
        /// Test seam: injects the factory that mints a transport for each (re)connect,
        /// so the reconnect loop and idle/heartbeat watchdog can be driven by a fake
        /// <see cref="IWebSocketConnection"/> with no real socket. Production callers
        /// use the public constructor, which defaults to a real WebSocketConnection.
        /// </summary>
        internal Manager(string url, bool ifPingerEnabled, string pingMessage, double pingInterval,
                         TimeSpan? staleReceiveTimeout, Func<string, IWebSocketConnection>? connectionFactory)
        {
            _url = url;
            _pingMessage = pingMessage;
            _pingInterval = pingInterval;
            _ifPingerEnabled = ifPingerEnabled;
            _staleReceiveTimeout = staleReceiveTimeout ?? TimeSpan.FromSeconds(90);
            _connectionFactory = connectionFactory ?? (u => new WebSocketConnection(u));
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            try { _cts.Cancel(); } catch { /* already disposed */ }
            Stop();
            _cts.Dispose();
        }

        public async Task Send(string text)
        {
            var connection = _connection;
            if (connection is null)
                return;
            await connection.Send(text, _cts.Token).ConfigureAwait(false);
        }

        public async Task Start()
        {
            var backoff = InitialBackoff;

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    Init();
                    await _connection!.Connect(_cts.Token).ConfigureAwait(false);
                    OnConnected?.Invoke(this, EventArgs.Empty);

                    backoff = InitialBackoff;
                    await StartReceiving().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "WebSocket connection error");
                }
                finally
                {
                    Stop();
                }

                if (_cts.IsCancellationRequested)
                    return;

                try
                {
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(-200, 200));
                    var delay = backoff + jitter;
                    Logger.LogInformation($"WebSocket reconnecting in {delay.TotalSeconds:F1}s");
                    await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                backoff = NextBackoff(backoff);
            }
        }

        private void Stop()
        {
            Pinger? pinger;
            IWebSocketConnection? connection;
            lock (_stateLock)
            {
                pinger = _pinger;
                connection = _connection;
                _pinger = null;
                _connection = null;
            }

            if (pinger != null)
            {
                pinger.PingFailed -= OnPingFailed;
                pinger.Dispose();
            }
            connection?.Dispose();
        }

        private void Init()
        {
            lock (_stateLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(Manager));

                _connection = _connectionFactory(_url);
                if (_ifPingerEnabled)
                {
                    _pinger = new Pinger(_connection, _pingInterval, _pingMessage);
                    _pinger.PingFailed += OnPingFailed;
                }
            }
        }

        private void OnPingFailed(object? sender, Exception e)
        {
            // Surface ping failure as a connection abort so the receive loop
            // exits and the reconnect loop kicks in.
            _connection?.Dispose();
        }

        private async Task StartReceiving()
        {
            // Capture the connection at entry; if Stop() nulls _connection mid-loop
            // we still hold a reference and the disposed socket will surface as an
            // OperationCanceledException or a closed state on the next IsOpen check.
            var connection = _connection;
            if (connection == null)
                return;

            _pinger?.Start();

            while (!_cts.IsCancellationRequested && connection.IsOpen())
            {
                string message;
                if (_staleReceiveTimeout > TimeSpan.Zero)
                {
                    // Bound each receive: a half-open TCP connection (NAT/idle
                    // timeout) leaves ReceiveAsync blocked forever while IsOpen()
                    // still reports Open, so the reconnect loop never fires and the
                    // feed silently freezes. A linked CTS that CancelAfter()s the
                    // idle window turns that into a cancellation we convert to a
                    // TimeoutException, which Start()'s catch handles as a normal
                    // reconnect. The link to _cts means a real shutdown still wins.
                    using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    receiveCts.CancelAfter(_staleReceiveTimeout);
                    try
                    {
                        message = await connection.ReceiveMessage(receiveCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                    {
                        Logger.LogWarning($"WebSocket idle for {_staleReceiveTimeout.TotalSeconds:F0}s; treating as half-open and reconnecting.");
                        throw new TimeoutException("WebSocket receive idle timeout");
                    }
                }
                else
                {
                    message = await connection.ReceiveMessage(_cts.Token).ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(message))
                    return;

                MessageReceived?.Invoke(this, new MessageReceivedEventArgs(message));
            }
        }
    }
}
