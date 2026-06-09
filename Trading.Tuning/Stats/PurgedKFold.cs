using System;
using System.Collections.Generic;

namespace GripTrader.Tuner.Stats
{
    /// <summary>
    /// Purged + embargoed K-fold splitter (validation primitive 1).
    ///
    /// <para>
    /// Test folds are contiguous index blocks over <c>[0, nObservations)</c>.
    /// Around each test block <c>[a, b]</c> the training set is:
    /// <list type="bullet">
    /// <item><b>purged</b> of any index within <paramref name="purgeGap"/> on either
    /// side (<c>a − purgeGap ≤ i ≤ b + purgeGap</c>), removing observations whose
    /// holding window OR signal-formation lookback overlaps the test window; and</item>
    /// <item><b>embargoed</b> of indices in <c>(b, b + embargo]</c> (forward-leak
    /// direction only — information after the test block can leak into a model
    /// trained on it).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Purge gap is the caller's responsibility:</b> pass
    /// <c>purgeGap = max(holdingHorizon, formationLookbackL)</c>. For pairs the
    /// formation lookback (rolling β / spread-mean / σ window length L) is the
    /// dominant cross-fold leak — purging only the holding window leaves it open.
    /// </para>
    ///
    /// Pure and deterministic: fixed index order, no RNG, no wall-clock.
    /// </summary>
    public static class PurgedKFold
    {
        /// <summary>
        /// Produce <paramref name="kFolds"/> purged + embargoed splits over
        /// <paramref name="nObservations"/> observations.
        /// </summary>
        /// <param name="nObservations">Total observation count (&gt; 0).</param>
        /// <param name="kFolds">Number of folds (2 ≤ kFolds ≤ nObservations).</param>
        /// <param name="purgeGap">
        /// Bars removed from train on EACH side of a test block; the caller computes
        /// <c>max(holdingHorizon, formationLookbackL)</c>. Must be ≥ 0.
        /// </param>
        /// <param name="embargo">
        /// Bars removed from train AFTER each test block (e.g. <c>ceil(0.01·n)</c>);
        /// forward direction only. Must be ≥ 0; clamps at <paramref name="nObservations"/>.
        /// </param>
        /// <remarks>
        /// If purge + embargo empty a fold's training set, that split's
        /// <see cref="CvSplit.TrainIdx"/> is an empty array (the fold is surfaced as
        /// unusable rather than silently shrinking the gap).
        /// </remarks>
        public static IReadOnlyList<CvSplit> Split(int nObservations, int kFolds, int purgeGap, int embargo)
        {
            if (nObservations <= 0)
                throw new ArgumentOutOfRangeException(nameof(nObservations), nObservations, "nObservations must be positive.");
            if (kFolds < 2)
                throw new ArgumentOutOfRangeException(nameof(kFolds), kFolds, "kFolds must be at least 2.");
            if (kFolds > nObservations)
                throw new ArgumentOutOfRangeException(nameof(kFolds), kFolds, "kFolds cannot exceed nObservations.");
            if (purgeGap < 0)
                throw new ArgumentOutOfRangeException(nameof(purgeGap), purgeGap, "purgeGap must be non-negative.");
            if (embargo < 0)
                throw new ArgumentOutOfRangeException(nameof(embargo), embargo, "embargo must be non-negative.");

            var bounds = FoldBounds(nObservations, kFolds);
            var splits = new List<CvSplit>(kFolds);

            foreach (var (a, b) in bounds)
            {
                splits.Add(BuildSplit(nObservations, a, b, purgeGap, embargo));
            }

            return splits;
        }

        /// <summary>
        /// Build one split given an inclusive test span <c>[a, b]</c>. Exposed
        /// internally so CPCV can reuse the identical purge/embargo rule for its
        /// multi-block test masks.
        /// </summary>
        internal static CvSplit BuildSplit(int nObservations, int a, int b, int purgeGap, int embargo)
        {
            var testIdx = new int[b - a + 1];
            for (int i = a, j = 0; i <= b; i++, j++) testIdx[j] = i;

            int purgeLo = a - purgeGap;
            int purgeHi = b + purgeGap;
            int embargoHi = b + embargo; // inclusive upper bound of the embargo window

            var train = new List<int>(nObservations);
            for (int i = 0; i < nObservations; i++)
            {
                // Exclude the test block itself, the purge halo on both sides,
                // and the forward-only embargo window.
                if (i >= purgeLo && i <= purgeHi) continue;
                if (i > b && i <= embargoHi) continue;
                train.Add(i);
            }

            return new CvSplit(train.ToArray(), testIdx);
        }

        /// <summary>
        /// Build a split for a CPCV test mask composed of one or more disjoint,
        /// already-sorted contiguous test blocks. Purge + embargo are applied
        /// around every block; the result train set excludes the union of all
        /// per-block halos. Internal — CPCV-only.
        /// </summary>
        internal static CvSplit BuildSplitMultiBlock(
            int nObservations, IReadOnlyList<(int a, int b)> testBlocks, int purgeGap, int embargo)
        {
            // Collect test indices in ascending order (blocks arrive sorted).
            int testCount = 0;
            for (int k = 0; k < testBlocks.Count; k++) testCount += testBlocks[k].b - testBlocks[k].a + 1;
            var testIdx = new int[testCount];
            for (int k = 0, j = 0; k < testBlocks.Count; k++)
                for (int i = testBlocks[k].a; i <= testBlocks[k].b; i++) testIdx[j++] = i;

            var train = new List<int>(nObservations);
            for (int i = 0; i < nObservations; i++)
            {
                bool excluded = false;
                for (int k = 0; k < testBlocks.Count; k++)
                {
                    int a = testBlocks[k].a, b = testBlocks[k].b;
                    if (i >= a - purgeGap && i <= b + purgeGap) { excluded = true; break; }
                    if (i > b && i <= b + embargo) { excluded = true; break; }
                }
                if (!excluded) train.Add(i);
            }

            return new CvSplit(train.ToArray(), testIdx);
        }

        /// <summary>
        /// Contiguous inclusive fold boundaries <c>[a, b]</c>; the remainder is
        /// distributed to the EARLIEST folds (fixed rule for determinism).
        /// Internal so CPCV can reuse the identical grouping rule.
        /// </summary>
        internal static List<(int a, int b)> FoldBounds(int n, int k)
        {
            var bounds = new List<(int, int)>(k);
            int baseSize = n / k;
            int remainder = n % k;
            int start = 0;
            for (int f = 0; f < k; f++)
            {
                int size = baseSize + (f < remainder ? 1 : 0); // remainder to earliest folds
                int end = start + size - 1;
                bounds.Add((start, end));
                start = end + 1;
            }
            return bounds;
        }
    }
}
