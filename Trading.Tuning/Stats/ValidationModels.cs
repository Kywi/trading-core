using System;
using System.Collections.Generic;

namespace GripTrader.Tuner.Stats
{
    /// <summary>
    /// Canonical per-observation moments of one return series, produced by
    /// <see cref="SharpeStats.Moments"/>. All quantities are NON-annualized.
    /// <see cref="Kurtosis"/> is the NON-EXCESS γ₄ (Gaussian = 3.0) — the
    /// load-bearing convention the PSR denominator depends on.
    /// </summary>
    public readonly record struct SeriesMoments(double Sr, double Skew, double Kurtosis, int T);

    /// <summary>
    /// A single cross-validation split: disjoint train and test index sets over
    /// <c>[0, nObservations)</c>. Indices are in ascending order. An empty
    /// <see cref="TrainIdx"/> means the fold is unusable (purge + embargo
    /// consumed the entire training set) — surfaced explicitly, never silently
    /// shrunk.
    /// </summary>
    public readonly record struct CvSplit(int[] TrainIdx, int[] TestIdx);

    /// <summary>
    /// A Combinatorial Purged CV configuration: <see cref="NGroups"/> contiguous
    /// groups with <see cref="KTest"/> of them held out per split.
    /// </summary>
    public readonly record struct CpcvScheme(int NGroups, int KTest);

    /// <summary>
    /// Probability of Backtest Overfitting result: the scalar <see cref="Pbo"/>
    /// (fraction of CSCV splits whose in-sample-best config landed in the bottom
    /// OOS half, i.e. logit ≤ 0) plus the per-split <see cref="Logits"/> for
    /// diagnostics.
    /// </summary>
    public readonly record struct PboResult(double Pbo, IReadOnlyList<double> Logits);

    /// <summary>
    /// One honest trial record for <see cref="TrialCountLog"/>. Strategy-agnostic:
    /// strategy-specific fields (pair, threshold, lookback, window) ride in the
    /// generic <see cref="Tags"/> bag — core names no strategy.
    /// </summary>
    public sealed class TrialRecord
    {
        /// <summary>Caller's feed-clock timestamp (epoch ms). NOT <c>DateTime.UtcNow</c> — the replay path is deterministic.</summary>
        public long TimestampMs { get; init; }

        /// <summary>Trial kind, e.g. "CointegrationScreen" | "ParamConfig".</summary>
        public string Kind { get; init; } = "";

        /// <summary>Outcome, e.g. "Evaluated" | "Rejected" | "Abandoned" | "Traded". Rejected/abandoned are first-class trials.</summary>
        public string Outcome { get; init; } = "";

        /// <summary>Strategy-specific descriptors (pair, threshold, lookback, window, …). Core does not interpret these.</summary>
        public IReadOnlyDictionary<string, string> Tags { get; init; } = EmptyTags;

        private static readonly IReadOnlyDictionary<string, string> EmptyTags =
            new Dictionary<string, string>();
    }
}
