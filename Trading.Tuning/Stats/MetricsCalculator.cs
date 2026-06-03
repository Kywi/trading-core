using System;
using System.Collections.Generic;
using System.Linq;

namespace GripTrader.Tuner.Stats
{
    /// <summary>Compact summary written to summary.json.</summary>
    public sealed class RunSummary
    {
        public string RunId { get; set; } = "";
        public string Symbol { get; set; } = "";
        public decimal InitialBankroll { get; set; }

        public string? PeriodStart { get; set; }   // ISO of first equity sample
        public string? PeriodEnd { get; set; }     // ISO of last equity sample
        public double DurationDays { get; set; }
        public double WallClockSeconds { get; set; }

        // P&L
        public decimal RealizedProfit { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal ColdStorage { get; set; }
        public decimal CleanedLosses { get; set; }
        public decimal NetPnL { get; set; }
        public decimal FinalEquity { get; set; }
        public double TotalReturnPct { get; set; }
        public double Cagr { get; set; }

        // Risk
        public double MaxDrawdownPct { get; set; }
        public double MaxDrawdownDurationDays { get; set; }
        public double Sharpe { get; set; }
        public double Sortino { get; set; }
        public double Calmar { get; set; }

        // Trades
        public int TradeCount { get; set; }
        public int WinCount { get; set; }
        public int LossCount { get; set; }
        public double WinRatePct { get; set; }
        public decimal AvgWin { get; set; }
        public decimal AvgLoss { get; set; }
        public double ProfitFactor { get; set; }
        public decimal AvgProfitPerTrade { get; set; }
        public double AvgHoldHours { get; set; }

        // Bag cleaning
        public int FullCleanings { get; set; }
        public int PartialCleanings { get; set; }
        public decimal CleaningCostTotal { get; set; }

        // Fees / executor
        public decimal MakerFees { get; set; }
        public decimal TakerFees { get; set; }
        public int OrdersFilled { get; set; }
        public int OrdersCanceled { get; set; }

        // Sample counts
        public int EquitySamples { get; set; }
        public int OpenPositionsAtEnd { get; set; }
    }

    public sealed class MonthlyRow
    {
        public string Month { get; set; } = ""; // "YYYY-MM"
        public decimal StartEquity { get; set; }
        public decimal EndEquity { get; set; }
        public double ReturnPct { get; set; }
        public double MaxDrawdownPct { get; set; }
        public int Trades { get; set; }
        public decimal RealizedProfit { get; set; }
    }

    public static class MetricsCalculator
    {
        public static RunSummary Compute(
            IReadOnlyList<EquityPoint> equity, IReadOnlyList<TradeRecord> trades, IReadOnlyList<CleaningRecord> cleanings,
            decimal initialBankroll, string runId, string symbol,
            decimal makerFees, decimal takerFees, int ordersFilled, int ordersCanceled,
            double wallClockSeconds, int openPositionsAtEnd)
        {
            var summary = new RunSummary
            {
                RunId = runId,
                Symbol = symbol,
                InitialBankroll = initialBankroll,
                MakerFees = makerFees,
                TakerFees = takerFees,
                OrdersFilled = ordersFilled,
                OrdersCanceled = ordersCanceled,
                WallClockSeconds = wallClockSeconds,
                OpenPositionsAtEnd = openPositionsAtEnd,
                EquitySamples = equity.Count
            };

            // ---- Period bounds ----
            if (equity.Count > 0)
            {
                var first = equity[0];
                var last = equity[^1];
                summary.PeriodStart = TimeUtil.FormatIso(first.TimestampMs);
                summary.PeriodEnd = TimeUtil.FormatIso(last.TimestampMs);
                summary.DurationDays = (last.TimestampMs - first.TimestampMs) / 86_400_000.0;

                summary.RealizedProfit = last.RealizedProfit;
                summary.UnrealizedPnL = last.UnrealizedPnL;
                summary.ColdStorage = last.ColdStorage;
                summary.CleanedLosses = last.CleanedLosses;
                summary.NetPnL = last.NetPnL;
                summary.FinalEquity = initialBankroll + last.NetPnL;

                if (initialBankroll > 0)
                {
                    summary.TotalReturnPct = (double)(last.NetPnL / initialBankroll) * 100.0;
                    if (summary.DurationDays > 0)
                    {
                        var growth = (double)summary.FinalEquity / (double)initialBankroll;
                        if (growth > 0)
                        {
                            var cagr = Math.Pow(growth, 365.25 / summary.DurationDays) - 1.0;
                            // Annualizing a very short window (e.g. a sub-day or
                            // single-day run with sizeable growth) blows the
                            // exponent up and overflows to +Infinity, which would
                            // then poison Calmar and every CSV/JSON consumer.
                            // Keep CAGR finite; a non-finite annualization is
                            // meaningless, so fall back to 0.
                            summary.Cagr = double.IsFinite(cagr) ? cagr : 0.0;
                        }
                    }
                }
            }

            // ---- Drawdown ----
            (summary.MaxDrawdownPct, summary.MaxDrawdownDurationDays) = ComputeMaxDrawdown(equity, initialBankroll);

            // ---- Daily returns + Sharpe / Sortino / Calmar ----
            var dailyReturns = ComputeDailyReturns(equity, initialBankroll);
            (summary.Sharpe, summary.Sortino) = ComputeSharpeSortino(dailyReturns);
            if (summary.MaxDrawdownPct > 0)
                summary.Calmar = summary.Cagr * 100.0 / summary.MaxDrawdownPct;

            // ---- Trades ----
            ComputeTradeStats(trades, summary);

            // ---- Cleanings ----
            summary.FullCleanings = cleanings.Count(c => c.Source == "Full");
            summary.PartialCleanings = cleanings.Count(c => c.Source == "Partial");
            summary.CleaningCostTotal = cleanings.Sum(c => c.CleaningCost);

            return summary;
        }

