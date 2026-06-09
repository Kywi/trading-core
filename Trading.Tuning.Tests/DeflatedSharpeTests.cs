using System;
using System.Collections.Generic;
using GripTrader.Tuner.Stats;
using Xunit;

namespace GripTrader.Tuner.Tests
{
    public class DeflatedSharpeTests
    {
        // ----- The kurtosis silent-flip guard (THE trap) -------------------

        [Fact]
        public void PsrRadicand_AtGaussianKurtosis_HasHalfTerm()
        {
            // At γ₄ = 3 (Gaussian, NON-excess), the variance term (γ₄−1)/4 = 0.5,
            // so the radicand must equal 1 − γ₃·SR̂ + 0.5·SR̂².
            double sr = 1.3, skew = -0.4;
            double radicand = DeflatedSharpe.PsrRadicand(sr, skew, 3.0);
            double expected = 1.0 - skew * sr + 0.5 * sr * sr;
            Assert.Equal(expected, radicand, 15);

            // Explicitly: the coefficient on SR̂² is exactly 0.5 at γ₄=3.
            Assert.Equal(0.5, (3.0 - 1.0) / 4.0, 15);
        }

        [Fact]
        public void PsrRadicand_WrongConvention_WouldDiffer()
        {
            // Guard: passing EXCESS kurtosis (γ₄_excess = 0 for Gaussian) instead of
            // non-excess (=3) flips the term to (0−1)/4 = −0.25 ≠ 0.5. This proves
            // the convention is load-bearing — the radicands differ.
            double sr = 1.5, skew = 0.0;
            double correct = DeflatedSharpe.PsrRadicand(sr, skew, 3.0);   // non-excess
            double wrong = DeflatedSharpe.PsrRadicand(sr, skew, 0.0);     // excess (mistake)
            Assert.NotEqual(correct, wrong, 6);
            Assert.Equal(1.0 + 0.5 * sr * sr, correct, 12);
            Assert.Equal(1.0 - 0.25 * sr * sr, wrong, 12);
        }

        // ----- DSR ∈ [0,1] on a grid ---------------------------------------

        [Fact]
        public void Dsr_InUnitInterval_OnGrid()
        {
            foreach (double sr in new[] { -0.5, 0.0, 0.5, 1.0, 2.0, 5.0 })
                foreach (double skew in new[] { -1.0, 0.0, 1.0 })
                    foreach (double kurt in new[] { 3.0, 5.0, 9.0 })
                        foreach (int t in new[] { 2, 50, 1000 })
                            foreach (double sr0 in new[] { 0.0, 0.5, 1.5 })
                            {
                                double dsr = DeflatedSharpe.Deflated(sr, skew, kurt, t, sr0);
                                Assert.InRange(dsr, 0.0, 1.0);
                                Assert.True(double.IsFinite(dsr));
                            }
        }

        // ----- DSR ≈ 0.5 when SR̂ == SR₀ -----------------------------------

        [Fact]
        public void Dsr_IsHalf_WhenSrHatEqualsSr0()
        {
            // Numerator (SR̂ − SR₀) = 0 ⇒ z = 0 ⇒ Φ(0) = 0.5 regardless of moments/T.
            // Tolerance is the A&S erf approximation error (~1.5e-7), not machine eps.
            double sr = 1.234;
            double dsr = DeflatedSharpe.Deflated(sr, 0.3, 4.5, 500, sr);
            Assert.Equal(0.5, dsr, 6);
        }

        // ----- Raising N raises SR₀ and lowers DSR -------------------------

        [Fact]
        public void MoreTrials_RaiseSr0_LowerDsr()
        {
            double v = 0.04; // trial Sharpe variance
            double sr0Small = DeflatedSharpe.ExpectedMaxSharpe(10, v);
            double sr0Large = DeflatedSharpe.ExpectedMaxSharpe(1000, v);
            Assert.True(sr0Large > sr0Small, $"SR₀ should rise with N: {sr0Small} -> {sr0Large}");

            // A fixed observed Sharpe ⇒ more trials deflates harder. Use a small T so
            // the PSR Φ-argument stays in the sensitive (unsaturated) region — with a
            // large T the probability pins to 1 for any positive numerator and the
            // (real) monotonic effect is invisible to floating point.
            double srHat = 0.5, skew = 0.0, kurt = 3.0; int t = 10;
            double dsrSmall = DeflatedSharpe.Deflated(srHat, skew, kurt, t, sr0Small);
            double dsrLarge = DeflatedSharpe.Deflated(srHat, skew, kurt, t, sr0Large);
            Assert.True(dsrLarge < dsrSmall, $"more trials should lower DSR: {dsrSmall} -> {dsrLarge}");
            Assert.InRange(dsrSmall, 0.0, 1.0);
            Assert.InRange(dsrLarge, 0.0, 1.0);
        }

