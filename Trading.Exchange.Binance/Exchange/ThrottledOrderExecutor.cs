using System;
using System.Threading;
using System.Threading.Tasks;
using GripTrader.Core.Bot;
using GripTrader.Core.Models;
using GripTrader.Core.Utils;

namespace GripTrader.Core.Exchange
{
    // Wraps any IOrderExecutor and enforces rate limits.
    public sealed class ThrottledOrderExecutor : IOrderExecutor
    {
        private readonly IOrderExecutor _inner;
        private readonly OrderRateLimiter _limiter;
        private readonly CancellationToken _ct;

        public ThrottledOrderExecutor(IOrderExecutor inner, OrderRateLimiter limiter, CancellationToken ct)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
            _ct = ct;
        }

        public async Task<PlaceMarketResult> PlaceMarketAsync(string symbol, decimal quantity, bool isBuy)
        {
            await _limiter.WaitAsync(_ct);
            return await _inner.PlaceMarketAsync(symbol, quantity, isBuy);
        }

        public async Task<PlaceLimitResult> PlaceLimitAsync(string symbol, decimal quantity, decimal price, bool isBuy, string? newClientOrderId = null)
        {
            await _limiter.WaitAsync(_ct);
            return await _inner.PlaceLimitAsync(symbol, quantity, price, isBuy, newClientOrderId);
        }

        public async Task<OrderQueryResult> QueryOrderAsync(string symbol, long orderId)
        {
            await _limiter.WaitAsync(_ct);
            return await _inner.QueryOrderAsync(symbol, orderId);
        }

        public async Task<OrderQueryResult> QueryOrderByClientIdAsync(string symbol, string clientOrderId)
        {
            await _limiter.WaitAsync(_ct);
            return await _inner.QueryOrderByClientIdAsync(symbol, clientOrderId);
        }

        public async Task<bool> CancelOrderAsync(string symbol, long orderId)
        {
            await _limiter.WaitAsync(_ct);
            return await _inner.CancelOrderAsync(symbol, orderId);
        }

        public async Task<decimal> GetCurrentTotalEquityAsync(string symbol, decimal markPrice)
        {
            await _limiter.WaitAsync(_ct);
            return await _inner.GetCurrentTotalEquityAsync(symbol, markPrice);
        }
    }
}