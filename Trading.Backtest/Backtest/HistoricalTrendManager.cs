using Log;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GripTrader.Core.Backtest
{
    /// <summary>Precomputed trend values for a single K-line candle.</summary>
    /// <param name="PriceSma">Simple Moving Average of close prices over the configured period.</param>
    /// <param name="VolumeSma">Simple Moving Average of base-asset volume over the configured volume period. Zero when volume confirmation is disabled.</param>
    /// <param name="CandleVolume">Actual base-asset volume for this specific candle.</param>
    public readonly record struct TrendSnapshot(decimal PriceSma, decimal VolumeSma, decimal CandleVolume);

    /// <summary>
    /// Loads Binance 4h K-line CSV files, computes a Simple Moving Average (SMA)
    /// for each candle, and provides trend lookups by tick timestamp.
    /// <para>
    /// Expected CSV columns (no header row):
    /// <c>OpenTime, Open, High, Low, Close, Volume, CloseTime, ...</c>
    /// </para>
    /// </summary>
    public sealed class HistoricalTrendManager
    {
        private readonly SortedList<long, TrendSnapshot> _trendByOpenTime = new();

        /// <summary>4 hours in ms — the fallback candle width when it can't be inferred (e.g. a single candle).</summary>
        private const long FourHoursMs = 4L * 60 * 60 * 1000;

        /// <summary>
        /// Epoch values above this (10^13) are microseconds, not milliseconds —
        /// Binance kline/aggTrades archives switched units during 2025. The
        /// feeders already divide such tick timestamps by 1000 before calling
        /// <see cref="GetTrend"/>; this manager must apply the SAME rule to the
        /// candle OpenTimes it keys on, or the units diverge across the switch.
        /// </summary>
        private const long MicrosecondEpochThreshold = 10_000_000_000_000L;

        /// <summary>Normalise a microsecond epoch to milliseconds; ms values pass through unchanged.</summary>
        private static long NormalizeEpochMs(long epoch)
            => epoch > MicrosecondEpochThreshold ? epoch / 1000L : epoch;

        /// <summary>
        /// Candle width in ms, inferred from the data (median spacing of
        /// consecutive OpenTimes) so the manager is interval-agnostic (4h, 1d,
        /// 1h, …) instead of assuming 4h. Used only to bound the very last
        /// candle; interior candles are bounded by the next candle's OpenTime.
        /// </summary>
        private long _candleWidthMs = FourHoursMs;

        /// <summary>Number of SMA data points that were computed.</summary>
        public int DataPointCount => _trendByOpenTime.Count;

        /// <summary>
        /// Reads all <c>*.csv</c> files in <paramref name="folderPath"/>, parses
        /// <c>OpenTime</c> (column 0), <c>Close</c> (column 4), and <c>Volume</c>
        /// (column 5, base asset), calculates a price SMA over <paramref name="period"/>
        /// candles and a volume SMA over <paramref name="volumeSmaPeriod"/> candles using
        /// an O(n) sliding-window, and stores the results keyed by OpenTime.
        /// </summary>
        /// <param name="folderPath">Folder containing 4h K-line CSV files.</param>
        /// <param name="period">Number of candles for the price SMA (e.g. 200).</param>
        /// <param name="volumeSmaPeriod">Number of candles for the volume SMA (e.g. 20). Zero disables volume tracking.</param>
        public void LoadData(string folderPath, int period, int volumeSmaPeriod = 0)
        {
            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException($"Kline folder not found: {folderPath}");
            if (period <= 0)
                throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");

            var files = Directory.GetFiles(folderPath, "*.csv", SearchOption.TopDirectoryOnly)
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();

            if (files.Count == 0)
                throw new FileNotFoundException($"No CSV files found in {folderPath}");

            // Collect all candles: (OpenTime, Close, Volume)
            var candles = new List<(long OpenTime, decimal Close, decimal Volume)>();

            foreach (var file in files)
            {
                using var reader = new StreamReader(file);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || !char.IsAsciiDigit(line[0]))
                        continue;

                    var span = line.AsSpan();
                    int col = 0, start = 0;
                    long openTime = 0;
                    decimal close = 0m;
                    decimal volume = 0m;

                    for (int i = 0; i <= span.Length; i++)
                    {
                        if (i == span.Length || span[i] == ',')
                        {
                            var field = span[start..i];
                            switch (col)
                            {
                                case 0: // OpenTime (ms epoch)
                                    openTime = long.Parse(field, CultureInfo.InvariantCulture);
                                    break;
                                case 4: // Close price
                                    close = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture);
                                    break;
                                case 5: // Base asset volume
                                    volume = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture);
                                    break;
                            }
                            col++;
                            start = i + 1;
                            if (col > 5) break;
                        }
                    }

                    candles.Add((openTime, close, volume));
                }
            }

            BuildFromCandles(candles, period, volumeSmaPeriod);

            Logger.LogInformation(
                $"[HistoricalTrendManager] Loaded {candles.Count} candle rows from {files.Count} files, " +
                $"computed {_trendByOpenTime.Count} SMA({period}) / VolSMA({volumeSmaPeriod}) snapshots; candleWidth={_candleWidthMs}ms.");
        }

        /// <summary>
        /// Builds the trend snapshots from raw (possibly overlapping/duplicate)
        /// candle rows. Exposed for unit testing without disk IO.
        /// <para>Three correctness rules are enforced here:</para>
        /// <list type="bullet">
        /// <item><b>De-duplication</b> by OpenTime (overlapping monthly/daily archives or
        /// re-downloads would otherwise double-count a candle in the sliding window).</item>
        /// <item><b>No lookahead</b>: the snapshot stored at candle i's OpenTime is the SMA
        /// of the <c>period</c> candles ending at i-1 — i.e. only candles that had
        /// fully closed before candle i opened. Live, an in-progress candle's close
        /// isn't known, so the backtest must not peek at candle i's own close.</item>
        /// <item><b>Interval-agnostic</b>: the candle width is inferred from the data, not
        /// assumed to be 4h.</item>
        /// </list>
        /// </summary>
        internal void BuildFromCandles(List<(long OpenTime, decimal Close, decimal Volume)> rawCandles, int period, int volumeSmaPeriod)
        {
            if (period <= 0)
                throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");

            _trendByOpenTime.Clear();
            _candleWidthMs = FourHoursMs;
            if (rawCandles.Count == 0)
                return;

            // Normalise epoch units BEFORE sort/dedup. Binance kline archives
            // switched from millisecond to microsecond OpenTimes during 2025. The
            // feeders divide tick timestamps > 10^13 by 1000 before calling
            // GetTrend, but this builder historically keyed on the RAW OpenTime.
            // Mixed units mean post-switch (microsecond) candles get keys ~1000x
            // larger than the feeder's ms lookups, so GetTrend can never reach
            // them: every post-switch tick resolves to the last pre-switch candle
            // and the trend/volume gate freezes, silently blocking all new buys
            // for the remainder of a backtest that crosses the boundary. Mirror
            // the feeders here so keys share the ms unit they query with.
            for (int i = 0; i < rawCandles.Count; i++)
            {
                var c = rawCandles[i];
                rawCandles[i] = (NormalizeEpochMs(c.OpenTime), c.Close, c.Volume);
            }

            rawCandles.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));

            // De-duplicate by OpenTime (sorted, so dupes are adjacent); last row
            // wins, matching the previous _trendByOpenTime[openTime]= overwrite.
            var candles = new List<(long OpenTime, decimal Close, decimal Volume)>(rawCandles.Count);
            foreach (var c in rawCandles)
            {
                if (candles.Count > 0 && candles[^1].OpenTime == c.OpenTime)
                    candles[^1] = c;
                else
                    candles.Add(c);
            }

            _candleWidthMs = InferCandleWidthMs(candles);

            // O(n) sliding-window SMA, SHIFTED by one candle so it never includes
            // the current candle's own close. At the start of iteration i the
            // window holds candles[max(0,i-period)..i-1]; we emit the snapshot for
            // candle i from that window, THEN slide candle i in for i+1.
            int warmup = Math.Max(period, volumeSmaPeriod > 0 ? volumeSmaPeriod : period);
            decimal priceWindowSum = 0m;
            decimal volumeWindowSum = 0m;

            for (int i = 0; i < candles.Count; i++)
            {
                // Emit using only prior (closed) candles. Require a full window so
                // the SMA is fully-formed (first emit at i == warmup).
                if (i >= warmup)
                {
                    decimal priceSma = priceWindowSum / period;
                    decimal volumeSma = volumeSmaPeriod > 0
                        ? volumeWindowSum / volumeSmaPeriod
                        : 0m;
                    _trendByOpenTime[candles[i].OpenTime] = new TrendSnapshot(priceSma, volumeSma, candles[i].Volume);
                }

                // Slide candle i into the window for the next iteration.
                priceWindowSum += candles[i].Close;
                if (i >= period)
                    priceWindowSum -= candles[i - period].Close;

                if (volumeSmaPeriod > 0)
                {
                    volumeWindowSum += candles[i].Volume;
                    if (i >= volumeSmaPeriod)
                        volumeWindowSum -= candles[i - volumeSmaPeriod].Volume;
                }
            }
        }

        /// <summary>Median spacing of consecutive OpenTimes, or 4h fallback for &lt; 2 candles.</summary>
        private static long InferCandleWidthMs(IReadOnlyList<(long OpenTime, decimal Close, decimal Volume)> candles)
        {
            if (candles.Count < 2)
                return FourHoursMs;
            var diffs = new List<long>(candles.Count - 1);
            for (int i = 1; i < candles.Count; i++)
            {
                var d = candles[i].OpenTime - candles[i - 1].OpenTime;
                if (d > 0) diffs.Add(d);
            }
            if (diffs.Count == 0)
                return FourHoursMs;
            diffs.Sort();
            return diffs[diffs.Count / 2];
        }

        /// <summary>
        /// Finds the 4h candle that contains <paramref name="currentTickTimestamp"/>
        /// and returns the precomputed SMA for that candle.
        /// </summary>
        /// <param name="currentTickTimestamp">
        /// Epoch timestamp in milliseconds (e.g. <c>transact_time</c> from aggTrades).
        /// </param>
        /// <returns>
        /// A <see cref="TrendSnapshot"/> for the 4h window, or <c>null</c> if no data
        /// is available for the given timestamp.
        /// </returns>
        public TrendSnapshot? GetTrend(long currentTickTimestamp)
        {
            // Defensive: the feeders already normalise, but accept a raw
            // microsecond timestamp too so keys and queries never diverge.
            currentTickTimestamp = NormalizeEpochMs(currentTickTimestamp);

            if (_trendByOpenTime.Count == 0)
                return null;

            // Binary search: find the largest OpenTime <= currentTickTimestamp
            var keys = _trendByOpenTime.Keys;
            int lo = 0, hi = keys.Count - 1;
            int idx = -1;

            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (keys[mid] <= currentTickTimestamp)
                {
                    idx = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (idx < 0)
                return null;

            // The tick belongs to candle idx until the NEXT candle opens. Using
            // the next OpenTime as the upper bound (instead of a fixed 4h window)
            // makes this interval-agnostic and means a data gap doesn't blackhole
            // the filter — the last candle before the gap stays in effect. The
            // very last candle has no successor, so bound it by the inferred
            // candle width.
            long openTime = keys[idx];
            long upperBound = idx + 1 < keys.Count ? keys[idx + 1] : openTime + _candleWidthMs;
            if (currentTickTimestamp >= openTime && currentTickTimestamp < upperBound)
                return _trendByOpenTime.Values[idx];

            return null;
        }
    }
}