        // ----- SR̂ below its deflated null ⇒ DSR < 0.5 (guards the >0 mistake)

        [Fact]
        public void SrHatBelowSr0_GivesDsrBelowHalf()
        {
            double sr0 = DeflatedSharpe.ExpectedMaxSharpe(200, 0.05);
            Assert.True(sr0 > 0);
            double srHat = sr0 * 0.5; // observed below the deflated null
            double dsr = DeflatedSharpe.Deflated(srHat, 0.0, 3.0, 500, sr0);
            Assert.True(dsr < 0.5, $"DSR for SR̂ below SR₀ should be < 0.5, got {dsr}");
            // It is still > 0 — which is exactly why a `> 0` gate is meaningless.
            Assert.True(dsr > 0.0);
        }

        // ----- ExpectedMaxSharpe edge cases --------------------------------

        [Theory]
        [InlineData(0, 0.04)]
        [InlineData(1, 0.04)]
        public void ExpectedMaxSharpe_NLeqOne_IsZero(int n, double v)
        {
            Assert.Equal(0.0, DeflatedSharpe.ExpectedMaxSharpe(n, v));
        }

        [Theory]
        [InlineData(100, 0.0)]
        [InlineData(100, -1.0)]
        public void ExpectedMaxSharpe_NonPositiveVariance_IsZero(int n, double v)
        {
            Assert.Equal(0.0, DeflatedSharpe.ExpectedMaxSharpe(n, v));
        }

        [Fact]
        public void Psr_TLessThanTwo_IsZero()
        {
            Assert.Equal(0.0, DeflatedSharpe.ProbabilisticSharpe(1.0, 0.0, 3.0, 1, 0.0));
        }

        // ----- Inverse CDF round-trip and Φ⁻¹(0.5)=0 -----------------------

        [Fact]
        public void InverseCdf_RoundTrip()
        {
            foreach (double x in new[] { -2.5, -1.0, -0.3, 0.3, 1.0, 2.5 })
            {
                double p = NormalDist.Cdf(x);
                double back = NormalDist.InverseCdf(p);
                Assert.Equal(x, back, 3); // combined Φ/Φ⁻¹ approximation tolerance
            }
        }

        [Fact]
        public void InverseCdf_Half_IsZero()
        {
            Assert.Equal(0.0, NormalDist.InverseCdf(0.5), 9);
        }

        [Fact]
        public void InverseCdf_KnownQuantiles()
        {
            Assert.Equal(1.6448536269514722, NormalDist.InverseCdf(0.95), 4);
            Assert.Equal(-1.6448536269514722, NormalDist.InverseCdf(0.05), 4);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        [InlineData(-0.1)]
        [InlineData(1.1)]
        public void InverseCdf_OutOfRange_Throws(double p)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => NormalDist.InverseCdf(p));
        }

        // ----- End-to-end DSR from canonical series ------------------------

        [Fact]
        public void Dsr_FromCanonicalSeries_RoutesThroughMoments()
        {
            var returns = SharpeStatsTests.DeterministicGaussian(2000);
            var m = SharpeStats.Moments(returns);
            double v = 0.03;
            double sr0 = DeflatedSharpe.ExpectedMaxSharpe(50, v);

            double endToEnd = DeflatedSharpe.Deflated(returns, 50, v);
            double manual = DeflatedSharpe.ProbabilisticSharpe(m.Sr, m.Skew, m.Kurtosis, m.T, sr0);
            Assert.Equal(manual, endToEnd, 15);
        }

        // ----- Determinism: bit-identical ----------------------------------

        [Fact]
        public void Determinism_BitIdentical()
        {
            double a = DeflatedSharpe.Deflated(1.1, 0.2, 4.0, 300, 0.7);
            double b = DeflatedSharpe.Deflated(1.1, 0.2, 4.0, 300, 0.7);
            Assert.Equal(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(b));
        }
    }
}
