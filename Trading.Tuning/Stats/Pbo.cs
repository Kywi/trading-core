using System;
using System.Collections.Generic;

namespace GripTrader.Tuner.Stats
{
    /// <summary>
    /// Probability of Backtest Overfitting (PBO) via Combinatorial Symmetric Cross
    /// Validation (CSCV) (validation primitive 4).
    ///
    /// <para>
    /// Input is a <c>metricMatrix[config][partition]</c>: the SAME metric that
    /// selects the in-sample winner (the <c>OptimizeBy</c> metric, Sharpe by
    /// default) evaluated for every config across the CPCV/CSCV partitions. For
    /// each balanced IS/OOS split of the partitions, pick the config that maximizes
    /// the metric IS, find that config's OOS rank <c>r</c> among <c>Nc</c> configs,
    /// form the relative rank <c>ω = r/(Nc+1)</c> (using the MID-RANK on OOS ties)
    /// and the logit <c>λ = ln(ω/(1−ω))</c>. <c>PBO</c> is the probability mass of
    /// splits with <c>λ &lt; 0</c> (the IS-best config landed in the bottom OOS half),
    /// counting an exact coin-flip (<c>λ = 0</c>, <c>ω = 0.5</c>) as half. PBO ≈ 0.5
    /// is the no-information line.
    /// </para>
    ///
    /// Pure and deterministic: partition combinations and tie-breaks are fixed by
    /// index. All quantities are <see cref="double"/> (statistics carve-out).
    /// </summary>
    public static class Pbo
    {
        /// <summary>Scalar PBO from the metric matrix. See <see cref="Compute"/> for the full result.</summary>
        public static double ProbabilityOfBacktestOverfitting(double[][] metricMatrix)
            => Compute(metricMatrix).Pbo;

