using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GripTrader.Core.Utils
{
    // Simple sliding-window rate limiter with a minimum spacing between calls.
    public sealed class OrderRateLimiter
    {
        private readonly int _maxCount;          // e.g., 50
        private readonly TimeSpan _window;       // e.g., 10s
        private readonly TimeSpan _minSpacing;   // e.g., 250ms
        private readonly Queue<DateTime> _calls = new Queue<DateTime>();
        private DateTime _lastCall = DateTime.MinValue;
        private readonly object _lock = new object();

        public OrderRateLimiter(int maxCount, TimeSpan window, TimeSpan minSpacing)
        {
            if (maxCount < 1) maxCount = 1;
            _maxCount = maxCount;
            _window = window <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : window;
            _minSpacing = minSpacing < TimeSpan.Zero ? TimeSpan.Zero : minSpacing;
        }

        public async Task WaitAsync(CancellationToken ct)
        {
            while (true)
            {
                TimeSpan delay = TimeSpan.Zero;
                DateTime now = DateTime.UtcNow;
                DateTime nextAllowedByWindow = now;
                DateTime nextAllowedBySpacing;

                lock (_lock)
                {
                    // evict old timestamps
                    while (_calls.Count > 0 && (now - _calls.Peek()) > _window)
                        _calls.Dequeue();

                    // spacing constraint
                    nextAllowedBySpacing = _lastCall + _minSpacing;

                    if (_calls.Count < _maxCount && now >= nextAllowedBySpacing)
                    {
                        // record and return
                        _calls.Enqueue(now);
                        _lastCall = now;
                        return;
                    }

                    // compute delay until we can proceed
                    if (_calls.Count >= _maxCount)
                    {
                        var oldest = _calls.Peek();
                        var earliestByWindow = oldest + _window;
                        if (earliestByWindow > nextAllowedByWindow) 
                            nextAllowedByWindow = earliestByWindow;
                    }
                    if (nextAllowedBySpacing > nextAllowedByWindow) 
                        nextAllowedByWindow = nextAllowedBySpacing;

                    delay = nextAllowedByWindow - now;
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                }

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);
            }
        }
    }
}
