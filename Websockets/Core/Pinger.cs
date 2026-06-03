using System;
using System.Threading;
using System.Timers;
using Log;
using Timer = System.Timers.Timer;

namespace Websockets.Core
{
    public class Pinger : IDisposable
    {
        private readonly IWebSocketConnection _connection;
        private readonly string _pingMessage;
        private readonly Timer _timer;
        private int _pinging;

        public event EventHandler<Exception>? PingFailed;

        public Pinger(IWebSocketConnection connection, double interval, string pingMessage)
        {
            _connection = connection;
            _pingMessage = pingMessage;
            _timer = new Timer
            {
                AutoReset = true,
                Interval = interval,
            };
            _timer.Elapsed += TimerOnElapsed;
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Elapsed -= TimerOnElapsed;
            _timer.Dispose();
        }

        public void Start()
        {
            _timer.Start();
        }

        private async void TimerOnElapsed(object? sender, ElapsedEventArgs e)
        {
            if (Interlocked.Exchange(ref _pinging, 1) == 1)
                return;

            try
            {
                await _connection.Send(_pingMessage).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "WebSocket ping failed");
                PingFailed?.Invoke(this, ex);
            }
            finally
            {
                Interlocked.Exchange(ref _pinging, 0);
            }
        }
    }
}
