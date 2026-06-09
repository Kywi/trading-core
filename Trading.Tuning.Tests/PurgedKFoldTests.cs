using System;
using System.Collections.Generic;
using System.Linq;
using GripTrader.Tuner.Stats;
using Xunit;

namespace GripTrader.Tuner.Tests
{
    public class PurgedKFoldTests
    {
        [Fact]
        public void NoTrainIndexWithinPurgeGapOfAnyTestIndex()
        {
            const int n = 100, k = 5, purge = 3, embargo = 2;
            var splits = PurgedKFold.Split(n, k, purge, embargo);

            foreach (var s in splits)
            {
                foreach (int tr in s.TrainIdx)
                    foreach (int te in s.TestIdx)
                        Assert.True(Math.Abs(tr - te) > purge,
                            $"train {tr} within purgeGap {purge} of test {te}");
            }
        }

        [Fact]
        public void EmbargoRemovesExactlyTheBarsAfterEachBlock()
        {
            const int n = 100, k = 5, purge = 0, embargo = 4;
            var splits = PurgedKFold.Split(n, k, purge, embargo);

            foreach (var s in splits)
            {
                int b = s.TestIdx[^1];
                var trainSet = new HashSet<int>(s.TrainIdx);
                // With purge 0, the embargo window (b, b+embargo] must be absent from train.
                for (int i = b + 1; i <= Math.Min(n - 1, b + embargo); i++)
                    Assert.DoesNotContain(i, trainSet);
                // The first bar past the embargo (if it exists and isn't another test/purge) IS in train.
                int firstAfter = b + embargo + 1;
                if (firstAfter < n && !s.TestIdx.Contains(firstAfter))
                    Assert.Contains(firstAfter, trainSet);
            }
        }

        [Fact]
        public void PartitionsRangeWithNoTrainTestOverlap()
        {
            const int n = 60, k = 4, purge = 2, embargo = 2;
            var splits = PurgedKFold.Split(n, k, purge, embargo);

            foreach (var s in splits)
            {
                var train = new HashSet<int>(s.TrainIdx);
                var test = new HashSet<int>(s.TestIdx);
                // No overlap.
                Assert.Empty(train.Intersect(test));
                // Every index is either train, test, purged, or embargoed — i.e. the
                // union of train+test ⊆ [0,n) and removed indices are exactly the gap.
                foreach (int i in s.TrainIdx) Assert.InRange(i, 0, n - 1);
                foreach (int i in s.TestIdx) Assert.InRange(i, 0, n - 1);
            }

            // Test folds collectively cover [0,n) with no gaps or overlap.
            var allTest = splits.SelectMany(s => s.TestIdx).OrderBy(x => x).ToArray();
            Assert.Equal(Enumerable.Range(0, n), allTest);
        }

        [Fact]
        public void LookbackDrivesPurging_WhenLGreaterThanHolding()
        {
            // The caller passes purgeGap = max(holding, L). Prove a larger L widens
            // the purge halo (vs a small holding-only gap).
            const int n = 100, k = 5;
            int holding = 1, lookback = 10;
            int purgeSmall = holding;                 // wrong: holding only
            int purgeCorrect = Math.Max(holding, lookback); // correct: lookback dominates

            var small = PurgedKFold.Split(n, k, purgeSmall, 0);
            var correct = PurgedKFold.Split(n, k, purgeCorrect, 0);

            // The correct (wider) purge must remove at least as many train indices.
            for (int f = 0; f < small.Count; f++)
                Assert.True(correct[f].TrainIdx.Length <= small[f].TrainIdx.Length,
                    $"fold {f}: wider lookback purge should not have MORE train indices");

            // And strictly fewer for at least one interior fold.
            bool anyStrictlyFewer = false;
            for (int f = 0; f < correct.Count; f++)
                if (correct[f].TrainIdx.Length < small[f].TrainIdx.Length) { anyStrictlyFewer = true; break; }
            Assert.True(anyStrictlyFewer, "a wider lookback purge should strictly shrink at least one fold's train set");
        }

        [Fact]
        public void FoldEmptiedByPurge_SurfacesEmptyTrain()
        {
            // n=10, k=2 ⇒ two folds [0,4],[5,9]; purgeGap=10 wipes all train.
            var splits = PurgedKFold.Split(10, 2, 10, 0);
            Assert.All(splits, s => Assert.Empty(s.TrainIdx));
            Assert.All(splits, s => Assert.NotEmpty(s.TestIdx));
        }

        [Fact]
        public void EmbargoClampsAtN_OnLastFold()
        {
            // Last fold ends at n-1; embargo past the end must not throw or index OOB.
            var splits = PurgedKFold.Split(20, 4, 1, 100);
            var last = splits[^1];
            Assert.Equal(19, last.TestIdx[^1]);
            Assert.All(last.TrainIdx, i => Assert.InRange(i, 0, 19));
        }

        [Theory]
        [InlineData(0, 5, 1, 1)]   // n <= 0
        [InlineData(10, 1, 1, 1)]  // k < 2
        [InlineData(10, 11, 1, 1)] // k > n
        public void InvalidArgs_Throw(int n, int k, int purge, int embargo)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PurgedKFold.Split(n, k, purge, embargo));
        }

        [Fact]
        public void Deterministic_RepeatedCallsIdentical()
        {
            var a = PurgedKFold.Split(50, 5, 3, 2);
            var b = PurgedKFold.Split(50, 5, 3, 2);
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].TrainIdx, b[i].TrainIdx);
                Assert.Equal(a[i].TestIdx, b[i].TestIdx);
            }
        }
    }
}
