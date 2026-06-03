namespace GripTrader.Core.Models
{
    public enum OrderStatus
    {
        Unknown = 0,
        New = 1,
        PartiallyFilled = 2,
        Filled = 3,
        Canceled = 4,
        Rejected = 5,
        Expired = 6,

        /// <summary>
        /// A query failed transiently (network timeout, server error, rate
        /// limit, or a non "order-not-found" client error). Distinct from
        /// <see cref="Unknown"/>, which means the exchange affirmatively does
        /// not have the order. Callers must NOT treat a transient error as
        /// "order never placed" — doing so would mint a duplicate order.
        /// </summary>
        TransientError = 7
    }

    public struct OrderQueryResult
    {
        public OrderStatus Status { get; set; }
        public decimal ExecutedQuantity { get; set; }
        public decimal AveragePrice { get; set; }

        /// <summary>
        /// The exchange-assigned order id. Non-zero only when the order was
        /// found - callers that issued a query by clientOrderId use this to
        /// re-adopt the orderId after a placement timed out.
        /// </summary>
        public long OrderId { get; set; }
    }
}