        /// <summary>
        /// Full CSCV computation: returns the scalar PBO plus the per-split logits.
        /// </summary>
        /// <param name="metricMatrix">
        /// <c>[config][partition]</c>; the per-partition value of the SAME metric used
        /// to pick the IS winner. Must be rectangular (every config has the same
        /// partition count) and have ≥ 2 partitions.
        /// </param>
        /// <remarks>
        /// <para>
        /// Aggregation: IS/OOS performance per config is the arithmetic MEAN of the
        /// per-partition metric values. For a non-linear metric (e.g. Sharpe) this is
        /// not identical to evaluating the metric on the concatenated IS/OOS data
        /// (textbook CSCV). Supply a metric whose partition-mean approximates the
        /// pooled value, or read the result as a mean-aggregated PBO.
        /// </para>
        /// Edge cases: an ODD partition count throws (CSCV needs an even, symmetric
        /// split); fewer than 2 configs ⇒ PBO 0 (no selection pressure to overfit —
        /// documented); ragged, empty, or null matrix ⇒ throws. IS-metric ties break
        /// by lowest config index; OOS rank ties use the MID-RANK, so a no-information
        /// matrix yields PBO = 0.5 (the coin-flip line) rather than a boundary value.
        /// </remarks>
        public static PboResult Compute(double[][] metricMatrix)
        {
            if (metricMatrix is null) throw new ArgumentNullException(nameof(metricMatrix));
            int nc = metricMatrix.Length;
            if (nc == 0) throw new ArgumentException("metricMatrix has no configs.", nameof(metricMatrix));

            int s = metricMatrix[0]?.Length ?? 0;
            if (s < 2) throw new ArgumentException("metricMatrix needs at least 2 partitions.", nameof(metricMatrix));
            for (int c = 0; c < nc; c++)
            {
                if (metricMatrix[c] is null)
                    throw new ArgumentException($"metricMatrix[{c}] is null.", nameof(metricMatrix));
                if (metricMatrix[c].Length != s)
                    throw new ArgumentException("metricMatrix is ragged: all configs must share one partition count.", nameof(metricMatrix));
            }

            // Fewer than 2 configs: there is no selection to overfit. PBO = 0.
            if (nc < 2)
                return new PboResult(0.0, Array.Empty<double>());

            // CSCV requires an EVEN partition count so the IS and OOS halves are
            // symmetric (the complement of an S/2-subset is itself an S/2-subset,
            // preserving the CSCV symmetry property). An odd S gives asymmetric,
            // biased halves — reject it rather than silently bias the estimate.
            if ((s & 1) != 0)
                throw new ArgumentException("CSCV requires an even partition count for a symmetric IS/OOS split.", nameof(metricMatrix));
            int half = s / 2;

            var combos = CombinatorialPurgedCv.Combinations(s, half);
            var logits = new List<double>(combos.Count);

            foreach (var isPartitions in combos)
            {
                var isMask = new bool[s];
                for (int i = 0; i < isPartitions.Length; i++) isMask[isPartitions[i]] = true;

                // IS metric per config = mean metric over the IS partitions
                // (fixed ascending-partition reduction order for determinism).
                int bestConfig = 0;
                double bestIsMetric = double.NegativeInfinity;
                for (int c = 0; c < nc; c++)
                {
                    double isMetric = MeanOverMask(metricMatrix[c], isMask, true, s);
                    // Strictly greater ⇒ ties keep the lowest config index.
                    if (isMetric > bestIsMetric)
                    {
                        bestIsMetric = isMetric;
                        bestConfig = c;
                    }
                }

                // OOS metric per config = mean over the complementary partitions.
                double bestOosMetric = MeanOverMask(metricMatrix[bestConfig], isMask, false, s);

                // OOS rank of the IS-best config (López de Prado convention: rank ∈
                // [1, Nc], best OOS = highest rank). Ties use the MID-RANK (the average
                // rank of the tied group) so a no-information matrix — every config
                // identical OOS — gives the median rank ⇒ ω = 0.5 ⇒ λ = 0, the honest
                // coin-flip, instead of being pushed to a boundary by an arbitrary
                // index tie-break.
                int worseStrict = 0; // configs strictly worse OOS than the IS-best
                int tiedOos = 0;     // configs tied with the IS-best OOS (excluding itself)
                for (int c = 0; c < nc; c++)
                {
                    if (c == bestConfig) continue;
                    double oos = MeanOverMask(metricMatrix[c], isMask, false, s);
                    if (oos < bestOosMetric) worseStrict++;
                    else if (oos == bestOosMetric) tiedOos++;
                }
                double rank = worseStrict + 1 + tiedOos / 2.0; // mid-rank within ties

                double omega = rank / (nc + 1);
                // IS-best also OOS-best ⇒ rank Nc ⇒ ω large ⇒ λ > 0 (not overfit).
                // IS-best is OOS-worst (overfit) ⇒ rank 1 ⇒ ω small ⇒ λ < 0.
                // Fully tied (no information) ⇒ rank (Nc+1)/2 ⇒ ω = 0.5 ⇒ λ = 0.
                double lambda = Math.Log(omega / (1.0 - omega));
                logits.Add(lambda);
            }

            // PBO = probability mass of splits whose IS-best landed in the bottom OOS
            // half (λ < 0), counting an exact coin-flip (λ = 0, ω = 0.5) as half — so a
            // no-information matrix gives PBO = 0.5, not a boundary value. Fixed
            // reduction order.
            double overfitMass = 0.0;
            for (int i = 0; i < logits.Count; i++)
            {
                if (logits[i] < 0.0) overfitMass += 1.0;
                else if (logits[i] == 0.0) overfitMass += 0.5;
            }

            double pbo = logits.Count > 0 ? overfitMass / logits.Count : 0.0;
            return new PboResult(pbo, logits);
        }

        /// <summary>
        /// Mean of <paramref name="row"/> over partitions where the mask matches
        /// <paramref name="wantMaskValue"/>. Fixed ascending-partition reduction
        /// order. Returns <see cref="double.NegativeInfinity"/> when no partition
        /// matches (cannot happen for a balanced split with half ≥ 1).
        /// </summary>
        private static double MeanOverMask(double[] row, bool[] mask, bool wantMaskValue, int s)
        {
            double sum = 0.0;
            int n = 0;
            for (int i = 0; i < s; i++)
            {
                if (mask[i] == wantMaskValue) { sum += row[i]; n++; }
            }
            return n > 0 ? sum / n : double.NegativeInfinity;
        }
    }
}
