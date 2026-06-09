using GripTrader.Core.Abstractions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GripTrader.Core.Backtest
{
    /// <summary>
    /// Static span-parsing reader for the Binance USDⓈ-M funding-rate archive
    /// (<c>fundingRate/{SYM}/{SYM}-fundingRate-YYYY-MM.csv</c>, monthly-only, no
    /// interval segment). The CSV carries a header row and the columns
    /// <c>calc_time,funding_interval_hours,last_funding_rate</c>.
    /// <para>
    /// There was no precedent funding feeder, so this loader is new. It is pure and
    /// directly testable: <see cref="ReadFundingRates"/> takes a <see cref="TextReader"/>
    /// (in-memory fixtures or a file stream). Each <c>calc_time</c> is epoch-normalized
    /// with the same <c>&gt;10^13 ? /1000</c> invariant as every feeder; the
    /// <c>funding_interval_hours</c> is read straight from column 1, <b>never inferred</b>
    /// from timestamp deltas. The funding rate stays <see cref="decimal"/> (decimal-only
    /// money math). The mark price is filled in by the merge feeder at apply time (this
    /// reader does not know mark), so the returned <see cref="FundingEvent.MarkPrice"/>
    /// is 0 here.
    /// </para>
    /// </summary>
    public static class FundingRateReader
    {
        // Same epoch-normalization constant/direction as HistoricalKlineFeeder
        // (ms vs microsecond archives): values above 10^13 ms are microseconds.
        private const long EpochMicrosThreshold = 10_000_000_000_000L;

        internal static long NormalizeEpochMs(long t) => t > EpochMicrosThreshold ? t / 1000L : t;

        /// <summary>
        /// Resolve a path to an ordered list of funding-rate CSV files for a symbol.
        /// Single file or directory both supported; for a directory, files matching
        /// <c>{SYMBOL}-fundingRate-*.csv</c> are sorted by the trailing <c>YYYY-MM</c>.
        /// The funding archive is monthly-only and has no interval segment.
        /// </summary>
        public static List<string> CollectSortedFundingFiles(string path, string symbol)
        {
            if (File.Exists(path))
                return new List<string> { path };

            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Funding path not found: {path}");

            var sym = symbol.ToUpperInvariant();
            var prefix = sym + "-fundingRate-";
            var files = new List<string>();
            foreach (var f in Directory.GetFiles(path, "*.csv", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    files.Add(f);
            }

            files.Sort((a, b) =>
                string.CompareOrdinal(SortKey(a, prefix.Length), SortKey(b, prefix.Length)));

            if (files.Count == 0)
                throw new FileNotFoundException(
                    $"No funding-rate CSV files found for {sym} in {path}. " +
                    $"Expected files like {sym}-fundingRate-2024-01.csv (monthly, no interval segment).");

            return files;

            static string SortKey(string filePath, int prefixLen)
            {
                var name = Path.GetFileNameWithoutExtension(filePath);
                return prefixLen <= name.Length ? name[prefixLen..] : name;
            }
        }

        /// <summary>
        /// Parse every funding row from <paramref name="reader"/> in file order into
        /// <see cref="FundingEvent"/>s (<see cref="FundingEvent.MarkPrice"/> left 0 —
        /// the merge feeder fills it from the aligned mark bar at apply time). The
        /// header row (and any blank/non-numeric leading line) is skipped via the same
        /// first-char-is-digit rule the other feeders use.
        /// </summary>
        public static List<FundingEvent> ReadFundingRates(TextReader reader, string symbol)
        {
            if (reader is null) throw new ArgumentNullException(nameof(reader));
            if (symbol is null) throw new ArgumentNullException(nameof(symbol));

            var events = new List<FundingEvent>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || !char.IsAsciiDigit(line[0]))
                    continue; // header / blank rows

                var span = line.AsSpan();

                long calcTime = 0;
                int intervalHours = 0;
                decimal rate = 0m;
                int col = 0, start = 0;

                for (int i = 0; i <= span.Length; i++)
                {
                    if (i == span.Length || span[i] == ',')
                    {
                        var field = span[start..i];
                        switch (col)
                        {
                            case 0: calcTime = long.Parse(field, CultureInfo.InvariantCulture); break;
                            case 1: intervalHours = int.Parse(field, NumberStyles.Integer, CultureInfo.InvariantCulture); break;
                            case 2: rate = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                        }
                        col++;
                        start = i + 1;
                        if (col > 2) break;
                    }
                }

                calcTime = NormalizeEpochMs(calcTime);
                events.Add(new FundingEvent(symbol, rate, markPrice: 0m, timestampMs: calcTime, intervalHours: intervalHours));
            }

            return events;
        }

        /// <summary>
        /// Read and concatenate every funding file in <paramref name="filePaths"/>
        /// (already sorted) for <paramref name="symbol"/>, streaming each with a
        /// sequential-scan <see cref="FileStream"/>.
        /// </summary>
        public static List<FundingEvent> ReadFundingRates(IReadOnlyList<string> filePaths, string symbol)
        {
            var all = new List<FundingEvent>();
            foreach (var fp in filePaths)
            {
                using var stream = new FileStream(
                    fp, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 1 << 20, FileOptions.SequentialScan);
                using var reader = new StreamReader(stream);
                all.AddRange(ReadFundingRates(reader, symbol));
            }
            return all;
        }
    }
}
