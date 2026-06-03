using System.Threading.Tasks;
using Binance.Spot;
using Binance.Spot.Models;

namespace GripTrader.Core.Exchange
{
    /// <summary>
    /// Narrow seam over the Binance.Spot SDK order endpoints used by
    /// <see cref="BinanceConnectorExecutor"/>. Each method returns the raw JSON
    /// response on success and throws
    /// <see cref="Binance.Common.BinanceClientException"/> when Binance rejects
    /// the request (the executor classifies the failure by its error code).
    /// Extracted so the executor's error-path control flow — transient vs
    /// order-not-found vs default — can be driven by a fake in tests without a
    /// live exchange connection.
    /// </summary>
    internal interface IBinanceTradeClient
    {
        Task<string> NewOrderAsync(string wireSymbol, bool isBuy, bool isMarket, decimal quantity, decimal? price, string? newClientOrderId, int recvWindow);
        Task<string> QueryOrderByIdAsync(string wireSymbol, long orderId, int recvWindow);
        Task<string> QueryOrderByClientIdAsync(string wireSymbol, string clientOrderId, int recvWindow);
        Task CancelOrderAsync(string wireSymbol, long orderId, int recvWindow);
    }

    /// <summary>
    /// Production adapter that forwards to a real <see cref="SpotAccountTrade"/>,
    /// translating the engine's domain parameters into the SDK's enums. This is
    /// a pure pass-through — the live order semantics are identical to the
    /// pre-seam inline SDK calls.
    /// </summary>
    internal sealed class SpotAccountTradeClient : IBinanceTradeClient
    {
        private readonly SpotAccountTrade _trade;

        public SpotAccountTradeClient(SpotAccountTrade trade) => _trade = trade;

        public Task<string> NewOrderAsync(string wireSymbol, bool isBuy, bool isMarket, decimal quantity, decimal? price, string? newClientOrderId, int recvWindow)
        {
            var side = isBuy ? Side.BUY : Side.SELL;
            return isMarket
                ? _trade.NewOrder(
                    symbol: wireSymbol,
                    side: side,
                    type: OrderType.MARKET,
                    quantity: quantity,
                    newOrderRespType: NewOrderResponseType.FULL,
                    recvWindow: recvWindow)
                : _trade.NewOrder(
                    symbol: wireSymbol,
                    side: side,
                    type: OrderType.LIMIT,
                    timeInForce: TimeInForce.GTC,
                    quantity: quantity,
                    price: price,
                    newClientOrderId: newClientOrderId,
                    newOrderRespType: NewOrderResponseType.FULL,
                    recvWindow: recvWindow);
        }

        public Task<string> QueryOrderByIdAsync(string wireSymbol, long orderId, int recvWindow)
            => _trade.QueryOrder(symbol: wireSymbol, orderId: orderId, recvWindow: recvWindow);

        public Task<string> QueryOrderByClientIdAsync(string wireSymbol, string clientOrderId, int recvWindow)
            => _trade.QueryOrder(symbol: wireSymbol, origClientOrderId: clientOrderId, recvWindow: recvWindow);

        public Task CancelOrderAsync(string wireSymbol, long orderId, int recvWindow)
            => _trade.CancelOrder(symbol: wireSymbol, orderId: orderId, recvWindow: recvWindow);
    }
}
