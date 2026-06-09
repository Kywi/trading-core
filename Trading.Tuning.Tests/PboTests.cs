using System;
using System.Collections.Generic;
using GripTrader.Tuner.Stats;
using Xunit;

namespace GripTrader.Tuner.Tests
{
    public class PboTests
    {
        // ----- Best IS == Best OOS ⇒ PBO ≈ 0 ------------------------------

        [Fact]
        public void BestIsAlsoBestOos_PboNearZero()
        {
            // Config 0 dominates every partition ⇒ it is always picked IS and always
            // ranks #1 OOS ⇒ logit > 0 on every split ⇒ PBO = 0.
            var matrix = new[]
            {
                new double[] { 10, 10, 10, 10, 10, 10 }, // config 0 — always best
                new double[] { 1, 2, 1, 2, 1, 2 },
                new double[] { 0, 1, 0, 1, 0, 1 },
                new double[] { -1, 0, -1, 0, -1, 0 },
            };
            var res = Pbo.Compute(matrix);
            Assert.Equal(0.0, res.Pbo, 12);
        }

        // ----- Best IS == Worst OOS ⇒ PBO ≈ 1 -----------------------------

        [Fact]
        public void BestIsWorstOos_PboNearOne()
        {
            // An overfit config: it wins on the IS partitions but is the worst on the
            // complementary OOS partitions. Construct so that whichever half is IS,
            // the IS-winner is OOS-worst. Use a config that is huge on even partitions
            // and tiny on odd ones, paired with a mirror — but the cleanest robust
            // construction is: config 0 is best on EVERY single partition individually
            // is the opposite case. Instead make config 0's per-partition values
            // anti-correlated between halves via a high IS-mean, low-elsewhere shape
            // that flips. The canonical overfit example: each config is a near-random
            // step that happens to peak IS. We force it deterministically:
            //
            // partitions p0..p5. For a balanced 3/3 split, there are C(6,3)=20 splits.
            // We make config 0 = [100,100,100,-100,-100,-100], config 1 = mirror, and
            // fillers in between. On the split IS={0,1,2}, config 0 wins IS (mean 100)
            // and is worst OOS (mean -100) ⇒ logit < 0. On IS={3,4,5}, config 1 wins
            // and is worst OOS. Mixed splits split the difference. The point: the
            // IS-winner is consistently OOS-poor ⇒ high PBO.
            var matrix = new[]
            {
                new double[] { 100, 100, 100, -100, -100, -100 },
                new double[] { -100, -100, -100, 100, 100, 100 },
            };
            var res = Pbo.Compute(matrix);
            // The IS winner is always the config peaking on the IS half, which is the
            // OOS-worst on the complementary half for the pure-half splits.
            Assert.True(res.Pbo >= 0.5, $"expected high PBO for overfit pair, got {res.Pbo}");
        }

        // ----- Fixed matrix ⇒ PBO ∈ [0,1] ----------------------------------

        [Fact]
        public void Pbo_InUnitInterval()
        {
            var matrix = new[]
            {
                new double[] { 0.5, 0.2, 0.8, 0.1, 0.9, 0.3 },
                new double[] { 0.3, 0.7, 0.2, 0.6, 0.1, 0.8 },
                new double[] { 0.6, 0.4, 0.5, 0.5, 0.4, 0.6 },
            };
            var res = Pbo.Compute(matrix);
            Assert.InRange(res.Pbo, 0.0, 1.0);
            Assert.NotEmpty(res.Logits);
        }

        // ----- No information ⇒ PBO = 0.5 (the coin-flip line) -------------

