using System;
using System.Collections.Generic;
using GripTrader.Tuner.Stats;
using Xunit;

namespace GripTrader.Tuner.Tests
{
    public class SharpeStatsTests
    {
        // ----- Hand-computed tiny series -----------------------------------

        [Fact]
        public void Moments_HandComputed_TinySeries()
        {
            // r = {1, 2, 3, 4, 5}; mean = 3.
            // sample var = Σ(r-3)²/(5-1) = (4+1+0+1+4)/4 = 10/4 = 2.5; sd = √2.5.
            // SR̂ = 3 / √2.5.
            // population var = 10/5 = 2 ; σ = √2.
            // γ₃: Σ(r-3)³ = (-8 + -1 + 0 + 1 + 8) = 0 ⇒ skew = 0 (symmetric).
            // γ₄: Σ(r-3)⁴ = (16 + 1 + 0 + 1 + 16) = 34 ; (34/5)/σ⁴ = 6.8/4 = 1.7.
            var r = new double[] { 1, 2, 3, 4, 5 };
            var m = SharpeStats.Moments(r);

            Assert.Equal(5, m.T);
            Assert.Equal(3.0 / Math.Sqrt(2.5), m.Sr, 12);
            Assert.Equal(0.0, m.Skew, 12);
            Assert.Equal(1.7, m.Kurtosis, 12); // NON-excess
        }

        [Fact]
        public void Sharpe_MatchesMomentsSr()
        {
            var r = new double[] { 0.01, -0.02, 0.03, 0.005, -0.001 };
            Assert.Equal(SharpeStats.Moments(r).Sr, SharpeStats.Sharpe(r), 15);
        }

        [Fact]
        public void Skew_PositivelySkewedSeries_IsPositive()
        {
            // Right tail: most values small, one large positive ⇒ γ₃ > 0.
            var r = new double[] { 0, 0, 0, 0, 10 };
            var m = SharpeStats.Moments(r);
            Assert.True(m.Skew > 0, $"expected positive skew, got {m.Skew}");
        }

        // ----- The load-bearing kurtosis convention ------------------------

        [Fact]
        public void Gaussian_NonExcessKurtosis_NearThree_So_Kurt1Over4_IsHalf()
        {
            // Deterministic standard-normal sample via Box-Muller on a fixed grid
            // (NO RNG — the replay path stays deterministic and the test is exact
            // across runs). Large N so γ₄ → 3.
            var sample = DeterministicGaussian(20000);
            var m = SharpeStats.Moments(sample);

            // NON-excess kurtosis ≈ 3.
            Assert.True(Math.Abs(m.Kurtosis - 3.0) < 0.1,
                $"expected γ₄ ≈ 3, got {m.Kurtosis}");
            // The PSR variance term (γ₄−1)/4 ≈ 0.5 — the silent-flip guard.
            double term = (m.Kurtosis - 1.0) / 4.0;
            Assert.True(Math.Abs(term - 0.5) < 0.025,
                $"expected (γ₄−1)/4 ≈ 0.5, got {term}");
        }

        // ----- Edge cases never NaN/Inf ------------------------------------

        [Fact]
        public void Empty_Defaults()
        {
            var m = SharpeStats.Moments(Array.Empty<double>());
            Assert.Equal(0, m.T);
            Assert.Equal(0.0, m.Sr);
            Assert.Equal(0.0, m.Skew);
            Assert.Equal(3.0, m.Kurtosis);
        }

        [Fact]
        public void SingleObservation_Defaults()
        {
            var m = SharpeStats.Moments(new double[] { 0.42 });
            Assert.Equal(1, m.T);
            Assert.Equal(0.0, m.Sr);
            Assert.Equal(0.0, m.Skew);
            Assert.Equal(3.0, m.Kurtosis);
        }

        [Fact]
        public void ZeroVariance_Defaults()
        {
            var m = SharpeStats.Moments(new double[] { 5, 5, 5, 5 });
            Assert.Equal(4, m.T);
            Assert.Equal(0.0, m.Sr);
            Assert.Equal(0.0, m.Skew);
            Assert.Equal(3.0, m.Kurtosis);
        }

        [Fact]
        public void NoNaNorInf_OnPathologicalInputs()
        {
            foreach (var series in new[]
            {
                new double[] { 0, 0, 0 },
                new double[] { 1e-300, -1e-300 },
                new double[] { 1e10, -1e10, 5e9 },
            })
            {
                var m = SharpeStats.Moments(series);
                Assert.True(double.IsFinite(m.Sr));
                Assert.True(double.IsFinite(m.Skew));
                Assert.True(double.IsFinite(m.Kurtosis));
            }
        }

        // ----- NormalDist.Cdf (via reflection-free internal access) --------

        [Fact]
        public void Cdf_KnownValues()
        {
            Assert.Equal(0.5, NormalDist.Cdf(0.0), 6);
            Assert.Equal(0.95, NormalDist.Cdf(1.6448536269514722), 3); // Φ⁻¹(0.95)
            Assert.Equal(0.975, NormalDist.Cdf(1.959963984540054), 3); // Φ⁻¹(0.975)
        }

        [Fact]
        public void Cdf_Symmetry()
        {
            for (double z = 0.0; z <= 3.0; z += 0.5)
            {
                Assert.Equal(1.0, NormalDist.Cdf(z) + NormalDist.Cdf(-z), 6);
            }
        }

        // ----- Determinism: bit-identical repeated calls -------------------

        [Fact]
        public void Moments_BitIdentical_OnRepeat()
        {
            var r = DeterministicGaussian(5000);
            var a = SharpeStats.Moments(r);
            var b = SharpeStats.Moments(r);
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Sr), BitConverter.DoubleToInt64Bits(b.Sr));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Skew), BitConverter.DoubleToInt64Bits(b.Skew));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Kurtosis), BitConverter.DoubleToInt64Bits(b.Kurtosis));
            Assert.Equal(a.T, b.T);
        }

        // ----- Deterministic Gaussian generator (no RNG) -------------------

        /// <summary>
        /// Box–Muller transform driven by a FIXED-SEED linear congruential generator
        /// (the classic Numerical Recipes constants). This is deterministic — the
        /// seed is hard-coded, not wall-clock / system RNG — so the sample is
        /// byte-identical every run, yet it is a genuine pseudo-random normal sample
        /// whose empirical non-excess kurtosis converges to 3 (a quasi-random / equi-
        /// probable grid does NOT — its kurtosis under-shoots). Test fixture only;
        /// this never runs on the backtest replay path.
        /// </summary>
        internal static double[] DeterministicGaussian(int n)
        {
            var outv = new double[n];
            ulong state = 0x9E3779B97F4A7C15UL; // fixed seed
            ulong NextU()
            {
                // 64-bit LCG (Knuth MMIX multiplier/increment).
                state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
                return state;
            }
            double NextUniform()
            {
                // Top 53 bits → uniform in (0,1).
                ulong bits = NextU() >> 11;
                double u = (bits + 0.5) / 9007199254740992.0; // 2^53
                return u;
            }
            for (int i = 0; i < n; i += 2)
            {
                double u1 = NextUniform();
                double u2 = NextUniform();
                if (u1 < 1e-300) u1 = 1e-300;
                double rmag = Math.Sqrt(-2.0 * Math.Log(u1));
                double theta = 2.0 * Math.PI * u2;
                outv[i] = rmag * Math.Cos(theta);
                if (i + 1 < n) outv[i + 1] = rmag * Math.Sin(theta);
            }
            return outv;
        }
    }
}
