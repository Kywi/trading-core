using System.Collections.Generic;

namespace GripTrader.Tuner.Sweep
{
    /// <summary>
    /// JSON-deserialised sweep manifest. Example:
    /// <code>
    /// {
    ///   "BaseConfig": "./config/baseline.json",
    ///   "Feeder": "klines",
    ///   "MaxParallel": 4,
    ///   "Parameters": {
    ///     "StepPct":          { "Range": [0.003, 0.010], "Step": 0.001 },
    ///     "TrailingNoisePct": { "Values": [0.001, 0.002, 0.005] }
    ///   }
    /// }
    /// </code>
    /// </summary>
    public sealed class SweepConfig
    {
        /// <summary>Path to a base BotSettings JSON. All sweep runs start from a clone of this.</summary>
        public string BaseConfig { get; set; } = "";

        /// <summary>Optional feeder override applied to every run (overrides config + CLI).</summary>
        public string? Feeder { get; set; }

        /// <summary>Maximum number of concurrent runs. Defaults to 1 (serial).</summary>
        public int MaxParallel { get; set; } = 1;

        /// <summary>
        /// Optional list of symbol overrides for cross-symbol robustness
        /// sweeps. When present, the total run count is
        /// <c>Symbols.Count × cartesian(Parameters)</c> — each parameter
        /// combination is replayed against every symbol with that symbol's
        /// data paths swapped in. When null/empty the sweep uses the base
        /// config's symbol and paths unchanged.
        /// </summary>
        public List<SymbolOverride>? Symbols { get; set; }

        /// <summary>Parameter name (BotSettings property) → spec. Cartesian product of all keys is expanded.</summary>
        public Dictionary<string, ParameterSpec> Parameters { get; set; } = new();

        /// <summary>
        /// Optional walk-forward block. When present the <c>walk-forward</c>
        /// command runs the sweep on the train window, picks the winner by
        /// the configured metric, then re-runs that single config on the
        /// test window. The plain <c>sweep</c> command ignores this block
        /// — it is only consumed by walk-forward.
        /// </summary>
        public WalkforwardConfig? Walkforward { get; set; }
    }

    /// <summary>
    /// One symbol entry inside <see cref="SweepConfig.Symbols"/>. The
    /// <see cref="Symbol"/> field replaces <c>BotSettings.Symbol</c> for
    /// runs in this group; the path fields are optional and only override
    /// when set, so e.g. an aggTrades sweep can leave the kline paths
    /// blank.
    /// </summary>
    public sealed class SymbolOverride
    {
        public string Symbol { get; set; } = "";
        public string? BacktestCsvPath { get; set; }
        public string? BacktestKlineFeederPath { get; set; }
        public string? BacktestKlineFolderPath { get; set; }
    }

    /// <summary>
    /// Single-parameter sweep specification. Provide exactly ONE of:
    /// <list type="bullet">
    /// <item><c>Range</c> + <c>Step</c> — inclusive numeric range expanded by Step (decimal/int targets).</item>
    /// <item><c>Values</c> — explicit list of decimal values (decimal/int targets).</item>
    /// <item><c>BoolValues</c> — explicit list of booleans (bool targets like <c>EnableBagCleaning</c>).</item>
    /// <item><c>StringValues</c> — explicit list of strings (string targets like <c>TrendInterval</c>).</item>
    /// </list>
    /// The applier dispatches by the BotSettings property type, so
    /// numeric values are routed to <c>Range</c>/<c>Values</c>, booleans to
    /// <c>BoolValues</c>, and strings to <c>StringValues</c>.
    /// </summary>
    public sealed class ParameterSpec
    {
        public decimal[]? Range { get; set; }
        public decimal? Step { get; set; }
        public decimal[]? Values { get; set; }
        public bool[]? BoolValues { get; set; }
        public string[]? StringValues { get; set; }
    }
}
