using System;
using System.Collections.Generic;

namespace GripTrader.Tuner.Stats
{
    /// <summary>
    /// Combinatorial Purged Cross-Validation (validation primitive 2).
    ///
    /// <para>
    /// The observation range <c>[0, nObservations)</c> is partitioned into
    /// <c>nGroups</c> contiguous, roughly equal groups (remainder distributed to
    /// the EARLIEST groups — a fixed rule for determinism). Each split holds out a
    /// distinct combination of <c>kTest</c> groups as the test set, giving
    /// <c>C(N, k)</c> splits. Reassembling the per-split OOS test returns yields
    /// <c>φ[N,k] = (k/N)·C(N,k)</c> full-length backtest paths — a DISTRIBUTION of
    /// Sharpe, not a point estimate.
    /// </para>
    ///
    /// <para>Each split reuses the <see cref="PurgedKFold"/> purge + embargo rule.</para>
    ///
    /// Pure and deterministic; all combinatorics use overflow-checked integer math
    /// (throws on overflow rather than wrapping).
    /// </summary>
    public static class CombinatorialPurgedCv
    {
        /// <summary>
        /// Upper ceiling on <see cref="SplitCount"/> to guard against accidental
        /// OOM from a pathological (N, k) (e.g. N=40, k=20). Throwing here is
        /// deliberate — silently truncating the split set would corrupt the path
        /// distribution.
        /// </summary>
        internal const int MaxSplits = 100_000;

        /// <summary>Number of splits = C(<paramref name="n"/>, <paramref name="k"/>).</summary>
        public static int SplitCount(int n, int k)
        {
            if (n < 2) throw new ArgumentOutOfRangeException(nameof(n), n, "nGroups must be at least 2.");
            if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), k, "kTest must be at least 1.");
            if (k >= n) throw new ArgumentOutOfRangeException(nameof(k), k, "kTest must be strictly less than nGroups.");