        public static List<MonthlyRow> ComputeMonthly(
            IReadOnlyList<EquityPoint> equity, IReadOnlyList<TradeRecord> trades, decimal initialBankroll)
        {
            var rows = new List<MonthlyRow>();
            if (equity.Count == 0) return rows;

            // Group equity points by YYYY-MM. The "start equity" of a month is
            // the last point of the previous month (or initial bankroll for
            // the first month).
            var byMonth = equity
                .GroupBy(e => TimeUtil.FromUnixMs(e.TimestampMs).ToString("yyyy-MM"))
                .OrderBy(g => g.Key);

            decimal prevEquity = initialBankroll;
            foreach (var grp in byMonth)
            {
                var ordered = grp.OrderBy(p => p.TimestampMs).ToList();
                var startEq = prevEquity;
                var endEq = initialBankroll + ordered[^1].NetPnL;

                // Drawdown within month
                decimal hwm = startEq;
                double maxDD = 0;
                foreach (var p in ordered)
                {
                    var eq = initialBankroll + p.NetPnL;
                    if (eq > hwm) hwm = eq;
                    if (hwm > 0)
                    {
                        var dd = (double)((hwm - eq) / hwm) * 100.0;
                        if (dd > maxDD) maxDD = dd;
                    }
                }

                var monthStart = TimeUtil.FromUnixMs(ordered[0].TimestampMs);
                var monthEnd = monthStart.AddMonths(1);
                var tradesInMonth = trades.Count(t =>
                    t.IsClosed
                    && t.CloseTimestampMs!.Value >= ordered[0].TimestampMs
                    && TimeUtil.FromUnixMs(t.CloseTimestampMs!.Value) < monthEnd);

                var realizedDelta = ordered[^1].RealizedProfit
                    - (rows.Count == 0 ? 0m : equity.Last(p =>
                        TimeUtil.FromUnixMs(p.TimestampMs).ToString("yyyy-MM") == rows[^1].Month).RealizedProfit);

                rows.Add(new MonthlyRow
                {
                    Month = grp.Key,
                    StartEquity = startEq,
                    EndEquity = endEq,
                    ReturnPct = startEq > 0 ? (double)((endEq - startEq) / startEq) * 100.0 : 0.0,
                    MaxDrawdownPct = maxDD,
                    Trades = tradesInMonth,
                    RealizedProfit = realizedDelta
                });

                prevEquity = endEq;
            }

            return rows;
        }

        // -----------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------

