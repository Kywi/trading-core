using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GripTrader.Tuner.Sweep
{
    /// <summary>
    /// Writes the aggregated sweep CSV. One row per sweep run; columns are:
    /// run_id, every swept parameter (in spec order), then a fixed block of
    /// summary metrics, then wall clock and error message.
    /// All numerics use <see cref="CultureInfo.InvariantCulture"/>.
    /// </summary>
    public static class SweepAggregator
    {
        public static void WriteAggregated(string path, IReadOnlyList<string> paramOrder, IReadOnlyList<SweepRunRecord> records)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            // Header
            sb.Append("run_id,symbol");
            foreach (var p in paramOrder)
                sb.Append(',').Append(p);
            sb.Append(",total_return_pct,cagr,max_dd_pct,max_dd_days,sharpe,sortino,calmar")
              .Append(",trades,win_rate_pct,profit_factor,avg_hold_h,full_cleanings,partial_cleanings,cleaning_cost")
              .Append(",fees_taker,fees_maker,wall_clock_s,error")
              .AppendLine();

            foreach (var rec in records)
            {
                sb.Append(rec.RunId).Append(',').Append(EscapeCsv(rec.Symbol ?? ""));
                foreach (var p in paramOrder)
                {
                    sb.Append(',');
                    if (rec.Parameters.TryGetValue(p, out var v))
                        sb.Append(FormatValue(v, ci));
                }

                var s = rec.Summary;
                sb.Append(',').Append(F(s?.TotalReturnPct, ci))
                  .Append(',').Append(F(s?.Cagr, ci))
                  .Append(',').Append(F(s?.MaxDrawdownPct, ci))
                  .Append(',').Append(F(s?.MaxDrawdownDurationDays, ci))
                  .Append(',').Append(F(s?.Sharpe, ci))
                  .Append(',').Append(F(s?.Sortino, ci))
                  .Append(',').Append(F(s?.Calmar, ci))
                  .Append(',').Append(s?.TradeCount.ToString(ci) ?? "")
                  .Append(',').Append(F(s?.WinRatePct, ci))
                  .Append(',').Append(F(s?.ProfitFactor, ci))
                  .Append(',').Append(F(s?.AvgHoldHours, ci))
                  .Append(',').Append(s?.FullCleanings.ToString(ci) ?? "")
                  .Append(',').Append(s?.PartialCleanings.ToString(ci) ?? "")
                  .Append(',').Append(D(s?.CleaningCostTotal, ci))
                  .Append(',').Append(D(s?.TakerFees, ci))
                  .Append(',').Append(D(s?.MakerFees, ci))
                  .Append(',').Append(rec.WallClockSeconds.ToString("F2", ci))
                  .Append(',').Append(EscapeCsv(rec.Error ?? ""))
                  .AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>Pretty-prints the top-N runs by Sharpe to the console — quick eyeball after a sweep.</summary>
        public static void PrintTopN(IReadOnlyList<SweepRunRecord> records, IReadOnlyList<string> paramOrder, int topN)
        {
            var ranked = records
                .Where(r => r.Summary != null && r.Error == null)
                .OrderByDescending(r => r.Summary!.Sharpe)
                .Take(topN)
                .ToList();

            if (ranked.Count == 0)
            {
                System.Console.WriteLine("[Sweep] No successful runs to rank.");
                return;
            }

            var ci = CultureInfo.InvariantCulture;
            System.Console.WriteLine($"[Sweep] === Top {ranked.Count} by Sharpe ===");
            System.Console.WriteLine($"  {"run_id",-12} {"symbol",-10} {"sharpe",8} {"return%",8} {"maxdd%",8} {"calmar",7} {"trades",7}  params");
            foreach (var r in ranked)
            {
                var s = r.Summary!;
                var pStr = string.Join(" ", paramOrder.Select(p => r.Parameters.TryGetValue(p, out var v) ? $"{p}={FormatValue(v, ci)}" : ""));
                System.Console.WriteLine(
                    $"  {r.RunId,-12} {r.Symbol,-10} {s.Sharpe,8:F2} {s.TotalReturnPct,8:F2} {s.MaxDrawdownPct,8:F2} {s.Calmar,7:F2} {s.TradeCount,7}  {pStr}");
            }
        }

        private static string F(double? v, CultureInfo ci) => v.HasValue ? FormatNumeric(v.Value, "F6", ci) : "";
        private static string D(decimal? v, CultureInfo ci) => v.HasValue ? v.Value.ToString(ci) : "";

        /// <summary>
        /// Formats a <see cref="double"/> metric for a numeric CSV cell using the
        /// given format and invariant culture. Non-finite values (±Infinity, NaN —
        /// e.g. ProfitFactor for an all-wins run, or a 0/0 ratio) map to the
        /// documented sentinel: an EMPTY string. This keeps the numeric column
        /// parseable/blank instead of emitting the literal text "Infinity"/"NaN"
        /// that corrupts the column for downstream parsers. Shared by
        /// <see cref="SweepAggregator"/> and <c>ReportWriter</c>.
        /// </summary>
        public static string FormatNumeric(double v, string format, CultureInfo ci) =>
            double.IsFinite(v) ? v.ToString(format, ci) : "";

        /// <summary>
        /// Non-finite-safe formatting with general (round-trippable) invariant
        /// formatting, e.g. <c>1.5 -&gt; "1.5"</c>, <c>0 -&gt; "0"</c>. Non-finite
        /// values map to the empty-string sentinel (see the formatted overload).
        /// </summary>
        public static string FormatNumeric(double v) =>
            double.IsFinite(v) ? v.ToString(CultureInfo.InvariantCulture) : "";

        /// <summary>
        /// Render a sweep parameter value for CSV / console output. Decimals
        /// and doubles use invariant culture (so "0.005" not "0,005"), bools
        /// use standard True/False, strings are CSV-escaped if needed.
        /// </summary>
        private static string FormatValue(object? v, CultureInfo ci)
        {
            return v switch
            {
                null => "",
                decimal d => d.ToString(ci),
                double d => d.ToString(ci),
                int i => i.ToString(ci),
                long l => l.ToString(ci),
                bool b => b ? "True" : "False",
                string s => EscapeCsv(s),
                _ => EscapeCsv(v.ToString() ?? ""),
            };
        }

        private static string EscapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
