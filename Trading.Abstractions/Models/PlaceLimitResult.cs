namespace GripTrader.Core.Models
{
    public struct PlaceLimitResult
    {
        public long OrderId { get; set; }
        public decimal Price { get; set; }
        public decimal Quantity { get; set; }
    }
}