        [Fact]
        public void NoInformation_PboIsHalf()
        {
            // Every config identical on every partition ⇒ no information about
            // overfitting. The IS-best ties all others OOS ⇒ mid-rank ⇒ ω = 0.5 ⇒
            // λ = 0 on every split ⇒ PBO = 0.5 exactly, not a boundary value. This
            // locks the mid-rank + half-mass mapping (a lower-index tie-break would
            // wrongly pin this to 0).
            var matrix = new[]
            {
                new double[] { 1, 1, 1, 1 },
                new double[] { 1, 1, 1, 1 },
                new double[] { 1, 1, 1, 1 },
            };
            var res = Pbo.Compute(matrix);
            Assert.Equal(0.5, res.Pbo, 12);
        }

        // ----- Odd partition count rejected -------------------------------

        [Fact]
        public void OddPartitionCount_Throws()
        {
            // CSCV needs an even partition count for a symmetric IS/OOS split.
            var matrix = new[]
            {
                new double[] { 1, 2, 3 },
                new double[] { 3, 2, 1 },
            };
            Assert.Throws<ArgumentException>(() => Pbo.Compute(matrix));
        }

        // ----- Metric swap changes the logit ------------------------------

        [Fact]
        public void DifferentMetricMatrix_ChangesPbo()
        {
            // Metric A: config 0 generalizes (consistent OOS winner) ⇒ low PBO.
            var metricA = new[]
            {
                new double[] { 5, 5, 5, 5, 5, 5 },
                new double[] { 1, 2, 1, 2, 1, 2 },
                new double[] { 0, 1, 0, 1, 0, 1 },
            };
            // Metric B (a DIFFERENT metric over the same configs): the IS-winner is
            // OOS-poor (anti-correlated halves) ⇒ higher PBO. Swapping the metric the
            // harness optimizes by must move the verdict — that is the whole point of
            // ranking PBO on the SAME metric that selected the IS winner.
            var metricB = new[]
            {
                new double[] { 9, 9, 9, -9, -9, -9 },
                new double[] { -9, -9, -9, 9, 9, 9 },
                new double[] { 0, 0, 0, 0, 0, 0 },
            };
            var a = Pbo.Compute(metricA);
            var b = Pbo.Compute(metricB);
            Assert.NotEqual(a.Pbo, b.Pbo);
            Assert.True(a.Pbo < b.Pbo, $"generalizing metric should give lower PBO: {a.Pbo} vs {b.Pbo}");
        }

        // ----- Tie-break determinism ---------------------------------------

        [Fact]
        public void Ties_DeterministicAndRepeatable()
        {
            // All configs identical ⇒ ties everywhere; result must be stable.
            var matrix = new[]
            {
                new double[] { 1, 1, 1, 1 },
                new double[] { 1, 1, 1, 1 },
                new double[] { 1, 1, 1, 1 },
            };
            var a = Pbo.Compute(matrix);
            var b = Pbo.Compute(matrix);
            Assert.Equal(a.Pbo, b.Pbo);
            Assert.Equal(a.Logits, b.Logits);
        }

        // ----- Fewer than 2 configs ⇒ PBO 0 (documented) -------------------

        [Fact]
        public void SingleConfig_PboZero()
        {
            var matrix = new[] { new double[] { 1, 2, 3, 4 } };
            var res = Pbo.Compute(matrix);
            Assert.Equal(0.0, res.Pbo);
            Assert.Empty(res.Logits);
        }

        // ----- Ragged / empty throw ----------------------------------------

        [Fact]
        public void Ragged_Throws()
        {
            var matrix = new[]
            {
                new double[] { 1, 2, 3, 4 },
                new double[] { 1, 2, 3 }, // ragged
            };
            Assert.Throws<ArgumentException>(() => Pbo.Compute(matrix));
        }

        [Fact]
        public void Empty_Throws()
        {
            Assert.Throws<ArgumentException>(() => Pbo.Compute(Array.Empty<double[]>()));
        }

        [Fact]
        public void TooFewPartitions_Throws()
        {
            var matrix = new[]
            {
                new double[] { 1 },
                new double[] { 2 },
            };
            Assert.Throws<ArgumentException>(() => Pbo.Compute(matrix));
        }

        [Fact]
        public void Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => Pbo.Compute(null!));
        }
    }
}
