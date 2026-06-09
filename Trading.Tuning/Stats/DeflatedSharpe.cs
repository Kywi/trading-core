using System;
using System.Collections.Generic;

namespace GripTrader.Tuner.Stats
{
    /// <summary>
    /// Probabilistic Sharpe Ratio (PSR) and Deflated Sharpe Ratio (DSR)
    /// (validation primitive 3) — the trap-heavy one.
    ///
    /// <para>
    /// <b>The kurtosis trap (load-bearing):</b> the PSR variance term uses
    /// <c>(γ₄ − 1)/4</c> with γ₄ the <b>NON-EXCESS</b> kurtosis (Gaussian γ₄ = 3,
    /// so <c>(3 − 1)/4 = 0.5</c>). Passing <i>excess</i> kurtosis here silently
    /// halves/flips the variance term. The kurtosis parameters are named
    /// <c>kurtosisNonExcess</c> to keep the convention explicit; feed γ₄ straight
    /// from <see cref="SharpeStats.Moments"/> (which produces non-excess γ₄).
    /// </para>
    ///
    /// <para>
    /// <b>DSR is a probability in [0,1]</b> — it is PSR evaluated at the deflated
    /// benchmark <c>SR₀</c> (the expected maximum Sharpe under the null across
    /// <c>N</c> trials). <c>DSR = 0.5</c> means SR̂ sits exactly on the deflated
    /// null. A <c>&gt; 0</c> gate is meaningless; the project's gate is DSR ≥ 0.95
    /// (decided bot-side, not here).
    /// </para>
    ///
    /// All quantities are <see cref="double"/> (statistics carve-out). BCL-only:
    /// Φ and Φ⁻¹ come from <see cref="NormalDist"/>.
    /// </summary>
    public static class DeflatedSharpe
    {
        /// <summary>
        /// Tiny floor for the PSR variance radicand when fat tails / strong skew
        /// drive it non-positive. Clamping (rather than returning NaN) keeps the
        /// statistic finite; documented as a degenerate-input safeguard.
        /// </summary>
        internal const double RadicandEpsilon = 1e-12;

        /// <summary>
        /// Probabilistic Sharpe Ratio: the probability that the true Sharpe exceeds
        /// the benchmark <paramref name="srBenchmark"/>, given the observed
        /// non-annualized Sharpe and higher moments.
        /// <code>
        /// PSR(SR*) = Φ( (SR̂ − SR*)·√(T−1) / √(1 − γ₃·SR̂ + ((γ₄−1)/4)·SR̂²) )
        /// </code>
        /// </summary>
        /// <param name="srHat">Observed non-annualized per-observation Sharpe SR̂.</param>
        /// <param name="skew">γ₃ (third standardized moment).</param>
        /// <param name="kurtosisNonExcess">γ₄ NON-excess (Gaussian = 3) — NOT excess kurtosis.</param>
        /// <param name="t">Observation count T.</param>
        /// <param name="srBenchmark">Benchmark Sharpe SR* (0 for the classic PSR; SR₀ for DSR).</param>
        /// <returns>A probability in [0,1]; 0 when T &lt; 2.</returns>
        public static double ProbabilisticSharpe(
            double srHat, double skew, double kurtosisNonExcess, int t, double srBenchmark)
        {
            if (t < 2) return 0.0;

            double radicand = PsrRadicand(srHat, skew, kurtosisNonExcess);
            if (radicand < RadicandEpsilon) radicand = RadicandEpsilon;

            double numerator = (srHat - srBenchmark) * Math.Sqrt(t - 1);
            double z = numerator / Math.Sqrt(radicand);
            double p = NormalDist.Cdf(z);
            return Clamp01(p);
        }

