using System.Collections.Generic;
using System.IO;
using GripTrader.Core.Abstractions;
using GripTrader.Core.Backtest;
using Xunit;

namespace GripTrader.Core.Backtest.Tests
{
    /// <summary>
    /// Tests for the funding-rate archive reader: header skipped, IntervalHours read
    /// straight from column 1 (never inferred from deltas), rate decimal-exact, and
    /// calc_time epoch-normalized identically for ms and microsecond timestamps.
    /// </summary>
    public class FundingRateReaderTests
    {
        private const string Sym = "BTCUSDT";

        private static List<FundingEvent> Read(string csv)
            => FundingRateReader.ReadFundingRates(new StringReader(csv), Sym);

        // ---- 9. Real-format fixture: header skipped, interval from col 1, rate exact, epoch normalized ----
        [Fact]
        public void Parses_RealFormat_HeaderSkipped_IntervalFromColumn_RateExact()
        {
            // Real archive header is calc_time,funding_interval_hours,last_funding_rate.
            // Note the SECOND row carries a 4h interval but the timestamp DELTA from the
            // first is 8h — if the reader inferred the interval from deltas it would get
            // 8, not the column's 4. So this asserts the column value wins.
            var csv =
                "calc_time,funding_interval_hours,last_funding_rate\n" +
                "1704067200000,8,0.00010000\n" +
                "1704096000000,4,-0.00005000\n" +   // +8h later, but interval column says 4
                "1704110400000,4,0.00012345\n";

            var events = Read(csv);

            Assert.Equal(3, events.Count);

            Assert.Equal(1704067200000L, events[0].TimestampMs);
            Assert.Equal(8, events[0].IntervalHours);
            Assert.Equal(0.00010000m, events[0].Rate);
            Assert.Equal(Sym, events[0].Symbol);
            // Reader does not know the mark — fills it at apply time in the feeder.
            Assert.Equal(0m, events[0].MarkPrice);

            // Interval read from the column, NOT inferred from the 8h delta.
            Assert.Equal(4, events[1].IntervalHours);
            Assert.Equal(-0.00005000m, events[1].Rate);

            Assert.Equal(4, events[2].IntervalHours);
            Assert.Equal(0.00012345m, events[2].Rate);
        }

        // ---- Epoch normalization: microsecond calc_time normalizes to ms identically ----
        [Fact]
        public void MicrosecondCalcTime_NormalizesToMilliseconds()
        {
            // 1704067200000 ms == 1704067200000000 microseconds (×1000).
            var csvMs =
                "calc_time,funding_interval_hours,last_funding_rate\n" +
                "1704067200000,8,0.00010000\n";
            var csvMicros =
                "calc_time,funding_interval_hours,last_funding_rate\n" +
                "1704067200000000,8,0.00010000\n";

            var ms = Read(csvMs);
            var micros = Read(csvMicros);

            Assert.Single(ms);
            Assert.Single(micros);
            // Identical normalized timestamp.
            Assert.Equal(ms[0].TimestampMs, micros[0].TimestampMs);
            Assert.Equal(1704067200000L, micros[0].TimestampMs);
            Assert.Equal(ms[0].Rate, micros[0].Rate);
            Assert.Equal(ms[0].IntervalHours, micros[0].IntervalHours);
        }

        // ---- Empty / header-only file yields no events (no crash) ----
        [Fact]
        public void HeaderOnly_ReturnsNoEvents()
        {
            var events = Read("calc_time,funding_interval_hours,last_funding_rate\n");
            Assert.Empty(events);
        }

        // ---- Decimal exactness: a tiny/odd rate round-trips exactly ----
        [Fact]
        public void RateIsDecimalExact_NoBinaryFloatDrift()
        {
            var csv =
                "calc_time,funding_interval_hours,last_funding_rate\n" +
                "1704067200000,8,0.00007531\n";
            var events = Read(csv);
            Assert.Single(events);
            // Exact decimal — a double would not represent 0.00007531 exactly.
            Assert.Equal(0.00007531m, events[0].Rate);
        }
    }
}
