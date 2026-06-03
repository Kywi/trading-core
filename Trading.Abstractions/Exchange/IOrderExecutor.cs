using GripTrader.Core.Models;
using System.Threading.Tasks;

namespace GripTrader.Core.Bot
{
    public interface IOrderExecutor
    {
        Task<PlaceMarketResult> PlaceMarketAsync(string symbol, decimal quantity, bool isBuy);

        /// <summary>
        /// Places a LIMIT order. <paramref name="newClientOrderId"/> is forwarded to
        /// the exchange as <c>newClientOrderId</c>; passing the same value on a
        /// retry causes the exchange to reject the duplicate (Binance error
        /// -2010), so combined with QueryOrderByClientIdAsync the caller can
        /// safely retry a placement that timed out.
        /// </summary>
        Task<PlaceLimitResult> PlaceLimitAsync(string symbol, decimal quantity, decimal price, bool isBuy, string? newClientOrderId = null);

        Task<OrderQueryResult> QueryOrderAsync(string symbol, long orderId);

        /// <summary>
        /// Looks up an order by the client-supplied id (origClientOrderId on
        /// Binance). Returns Status=Unknown if no such order exists. Used
        /// after a PlaceLimitAsync that timed out, to discover whether the
        /// order actually got placed and recover its exchange-assigned orderId.
        /// </summary>
        Task<OrderQueryResult> QueryOrderByClientIdAsync(string symbol, string clientOrderId);

        Task<bool> CancelOrderAsync(string symbol, long orderId);
        Task<decimal> GetCurrentTotalEquityAsync(string symbol, decimal markPrice);
    }
}