        /// <summary>
        /// The expected maximum Sharpe under the null across <paramref name="nTrials"/>
        /// independent trials (the DSR benchmark SR₀):
        /// <code>
        /// SR₀ = √v · ( (1 − γₑ)·Φ⁻¹(1 − 1/N) + γₑ·Φ⁻¹(1 − 1/(N·e)) )
        /// </code>
        /// where <c>γₑ ≈ 0.5772156649</c> (Euler–Mascheroni), <c>v</c> is the
        /// variance of the trial Sharpes, and <c>e</c> is Euler's number.
        /// </summary>
        /// <param name="nTrials">Trial count N (the honest, logged count). N ≤ 1 ⇒ 0.</param>
        /// <param name="trialSharpeVariance">Variance v of the trial Sharpes. v ≤ 0 ⇒ 0.</param>
        public static double ExpectedMaxSharpe(int nTrials, double trialSharpeVariance)
        {
            if (nTrials <= 1) return 0.0;
            if (trialSharpeVariance <= 0.0) return 0.0;

            double gammaE = NormalDist.EulerMascheroni;
            double n = nTrials;
            // Both arguments are strictly within (0,1) for N ≥ 2.
            double phiInv1 = NormalDist.InverseCdf(1.0 - 1.0 / n);
            double phiInv2 = NormalDist.InverseCdf(1.0 - 1.0 / (n * Math.E));

            double bracket = (1.0 - gammaE) * phiInv1 + gammaE * phiInv2;
            return Math.Sqrt(trialSharpeVariance) * bracket;
        }

        /// <summary>
        /// Deflated Sharpe Ratio: <c>PSR(SR₀)</c> — the probability that the
        /// strategy's Sharpe beats the expected maximum under the null. A
        /// probability in [0,1].
        /// </summary>
        /// <param name="kurtosisNonExcess">γ₄ NON-excess (Gaussian = 3).</param>
        /// <param name="sr0">The deflated benchmark SR₀ (from <see cref="ExpectedMaxSharpe"/>).</param>
        /// <remarks>
        /// Named <c>Deflated</c> rather than <c>DeflatedSharpe</c> because C# forbids
        /// a member sharing the name of its enclosing type
        /// (<see cref="DeflatedSharpe"/>). Semantics are exactly the design's
        /// <c>DeflatedSharpe(...)</c>.
        /// </remarks>
        public static double Deflated(
            double srHat, double skew, double kurtosisNonExcess, int t, double sr0)
            => ProbabilisticSharpe(srHat, skew, kurtosisNonExcess, t, sr0);

        /// <summary>
        /// End-to-end DSR from the canonical return series: derives SR̂, γ₃,
        /// NON-excess γ₄ and T from one series via <see cref="SharpeStats.Moments"/>,
        /// computes SR₀ from <paramref name="nTrials"/> and
        /// <paramref name="trialSharpeVariance"/>, and returns PSR(SR₀).
        /// </summary>
        /// <remarks>
        /// Named <c>Deflated</c> rather than <c>DeflatedSharpe</c> for the same
        /// C# enclosing-type-name restriction.
        /// </remarks>
        public static double Deflated(
            IReadOnlyList<double> canonicalReturns, int nTrials, double trialSharpeVariance)
        {
            if (canonicalReturns is null) throw new ArgumentNullException(nameof(canonicalReturns));
            var m = SharpeStats.Moments(canonicalReturns);
            double sr0 = ExpectedMaxSharpe(nTrials, trialSharpeVariance);
            return ProbabilisticSharpe(m.Sr, m.Skew, m.Kurtosis, m.T, sr0);
        }

        /// <summary>
        /// The PSR variance radicand <c>1 − γ₃·SR̂ + ((γ₄−1)/4)·SR̂²</c>, exposed
        /// internally so the kurtosis convention can be pinned by a test (at γ₄ = 3
        /// the <c>(γ₄−1)/4</c> term equals exactly 0.5 — the silent-flip guard).
        /// </summary>
        internal static double PsrRadicand(double srHat, double skew, double kurtosisNonExcess)
            => 1.0 - skew * srHat + ((kurtosisNonExcess - 1.0) / 4.0) * srHat * srHat;

        private static double Clamp01(double p)
        {
            if (p < 0.0) return 0.0;
            if (p > 1.0) return 1.0;
            return p;
        }
    }
}
