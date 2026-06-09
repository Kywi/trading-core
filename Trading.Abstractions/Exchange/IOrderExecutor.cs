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

        /// <summary>
        /// Returns the position's <see cref="EquityBreakdown"/> — the
        /// <c>(equity, maintenanceMargin)</c> pair the liquidation decision is
        /// made from — at the supplied <paramref name="markPrice"/>.
        /// <para>
        /// This is an <b>additive, default-preserving</b> seam extension: the
        /// default implementation returns
        /// <c>(GetCurrentTotalEquityAsync(symbol, markPrice), 0m)</c>, so every
        /// existing implementer (spot/live executors with no maintenance-margin
        /// concept) keeps byte-identical behavior and needs no edit. Only the
        /// perp mock executor overrides it to report per-leg
        /// <c>(allocated equity, leg maintenance margin)</c> under isolated
        /// margin. The five pre-existing members are unchanged.
        /// </para>
        /// </summary>
        async Task<EquityBreakdown> GetEquityBreakdownAsync(string symbol, decimal markPrice)
        {
            var equity = await GetCurrentTotalEquityAsync(symbol, markPrice).ConfigureAwait(false);
            return new EquityBreakdown(equity, 0m);
        }
    }
}