        internal static (double maxPct, double durationDays) ComputeMaxDrawdown(IReadOnlyList<EquityPoint> equity, decimal initialBankroll)
        {
            if (equity.Count == 0 || initialBankroll <= 0) return (0, 0);

            decimal hwm = initialBankroll;
            long hwmTimeMs = equity[0].TimestampMs;

            double maxDDPct = 0;
            long maxDDDurationMs = 0;

            foreach (var p in equity)
            {
                var eq = initialBankroll + p.NetPnL;
                if (eq >= hwm)
                {
                    hwm = eq;
                    hwmTimeMs = p.TimestampMs;
                    continue;
                }
                if (hwm > 0)
                {
                    var ddPct = (double)((hwm - eq) / hwm) * 100.0;
                    if (ddPct > maxDDPct) maxDDPct = ddPct;
                    var dur = p.TimestampMs - hwmTimeMs;
                    if (dur > maxDDDurationMs) maxDDDurationMs = dur;
                }
            }

            return (maxDDPct, maxDDDurationMs / 86_400_000.0);
        }

        internal static List<double> ComputeDailyReturns(IReadOnlyList<EquityPoint> equity, decimal initialBankroll)
        {
            var returns = new List<double>();
            if (equity.Count < 2 || initialBankroll <= 0) return returns;

            // Resample to one equity value per UTC day (last sample of the day wins).
            var byDay = new SortedDictionary<long, decimal>();
            foreach (var p in equity)
            {
                var dayKey = p.TimestampMs / 86_400_000L;
                byDay[dayKey] = initialBankroll + p.NetPnL;
            }

            decimal? prev = null;
            foreach (var kv in byDay)
            {
                if (prev.HasValue && prev.Value > 0)
                    returns.Add((double)((kv.Value - prev.Value) / prev.Value));
                prev = kv.Value;
            }
            return returns;
        }

        internal static (double sharpe, double sortino) ComputeSharpeSortino(IReadOnlyList<double> dailyReturns)
        {
            if (dailyReturns.Count < 2) return (0, 0);

            double mean = dailyReturns.Average();
            double sumSq = dailyReturns.Sum(r => (r - mean) * (r - mean));
            double sd = Math.Sqrt(sumSq / (dailyReturns.Count - 1));

            double sumDownSq = dailyReturns
                .Where(r => r < 0)
                .Sum(r => r * r);
            double dsd = Math.Sqrt(sumDownSq / Math.Max(1, dailyReturns.Count - 1));

            const double sqrtAnnualization = 19.10497317; // sqrt(365)
            var sharpe = sd > 0 ? mean / sd * sqrtAnnualization : 0;
            var sortino = dsd > 0 ? mean / dsd * sqrtAnnualization : 0;
            return (sharpe, sortino);
        }

        internal static void ComputeTradeStats(IReadOnlyList<TradeRecord> trades, RunSummary s)
        {
            var closed = trades.Where(t => t.IsClosed && t.RealizedProfit.HasValue).ToList();
            s.TradeCount = closed.Count;
            if (closed.Count == 0) return;

            var wins = closed.Where(t => t.RealizedProfit!.Value > 0m).ToList();
            var losses = closed.Where(t => t.RealizedProfit!.Value <= 0m).ToList();
            s.WinCount = wins.Count;
            s.LossCount = losses.Count;
            s.WinRatePct = closed.Count > 0 ? wins.Count * 100.0 / closed.Count : 0;
            s.AvgWin = wins.Count > 0 ? wins.Average(t => t.RealizedProfit!.Value) : 0m;
            s.AvgLoss = losses.Count > 0 ? losses.Average(t => t.RealizedProfit!.Value) : 0m;

            decimal grossWin = wins.Sum(t => t.RealizedProfit!.Value);
            decimal grossLoss = Math.Abs(losses.Sum(t => t.RealizedProfit!.Value));
            s.ProfitFactor = grossLoss > 0 ? (double)(grossWin / grossLoss) : (grossWin > 0 ? double.PositiveInfinity : 0);

            s.AvgProfitPerTrade = closed.Average(t => t.RealizedProfit!.Value);
            s.AvgHoldHours = closed
                .Where(t => t.HoldMs.HasValue)
                .Select(t => t.HoldMs!.Value / 3_600_000.0)
                .DefaultIfEmpty(0.0)
                .Average();
        }
    }
}
