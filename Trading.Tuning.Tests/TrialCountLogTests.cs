using System;
using System.Collections.Generic;
using System.IO;
using GripTrader.Tuner.Stats;
using Xunit;

namespace GripTrader.Tuner.Tests
{
    public class TrialCountLogTests
    {
        private static TrialRecord Rec(long ts, string kind, string outcome, params (string k, string v)[] tags)
        {
            var bag = new Dictionary<string, string>();
            foreach (var (k, v) in tags) bag[k] = v;
            return new TrialRecord { TimestampMs = ts, Kind = kind, Outcome = outcome, Tags = bag };
        }

        [Fact]
        public void Count_IncrementsPerAppend_AndMatchesLines()
        {
            var sw = new StringWriter();
            var log = new TrialCountLog(sw);

            log.Append(Rec(1000, "CointegrationScreen", "Rejected", ("pair", "BTC-ETH")));
            log.Append(Rec(2000, "CointegrationScreen", "Evaluated", ("pair", "BTC-SOL")));
            log.Append(Rec(3000, "ParamConfig", "Traded", ("threshold", "2.0"), ("lookback", "100")));

            Assert.Equal(3, log.Count);

            // Header + 3 records = 4 non-empty lines.
            var lines = sw.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            Assert.Equal(4, lines.Length);
            Assert.StartsWith("TimestampMs", lines[0]);
        }

        [Fact]
        public void RejectedAndAbandoned_AreCounted()
        {
            var sw = new StringWriter();
            var log = new TrialCountLog(sw);
            log.Append(Rec(1, "CointegrationScreen", "Rejected"));
            log.Append(Rec(2, "ParamConfig", "Abandoned"));
            log.Append(Rec(3, "ParamConfig", "Evaluated"));
            Assert.Equal(3, log.Count); // all three are first-class trials
        }

        [Fact]
        public void Freeze_ReturnsCount_AndBlocksFurtherAppend()
        {
            var sw = new StringWriter();
            var log = new TrialCountLog(sw);
            log.Append(Rec(1, "ParamConfig", "Evaluated"));
            log.Append(Rec(2, "ParamConfig", "Evaluated"));

            Assert.False(log.IsFrozen);
            int n = log.Freeze();
            Assert.Equal(2, n);
            Assert.True(log.IsFrozen);

            Assert.Throws<InvalidOperationException>(() => log.Append(Rec(3, "ParamConfig", "Evaluated")));
            Assert.Equal(2, log.Count); // unchanged after blocked append
        }

        [Fact]
        public void Freeze_Idempotent()
        {
            var sw = new StringWriter();
            var log = new TrialCountLog(sw);
            log.Append(Rec(1, "K", "Evaluated"));
            Assert.Equal(1, log.Freeze());
            Assert.Equal(1, log.Freeze());
        }

        [Fact]
        public void AppendOrder_Preserved()
        {
            var sw = new StringWriter();
            var log = new TrialCountLog(sw);
            log.Append(Rec(100, "A", "Evaluated", ("pair", "X")));
            log.Append(Rec(200, "B", "Rejected", ("pair", "Y")));

            var lines = sw.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            Assert.Contains("100\tA\tEvaluated\tpair=X", lines[1]);
            Assert.Contains("200\tB\tRejected\tpair=Y", lines[2]);
        }

        [Fact]
        public void TagOrder_DeterministicByKey()
        {
            // Tags inserted out of order must serialize ordinal-sorted by key.
            var sw1 = new StringWriter();
            var log1 = new TrialCountLog(sw1);
            log1.Append(Rec(1, "K", "Evaluated", ("zeta", "1"), ("alpha", "2"), ("mid", "3")));

            var sw2 = new StringWriter();
            var log2 = new TrialCountLog(sw2);
            log2.Append(Rec(1, "K", "Evaluated", ("alpha", "2"), ("mid", "3"), ("zeta", "1")));

            // Same record content (different insertion order) ⇒ identical text.
            Assert.Equal(sw1.ToString(), sw2.ToString());
            Assert.Contains("alpha=2;mid=3;zeta=1", sw1.ToString());
        }

        [Fact]
        public void Deterministic_SinkText_NoWallClock()
        {
            // Two logs fed identical records with caller-supplied timestamps produce
            // byte-identical text — proving no DateTime.UtcNow leaks in.
            string Build()
            {
                var sw = new StringWriter();
                var log = new TrialCountLog(sw);
                log.Append(Rec(1717000000000, "CointegrationScreen", "Rejected", ("pair", "BTC-ETH")));
                log.Append(Rec(1717000003600, "ParamConfig", "Traded", ("threshold", "2.5")));
                log.Freeze();
                return sw.ToString();
            }
            Assert.Equal(Build(), Build());
        }

        [Fact]
        public void Sanitize_StripsDelimitersAndNewlines()
        {
            var sw = new StringWriter();
            var log = new TrialCountLog(sw);
            // Field/tag delimiters and newlines inside data must not corrupt the layout.
            log.Append(Rec(1, "Kind\twith\ttabs", "Out\ncome", ("k\ne;y", "va=lue")));
            var lines = sw.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

            // Exactly header + one record line (no embedded newline split the record).
            Assert.Equal(2, lines.Length);
            // The record line has exactly 4 columns ⇒ exactly 3 tab separators
            // (data tabs were sanitized to spaces).
            Assert.Equal(3, lines[1].Split('\t').Length - 1);
        }

        [Fact]
        public void NullSink_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new TrialCountLog(null!));
        }

        [Fact]
        public void NullRecord_Throws()
        {
            var log = new TrialCountLog(new StringWriter());
            Assert.Throws<ArgumentNullException>(() => log.Append(null!));
        }
    }
}