            long c = Choose(n, k);
            if (c > MaxSplits)
                throw new ArgumentOutOfRangeException(nameof(k), c, $"C({n},{k})={c} exceeds the {MaxSplits} split ceiling.");
            return checked((int)c);
        }

        /// <summary>
        /// Number of reassembled paths = <c>φ[N,k] = (k/N)·C(N,k)</c>, computed as
        /// the EXACT integer <c>k·C(N,k)/N</c>. This divides evenly: each group
        /// appears as a test group in exactly <c>C(N−1, k−1) = (k/N)·C(N,k)</c>
        /// splits, so the product is integral.
        /// </summary>
        public static int PathCount(int n, int k)
        {
            // Validates n/k bounds and the split ceiling.
            _ = SplitCount(n, k);
            long c = Choose(n, k);
            long numer = checked(c * k);
            // Exact: φ = k·C(N,k)/N is integral (equals C(N−1,k−1)).
            long phi = numer / n;
            return checked((int)phi);
        }

        /// <summary>
        /// Enumerate the <c>C(N,k)</c> purged + embargoed splits. Test-group
        /// combinations are generated in lexicographic order (fixed for
        /// determinism); each split's purge + embargo reuse the
        /// <see cref="PurgedKFold"/> rule with <paramref name="purgeGap"/> and
        /// <paramref name="embargo"/>.
        /// </summary>
        public static IReadOnlyList<CvSplit> Splits(
            int nObservations, int nGroups, int kTest, int purgeGap, int embargo)
        {
            if (nObservations <= 0)
                throw new ArgumentOutOfRangeException(nameof(nObservations), nObservations, "nObservations must be positive.");
            if (nGroups < 2)
                throw new ArgumentOutOfRangeException(nameof(nGroups), nGroups, "nGroups must be at least 2.");
            if (nGroups > nObservations)
                throw new ArgumentOutOfRangeException(nameof(nGroups), nGroups, "nGroups cannot exceed nObservations.");
            if (kTest < 1)
                throw new ArgumentOutOfRangeException(nameof(kTest), kTest, "kTest must be at least 1.");
            if (kTest >= nGroups)
                throw new ArgumentOutOfRangeException(nameof(kTest), kTest, "kTest must be strictly less than nGroups.");
            if (purgeGap < 0)
                throw new ArgumentOutOfRangeException(nameof(purgeGap), purgeGap, "purgeGap must be non-negative.");
            if (embargo < 0)
                throw new ArgumentOutOfRangeException(nameof(embargo), embargo, "embargo must be non-negative.");

            // Guard against OOM before allocating.
            _ = SplitCount(nGroups, kTest);

            var groupBounds = PurgedKFold.FoldBounds(nObservations, nGroups);
            var combos = Combinations(nGroups, kTest);
            var splits = new List<CvSplit>(combos.Count);

            foreach (var combo in combos)
            {
                // combo is ascending group indices ⇒ ascending, disjoint test blocks.
                var blocks = new (int a, int b)[combo.Length];
                for (int g = 0; g < combo.Length; g++) blocks[g] = groupBounds[combo[g]];
                splits.Add(PurgedKFold.BuildSplitMultiBlock(nObservations, blocks, purgeGap, embargo));
            }

            return splits;
        }

        /// <summary>
        /// Reassemble per-split OOS test returns into <c>φ[N,k]</c> full-length
        /// backtest paths (the de Prado construction) — equal-group convenience
        /// overload. Each entry of <paramref name="perSplitTestReturns"/> is one
        /// split's concatenated test-group returns (in ascending group order),
        /// supplied in the SAME order produced by <see cref="Splits"/>
        /// (lexicographic combinations). Each path covers every group index exactly
        /// once; across the φ paths every (group, split-that-tests-it) block is used
        /// exactly once, so the paths are mutually non-overlapping per group.
        /// <para>
        /// This overload assumes EQUAL group sizes: each split's block is sliced into
        /// <paramref name="kTest"/> equal sub-blocks (block length must divide by
        /// <paramref name="kTest"/>). When groups are uneven (a remainder split), use
        /// the <c>nObservations</c> sibling overload so group boundaries are exact.
        /// </para>
        /// </summary>
        public static IReadOnlyList<double[]> ReassemblePaths(
            int nGroups, int kTest, IReadOnlyList<double[]> perSplitTestReturns)
        {
            if (perSplitTestReturns is null) throw new ArgumentNullException(nameof(perSplitTestReturns));
            int expectedSplits = SplitCount(nGroups, kTest);
            if (perSplitTestReturns.Count != expectedSplits)
                throw new ArgumentException(
                    $"Expected {expectedSplits} per-split return blocks (C({nGroups},{kTest})), got {perSplitTestReturns.Count}.",
                    nameof(perSplitTestReturns));

            var combos = Combinations(nGroups, kTest);
            int pathCount = PathCount(nGroups, kTest);

            // Slice each split's flattened block into kTest equal group sub-blocks.
            var groupBlocks = new List<double[]>[nGroups];
            for (int g = 0; g < nGroups; g++) groupBlocks[g] = new List<double[]>();

            for (int s = 0; s < combos.Count; s++)
            {
                int[] combo = combos[s];
                double[] flat = perSplitTestReturns[s];
                if (flat.Length % kTest != 0)
                    throw new ArgumentException(
                        $"Split {s} block length {flat.Length} is not divisible by kTest={kTest}; " +
                        "groups are uneven — use the nObservations overload.",
                        nameof(perSplitTestReturns));
                int len = flat.Length / kTest;
                int cursor = 0;
                for (int g = 0; g < combo.Length; g++)
                {
                    var block = new double[len];
                    Array.Copy(flat, cursor, block, 0, len);
                    cursor += len;
                    groupBlocks[combo[g]].Add(block);
                }
            }

            return AssemblePathsFromGroupBlocks(groupBlocks, nGroups, pathCount);
        }

        /// <summary>
        /// Reassemble per-split OOS test returns into <c>φ[N,k]</c> full-length
        /// paths (the de Prado construction). Each path covers every group index
        /// exactly once; across the φ paths every (group, split-that-tests-it)
        /// block is used exactly once, so the paths jointly use each split's test
        /// blocks once and are mutually non-overlapping per group. The per-split
        /// inputs must be supplied in the SAME order produced by <see cref="Splits"/>
        /// (lexicographic combinations), and each entry is that split's concatenated
        /// test-group returns in ascending group order. This overload uses exact
        /// group boundaries (handles remainder/uneven groups).
        /// </summary>
        public static IReadOnlyList<double[]> ReassemblePaths(
            int nObservations, int nGroups, int kTest, IReadOnlyList<double[]> perSplitTestReturns)
        {
            if (perSplitTestReturns is null) throw new ArgumentNullException(nameof(perSplitTestReturns));
            int expectedSplits = SplitCount(nGroups, kTest);
            if (perSplitTestReturns.Count != expectedSplits)
                throw new ArgumentException(
                    $"Expected {expectedSplits} per-split return blocks (C({nGroups},{kTest})), got {perSplitTestReturns.Count}.",
                    nameof(perSplitTestReturns));

            var groupBounds = PurgedKFold.FoldBounds(nObservations, nGroups);
            var combos = Combinations(nGroups, kTest);
            int pathCount = PathCount(nGroups, kTest);

            // Slice each split's flattened test block into its kTest group sub-blocks
            // using exact group bounds.
            var groupBlocks = new List<double[]>[nGroups];
            for (int g = 0; g < nGroups; g++) groupBlocks[g] = new List<double[]>();

            for (int s = 0; s < combos.Count; s++)
            {
                int[] combo = combos[s];
                double[] flat = perSplitTestReturns[s];
                int expectedLen = 0;
                for (int g = 0; g < combo.Length; g++)
                {
                    var (a, b) = groupBounds[combo[g]];
                    expectedLen += b - a + 1;
                }
                if (flat.Length != expectedLen)
                    throw new ArgumentException(
                        $"Split {s} test block length {flat.Length} != expected {expectedLen} for its {kTest} groups.",
                        nameof(perSplitTestReturns));

                int cursor = 0;
                for (int g = 0; g < combo.Length; g++)
                {
                    var (a, b) = groupBounds[combo[g]];
                    int len = b - a + 1;
                    var block = new double[len];
                    Array.Copy(flat, cursor, block, 0, len);
                    cursor += len;
                    groupBlocks[combo[g]].Add(block);
                }
            }

            return AssemblePathsFromGroupBlocks(groupBlocks, nGroups, pathCount);
        }

        /// <summary>
        /// Build the φ paths from per-group ordered block lists. Path <c>p</c> takes
        /// the <c>p</c>-th block of every group, concatenated in ascending group
        /// order. Every group must contribute exactly <paramref name="pathCount"/>
        /// blocks.
        /// </summary>
        private static IReadOnlyList<double[]> AssemblePathsFromGroupBlocks(
            List<double[]>[] groupBlocks, int nGroups, int pathCount)
        {
            for (int g = 0; g < nGroups; g++)
            {
                if (groupBlocks[g].Count != pathCount)
                    throw new InvalidOperationException(
                        $"Group {g} appears in {groupBlocks[g].Count} test blocks, expected {pathCount}.");
            }

            var paths = new List<double[]>(pathCount);
            for (int p = 0; p < pathCount; p++)
            {
                int total = 0;
                for (int g = 0; g < nGroups; g++) total += groupBlocks[g][p].Length;
                var path = new double[total];
                int cursor = 0;
                for (int g = 0; g < nGroups; g++)
                {
                    double[] block = groupBlocks[g][p];
                    Array.Copy(block, 0, path, cursor, block.Length);
                    cursor += block.Length;
                }
                paths.Add(path);
            }

            return paths;
        }

        /// <summary>
        /// Non-annualized per-observation Sharpe of each reassembled path, routed
        /// through the canonical <see cref="SharpeStats.Sharpe"/> (the same
        /// convention DSR/PSR consume).
        /// </summary>
        public static double[] PathSharpes(IReadOnlyList<double[]> paths)
        {
            if (paths is null) throw new ArgumentNullException(nameof(paths));
            var sharpes = new double[paths.Count];
            for (int i = 0; i < paths.Count; i++) sharpes[i] = SharpeStats.Sharpe(paths[i]);
            return sharpes;
        }

        // -----------------------------------------------------------------
        // Combinatorics (overflow-checked)
        // -----------------------------------------------------------------

        /// <summary>
        /// C(n, k) via the multiplicative formula in overflow-checked
        /// <see cref="long"/> arithmetic; throws <see cref="OverflowException"/>
        /// (with a clear message) rather than wrapping silently.
        /// </summary>
        internal static long Choose(int n, int k)
        {
            if (k < 0 || k > n) return 0;
            if (k == 0 || k == n) return 1;
            if (k > n - k) k = n - k; // symmetry: minimize iterations

            long result = 1;
            try
            {
                checked
                {
                    for (int i = 1; i <= k; i++)
                    {
                        // result *= (n - k + i); result /= i; — division is exact at each step
                        result = result * (n - k + i) / i;
                    }
                }
            }
            catch (OverflowException ex)
            {
                throw new OverflowException($"C({n},{k}) overflows Int64.", ex);
            }
            return result;
        }

        /// <summary>
        /// All k-combinations of <c>{0,…,n−1}</c> in lexicographic (ascending)
        /// order — a fixed enumeration for determinism. Each combo is ascending.
        /// </summary>
        internal static List<int[]> Combinations(int n, int k)
        {
            var result = new List<int[]>();
            var combo = new int[k];
            for (int i = 0; i < k; i++) combo[i] = i;

            while (true)
            {
                result.Add((int[])combo.Clone());

                // Advance to the next lexicographic combination.
                int idx = k - 1;
                while (idx >= 0 && combo[idx] == n - k + idx) idx--;
                if (idx < 0) break;
                combo[idx]++;
                for (int j = idx + 1; j < k; j++) combo[j] = combo[j - 1] + 1;
            }

            return result;
        }
    }
}
