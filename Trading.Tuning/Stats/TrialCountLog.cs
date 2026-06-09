using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GripTrader.Tuner.Stats
{
    /// <summary>
    /// Append-only honest trial-count logger (validation primitive 5).
    ///
    /// <para>
    /// The DSR can only deflate for trials it knows about, so EVERY configuration
    /// evaluated must be recorded here — including rejected/abandoned ones and the
    /// full cointegration screen (all <c>C(n,2)</c> candidate pairs × every
    /// walk-forward re-selection step). The final <see cref="Count"/> after
    /// <see cref="Freeze"/> is the <c>N</c> fed to <see cref="DeflatedSharpe"/>.
    /// </para>
    ///
    /// <para>
    /// Strategy-agnostic: core provides only the mechanism and the frozen-N
    /// contract; the bot supplies strategy-specific fields via the generic
    /// <see cref="TrialRecord.Tags"/> bag, and the caller supplies timestamps from
    /// the feed clock (never <c>DateTime.UtcNow</c>) so the log is deterministic
    /// and testable. Records are written durably (one delimited line per append,
    /// flushed) through an injected <see cref="TextWriter"/> — core stays
    /// I/O-policy-agnostic. Single-threaded only (no locking).
    /// </para>
    /// </summary>
    public sealed class TrialCountLog
    {
        private const char FieldDelimiter = '\t';
        private const char TagPairDelimiter = ';';
        private const char TagKvDelimiter = '=';

        private readonly TextWriter _sink;
        private int _count;
        private bool _frozen;

        /// <summary>
        /// Construct a logger that writes durably to <paramref name="sink"/>.
        /// Writes a header line on construction.
        /// </summary>
        public TrialCountLog(TextWriter sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _sink.WriteLine(string.Join(FieldDelimiter, "TimestampMs", "Kind", "Outcome", "Tags"));
            _sink.Flush();
        }

        /// <summary>Number of trials appended so far. After <see cref="Freeze"/> this is the final N.</summary>
        public int Count => _count;

        /// <summary>True once <see cref="Freeze"/> has been called; further <see cref="Append"/> throws.</summary>
        public bool IsFrozen => _frozen;

        /// <summary>
        /// Append one trial record: writes a single delimited line and flushes
        /// (durable). Append order is preserved exactly. Throws
        /// <see cref="InvalidOperationException"/> if the log has been frozen.
        /// </summary>
        public void Append(TrialRecord r)
        {
            if (r is null) throw new ArgumentNullException(nameof(r));
            if (_frozen)
                throw new InvalidOperationException("TrialCountLog is frozen; no further trials may be appended (N is locked before the holdout is unlocked).");

            _sink.WriteLine(FormatLine(r));
            _sink.Flush();
            _count++;
        }

        /// <summary>
        /// Freeze the log: returns the final trial count <c>N</c> and blocks any
        /// further <see cref="Append"/>. <c>N</c> must be frozen before the locked
        /// holdout is unlocked. Idempotent — calling twice returns the same count.
        /// </summary>
        public int Freeze()
        {
            _frozen = true;
            return _count;
        }

        private static string FormatLine(TrialRecord r)
        {
            var sb = new StringBuilder();
            sb.Append(r.TimestampMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(FieldDelimiter);
            sb.Append(Sanitize(r.Kind));
            sb.Append(FieldDelimiter);
            sb.Append(Sanitize(r.Outcome));
            sb.Append(FieldDelimiter);
            sb.Append(FormatTags(r.Tags));
            return sb.ToString();
        }

        private static string FormatTags(IReadOnlyDictionary<string, string>? tags)
        {
            if (tags is null || tags.Count == 0) return "";

            // Deterministic ordering: ordinal-sorted keys (no wall-clock, no hash
            // iteration order leaking into the durable text).
            var keys = new List<string>(tags.Keys);
            keys.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(TagPairDelimiter);
                sb.Append(Sanitize(keys[i]));
                sb.Append(TagKvDelimiter);
                sb.Append(Sanitize(tags[keys[i]]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Strip delimiter characters and line breaks so a single record never
        /// spans lines or corrupts the column layout (replaced with spaces).
        /// </summary>
        private static string Sanitize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                sb.Append(ch switch
                {
                    FieldDelimiter => ' ',
                    TagPairDelimiter => ' ',
                    TagKvDelimiter => ' ',
                    '\r' => ' ',
                    '\n' => ' ',
                    _ => ch
                });
            }
            return sb.ToString();
        }
    }
}
