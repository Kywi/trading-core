using System;
using System.Collections.Generic;

namespace GripTrader.Tuner.Stats
{
    /// <summary>
    /// Canonical per-observation moments of a bare return series — the single
    /// source of <c>SR̂</c>, skew γ₃, NON-EXCESS kurtosis γ₄, and observation
    /// count <c>T</c> that every downstream validation primitive (CPCV path
    /// Sharpe, DSR/PSR, PBO ranking) routes through, so the conventions cannot
    /// drift between callers.
    ///
    /// <para>
    /// Statistics carve-out: every quantity here is <see cref="double"/> (these
    /// are statistics, not money — mirrors <see cref="MetricsCalculator"/>). The
    /// <c>decimal price → double return</c> conversion happens upstream in the bot.
    /// </para>
    ///
    /// <para>
    /// Determinism: every Σ uses a fixed ascending-index reduction order so the
    /// output is bit-identical across runs/JIT/SIMD. The functions are pure.
    /// </para>
    ///
    /// <para>
    /// Sharpe convention: this is the NON-annualized, per-observation Sharpe
    /// <c>SR̂ = mean / sampleStdDev</c> required by PSR/DSR — NOT
    /// <see cref="MetricsCalculator.ComputeSharpeSortino"/>'s √365-annualized
    /// display Sharpe (which is for the human-readable RunSummary only).
    /// </para>
    /// </summary>
    public static class SharpeStats
    {
        /// <summary>
        /// Canonical moments of <paramref name="returns"/>.
        /// <list type="bullet">
        /// <item><c>Sr</c> = per-observation (NON-annualized) Sharpe = mean / sample stddev.</item>
        /// <item><c>Skew</c> = γ₃ (third standardized moment, population σ).</item>
        /// <item><c>Kurtosis</c> = γ₄ NON-EXCESS (Gaussian limit = 3.0; do NOT subtract 3).</item>
        /// <item><c>T</c> = observation count.</item>
        /// </list>
        /// Edge cases never produce NaN/Inf: <c>T &lt; 2</c> or empty ⇒
        /// <c>Sr=0, Skew=0, Kurtosis=3</c>; zero variance ⇒ <c>Sr=0, γ₃=0, γ₄=3</c>.
        /// </summary>
        public static SeriesMoments Moments(IReadOnlyList<double> returns)
        {
            if (returns is null) throw new ArgumentNullException(nameof(returns));

            int t = returns.Count;
            if (t < 2) return new SeriesMoments(0.0, 0.0, 3.0, t);

            // mean — fixed ascending-index reduction.
            double sum = 0.0;
            for (int i = 0; i < t; i++) sum += returns[i];
            double mean = sum / t;

            // Σ(rᵢ−m)², Σ(rᵢ−m)³, Σ(rᵢ−m)⁴ — fixed ascending-index reduction.
            double s2 = 0.0, s3 = 0.0, s4 = 0.0;
            for (int i = 0; i < t; i++)
            {
                double d = returns[i] - mean;
                double d2 = d * d;
                s2 += d2;
                s3 += d2 * d;
                s4 += d2 * d2;
            }

            // Sample stddev (T−1) — matches MetricsCalculator.ComputeSharpeSortino.
            double sampleVar = s2 / (t - 1);
            double sampleSd = Math.Sqrt(sampleVar);
            double sr = sampleSd > 0.0 ? mean / sampleSd : 0.0;

            // Population σ (divide by T) so the Gaussian limit of γ₄ is exactly 3.
            double popVar = s2 / t;
            double skew, kurt;
            if (popVar <= 0.0)
            {
                // Degenerate (all observations identical): no shape; γ₄ defaults to Gaussian 3.
                skew = 0.0;
                kurt = 3.0;
            }
            else
            {
                double popSd = Math.Sqrt(popVar);
                double sd3 = popSd * popSd * popSd;       // σ³
                double sd4 = popVar * popVar;             // σ⁴
                skew = (s3 / t) / sd3;
                kurt = (s4 / t) / sd4;                    // NON-EXCESS
            }

            return new SeriesMoments(sr, skew, kurt, t);
        }

        /// <summary>
        /// Non-annualized, per-observation Sharpe of <paramref name="returns"/>
        /// (<c>mean / sample stddev</c>). Returns 0 for fewer than 2 observations
        /// or zero variance. This is the Sharpe DSR/PSR/CPCV consume — never the
        /// √365-annualized display Sharpe.
        /// </summary>
        public static double Sharpe(IReadOnlyList<double> returns) => Moments(returns).Sr;
    }

