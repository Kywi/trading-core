using System.Collections.Generic;
using GripTrader.Tuner.Stats;

namespace GripTrader.Tuner.Sweep
{
    /// <summary>One row of the aggregated sweep CSV: a parameter combination and
    /// the resulting <see cref="RunSummary"/> (or an error for a failed run).</summary>
    public sealed class SweepRunRecord
    {
        public required int Index { get; init; }
        public required string RunId { get; init; }
        public required string Symbol { get; init; }
        public required Dictionary<string, object> Parameters { get; init; }
        public RunSummary? Summary { get; init; }
        public string? Error { get; init; }
        public double WallClockSeconds { get; init; }
    }
}
