using System;
using System.Collections.Generic;
using System.Linq;
using GripTrader.Tuner.Stats;
using Xunit;

namespace GripTrader.Tuner.Tests
{
    public class CombinatorialPurgedCvTests
    {
        // ----- The exact-integer PathCount must-pass assertions ------------

        [Theory]
        [InlineData(6, 2, 5)]   // φ[6,2] = (2/6)·C(6,2) = (2/6)·15 = 5
        [InlineData(10, 2, 9)]  // (2/10)·45 = 9
        [InlineData(5, 2, 4)]   // (2/5)·10 = 4
        public void PathCount_ExactInteger(int n, int k, int expected)
        {
            Assert.Equal(expected, CombinatorialPurgedCv.PathCount(n, k));
        }

        [Theory]
        [InlineData(6, 2, 15)]
        [InlineData(10, 2, 45)]
        [InlineData(8, 3, 56)]
        public void SplitCount_EqualsChoose(int n, int k, int expected)
        {
            Assert.Equal(expected, CombinatorialPurgedCv.SplitCount(n, k));
        }

        // ----- C(n,k) overflow guard throws (does NOT wrap) ----------------

        [Fact]
        public void Choose_Overflow_Throws()
        {
            // C(100, 50) overflows Int64.
            Assert.Throws<OverflowException>(() => CombinatorialPurgedCv.Choose(100, 50));
        }

        [Fact]
        public void SplitCount_AboveCeiling_Throws()
        {
            // C(40,20) = 137846528820 >> 100k ceiling.
            Assert.Throws<ArgumentOutOfRangeException>(() => CombinatorialPurgedCv.SplitCount(40, 20));
        }

        [Theory]
        [InlineData(1, 1)]   // n < 2
        [InlineData(5, 0)]   // k < 1
        [InlineData(5, 5)]   // k >= n
        public void SplitCount_InvalidArgs_Throw(int n, int k)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CombinatorialPurgedCv.SplitCount(n, k));
        }

        // ----- Splits are purged + embargoed -------------------------------

        [Fact]
        public void EachSplit_IsPurgedAndEmbargoed()
        {
            const int n = 60, groups = 6, k = 2, purge = 2, embargo = 2;
            var splits = CombinatorialPurgedCv.Splits(n, groups, k, purge, embargo);
            Assert.Equal(CombinatorialPurgedCv.SplitCount(groups, k), splits.Count);

            foreach (var s in splits)
            {
                var test = new HashSet<int>(s.TestIdx);
                foreach (int tr in s.TrainIdx)
                {
                    foreach (int te in s.TestIdx)
                        Assert.True(Math.Abs(tr - te) > purge,
                            $"train {tr} within purge {purge} of test {te}");
                    Assert.DoesNotContain(tr, test);
                }
            }
        }

        // ----- Reassembled paths: full-length & non-overlapping ------------

        [Fact]
        public void ReassemblePaths_FullLengthAndCoverEveryGroupOnce()
        {
            // Equal groups: n divisible by groups.
            const int n = 60, groups = 6, k = 2;
            var splits = CombinatorialPurgedCv.Splits(n, groups, k, 0, 0);

            // Build per-split test returns = the test indices themselves (as doubles)
            // so we can verify coverage exactly.
            var perSplit = splits.Select(s => s.TestIdx.Select(i => (double)i).ToArray()).ToArray();

            var paths = CombinatorialPurgedCv.ReassemblePaths(n, groups, k, perSplit);

            int expectedPaths = CombinatorialPurgedCv.PathCount(groups, k);
            Assert.Equal(expectedPaths, paths.Count);

            foreach (var path in paths)
            {
                // Full-length: a path covers every one of the n observations exactly once.
                Assert.Equal(n, path.Length);
                var covered = path.Select(d => (int)d).OrderBy(x => x).ToArray();
                Assert.Equal(Enumerable.Range(0, n), covered);
            }
        }

        [Fact]
        public void ReassemblePaths_EqualGroupOverload_MatchesExactOverload()
        {
            const int n = 60, groups = 6, k = 2;
            var splits = CombinatorialPurgedCv.Splits(n, groups, k, 0, 0);
            var perSplit = splits.Select(s => s.TestIdx.Select(i => (double)i).ToArray()).ToArray();

            var exact = CombinatorialPurgedCv.ReassemblePaths(n, groups, k, perSplit);
            var equal = CombinatorialPurgedCv.ReassemblePaths(groups, k, perSplit);

            Assert.Equal(exact.Count, equal.Count);
            for (int p = 0; p < exact.Count; p++)
                Assert.Equal(exact[p], equal[p]);
        }

        [Fact]
        public void ReassemblePaths_ExactOverload_HandlesRemainderGroups()
        {
            // n=10, groups=3 ⇒ sizes 4,3,3 (remainder to earliest). k=1.
            const int n = 10, groups = 3, k = 1;
            var splits = CombinatorialPurgedCv.Splits(n, groups, k, 0, 0);
            var perSplit = splits.Select(s => s.TestIdx.Select(i => (double)i).ToArray()).ToArray();

            var paths = CombinatorialPurgedCv.ReassemblePaths(n, groups, k, perSplit);
            Assert.Equal(CombinatorialPurgedCv.PathCount(groups, k), paths.Count);
            foreach (var path in paths)
            {
                Assert.Equal(n, path.Length);
                Assert.Equal(Enumerable.Range(0, n), path.Select(d => (int)d).OrderBy(x => x));
            }
        }

        [Fact]
        public void PathSharpes_RoutesThroughCanonicalSharpe()
        {
            var paths = new List<double[]>
            {
                new double[] { 0.01, -0.02, 0.03 },
                new double[] { 0.0, 0.0, 0.0 },
            };
            var sharpes = CombinatorialPurgedCv.PathSharpes(paths);
            Assert.Equal(SharpeStats.Sharpe(paths[0]), sharpes[0], 15);
            Assert.Equal(0.0, sharpes[1]); // zero variance ⇒ 0
        }

        [Fact]
        public void Deterministic_SplitsAndPaths_Identical()
        {
            const int n = 48, groups = 4, k = 2;
            var a = CombinatorialPurgedCv.Splits(n, groups, k, 1, 1);
            var b = CombinatorialPurgedCv.Splits(n, groups, k, 1, 1);
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].TrainIdx, b[i].TrainIdx);
                Assert.Equal(a[i].TestIdx, b[i].TestIdx);
            }
        }
    }
}
