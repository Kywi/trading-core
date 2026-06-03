namespace GripTrader.Tuner.Sweep
{
    /// <summary>
    /// Walk-forward block embedded inside a <see cref="SweepConfig"/> JSON.
    /// When present, the sweep splits into two phases: a parameter sweep
    /// over the <see cref="Train"/> window, then a single re-run of the
    /// winning configuration over the <see cref="Test"/> window — a
    /// genuine out-of-sample check that the chosen parameters are not
    /// overfit to the train period.
    /// </summary>
    public sealed class WalkforwardConfig
    {
        /// <summary>[fromYYYY-MM, toYYYY-MM] inclusive train window for parameter optimisation.</summary>
        public string[]? Train { get; set; }

        /// <summary>[fromYYYY-MM, toYYYY-MM] inclusive test window for the out-of-sample run.</summary>
        public string[]? Test { get; set; }

        /// <summary>
        /// Metric used to pick the winner from the train sweep. Supported:
        /// <c>"Sharpe"</c> (default), <c>"Sortino"</c>, <c>"Calmar"</c>,
        /// <c>"TotalReturn"</c>, <c>"Cagr"</c>. Higher is better — runs
        /// with NaN/null metric or any error are excluded from ranking.
        /// </summary>
        public string OptimizeBy { get; set; } = "Sharpe";
    }
}