    /// <summary>
    /// BCL-only standard-normal CDF and its inverse. MathNet is not in the
    /// offline NuGet cache, so these are hand-rolled rational approximations.
    /// Internal — used by <see cref="DeflatedSharpe"/>.
    /// </summary>
    internal static class NormalDist
    {
        // Euler–Mascheroni constant (used by ExpectedMaxSharpe in DeflatedSharpe).
        internal const double EulerMascheroni = 0.5772156649015329;

        /// <summary>
        /// Standard-normal CDF Φ(z) = ½·erfc(−z/√2) = ½·(1 + erf(z/√2)).
        /// Inherits the <see cref="Erf"/> approximation error (max abs ≈ 1.5e-7).
        /// </summary>
        internal static double Cdf(double z)
        {
            // 1/√2
            return 0.5 * (1.0 + Erf(z * 0.7071067811865476));
        }

        /// <summary>
        /// Error function via Abramowitz &amp; Stegun 7.1.26.
        /// Maximum absolute error ≈ 1.5e-7 over all x (documented tolerance).
        /// Odd-symmetric: erf(−x) = −erf(x).
        /// </summary>
        internal static double Erf(double x)
        {
            // Constants of A&S 7.1.26.
            const double a1 = 0.254829592;
            const double a2 = -0.284496736;
            const double a3 = 1.421413741;
            const double a4 = -1.453152027;
            const double a5 = 1.061405429;
            const double p = 0.3275911;

            int sign = x < 0.0 ? -1 : 1;
            double ax = Math.Abs(x);

            double t = 1.0 / (1.0 + p * ax);
            // Horner evaluation, fixed order.
            double poly = ((((a5 * t + a4) * t + a3) * t + a2) * t + a1) * t;
            double y = 1.0 - poly * Math.Exp(-ax * ax);
            return sign * y;
        }

        /// <summary>
        /// Inverse standard-normal CDF Φ⁻¹(p) via the Acklam (Beasley-Springer-Moro
        /// style) rational approximation. Defined for p ∈ (0,1); p ≤ 0 ⇒
        /// <see cref="double.NegativeInfinity"/> is avoided by clamping callers,
        /// but here p outside (0,1) throws. Maximum relative error ≈ 1.15e-9 over
        /// the central region (documented tolerance); no Halley refinement step is
        /// applied (the raw rational form is sufficient for SR₀).
        /// </summary>
        internal static double InverseCdf(double p)
        {
            if (p <= 0.0 || p >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(p), p, "InverseCdf requires p in the open interval (0,1).");

            // Coefficients for Acklam's rational approximation.
            const double a1 = -3.969683028665376e+01;
            const double a2 = 2.209460984245205e+02;
            const double a3 = -2.759285104469687e+02;
            const double a4 = 1.383577518672690e+02;
            const double a5 = -3.066479806614716e+01;
            const double a6 = 2.506628277459239e+00;

            const double b1 = -5.447609879822406e+01;
            const double b2 = 1.615858368580409e+02;
            const double b3 = -1.556989798598866e+02;
            const double b4 = 6.680131188771972e+01;
            const double b5 = -1.328068155288572e+01;

            const double c1 = -7.784894002430293e-03;
            const double c2 = -3.223964580411365e-01;
            const double c3 = -2.400758277161838e+00;
            const double c4 = -2.549732539343734e+00;
            const double c5 = 4.374664141464968e+00;
            const double c6 = 2.938163982698783e+00;

            const double d1 = 7.784695709041462e-03;
            const double d2 = 3.224671290700398e-01;
            const double d3 = 2.445134137142996e+00;
            const double d4 = 3.754408661907416e+00;

            // Break-points defining the central region.
            const double pLow = 0.02425;
            const double pHigh = 1.0 - pLow;

            double q, r;
            if (p < pLow)
            {
                // Lower tail.
                q = Math.Sqrt(-2.0 * Math.Log(p));
                return (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                       ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
            }
            if (p <= pHigh)
            {
                // Central region.
                q = p - 0.5;
                r = q * q;
                return (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q /
                       (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1.0);
            }
            // Upper tail.
            q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
            return -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                    ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
        }
    }
}
