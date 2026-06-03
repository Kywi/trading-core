using GripTrader.Core.Abstractions;
using GripTrader.Core.Models;
using Log;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GripTrader.Core.Backtest
{
    /// <summary>
    /// Reads Binance kline (candlestick) CSVs and replays each bar as four
    /// synthetic ticks through the <see cref="MockBinanceExecutor"/> and the
    /// strategy under test (<see cref="IBacktestTickReceiver"/>).
    /// <para>
    /// Use this feeder when you need to backtest over many years — kline
    /// archives are 1–2 orders of magnitude smaller than aggTrades and stream
    /// fast enough to make multi-year sweeps practical. The cost is fidelity:
    /// bid/ask spread, tick-level slippage, and intra-bar trailing-buy
    /// micro-bounces are smoothed out. Validate execution-sensitive parameter
    /// choices on aggTrades for shorter periods.
    /// </para>
    /// <para>
    /// Expected CSV columns (no header, matches data.binance.vision):
    /// <c>OpenTime, Open, High, Low, Close, Volume, CloseTime, ...</c>
    /// All timestamps are Unix milliseconds. Files are scanned for the
    /// pattern <c>{SYMBOL}-*.csv</c> and sorted by the date portion.
    /// </para>
    /// <para>
    /// Bar-to-tick rule:
    /// </para>
    /// <list type="bullet">
    /// <item>Bullish bar (Close >= Open): O → L → H → C — price dips, then rallies into the close.</item>
    /// <item>Bearish bar (Close &lt; Open):  O → H → L → C — price rises, then falls into the close.</item>
    /// </list>
    /// <para>
    /// Tick timestamps are distributed evenly across the bar. <c>bid</c> and
    /// <c>ask</c> are both set to the synthetic price (no spread modelled).
    /// </para>
    /// </summary>
    public sealed class HistoricalKlineFeeder : IBacktestFeeder
    {
        private readonly IBacktestTickReceiver _tickReceiver;
        private readonly MockBinanceExecutor _executor;
        private readonly string _symbol;
        private readonly HistoricalTrendManager? _trendManager;
        private readonly bool _useVolumeConfirmation;
        private readonly bool _useOverboughtFilter;
        private readonly decimal _maxDistanceFromSmaPct;

        private long _lastSampledDay = -1L;

        public event Action<long>? DailyBoundaryCrossed;

        public int TotalTicksProcessed { get; private set; }
        public string? CurrentFileName { get; private set; }
        public int CurrentFileIndex { get; private set; }
        public int TotalFiles { get; private set; }

        public HistoricalKlineFeeder(IBacktestTickReceiver tickReceiver, MockBinanceExecutor executor, string symbol,
                                     HistoricalTrendManager? trendManager = null,
                                     bool useVolumeConfirmation = false,
                                     bool useOverboughtFilter = true,
                                     decimal maxDistanceFromSmaPct = 0.30m)
        {
            _tickReceiver = tickReceiver ?? throw new ArgumentNullException(nameof(tickReceiver));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _trendManager = trendManager;
            _useVolumeConfirmation = useVolumeConfirmation;
            _useOverboughtFilter = useOverboughtFilter;
            _maxDistanceFromSmaPct = maxDistanceFromSmaPct >= 0m ? maxDistanceFromSmaPct : 0m;
        }

        /// <summary>
        /// Read the first bar of the first kline CSV and return its open
        /// price as the initial ask. Used to seed the strategy's backtest start.
        /// </summary>
        public static decimal ReadInitialAsk(string filePath)
        {
            using var reader = new StreamReader(filePath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || !char.IsAsciiDigit(line[0]))
                    continue;

                var span = line.AsSpan();
                int col = 0, start = 0;
                for (int i = 0; i <= span.Length; i++)
                {
                    if (i == span.Length || span[i] == ',')
                    {
                        if (col == 1) // Open
                            return decimal.Parse(span[start..i], NumberStyles.Any, CultureInfo.InvariantCulture);
                        col++;
                        start = i + 1;
                    }
                }
            }
            throw new InvalidOperationException("Kline CSV file contains no data rows.");
        }

        /// <summary>
        /// Resolve a path to an ordered list of kline CSV files. Single file
        /// or directory both supported; for a directory, files matching
        /// <c>{SYMBOL}-*.csv</c> are sorted by the date portion of the filename
        /// (e.g. <c>BNBUSDT-1h-2024-03.csv</c>).
        /// </summary>
        public static List<string> CollectSortedKlineFiles(string path, string symbol)
        {
            if (File.Exists(path))
                return new List<string> { path };

            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Path not found: {path}");

            var prefix = symbol.ToUpperInvariant() + "-";
            // Both aggTrades and kline archives use the SYMBOL- prefix; exclude
            // the former by name so a user who points --feeder Klines at an
            // aggTrades folder gets a clear "no kline files found" message
            // instead of a FormatException deep in the parser when "True"/
            // "False" hits the long.Parse for CloseTime.
            var files = Directory.GetFiles(path, "*.csv", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).Contains("aggTrades", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f =>
                {
                    // Filename pattern: SYMBOL-INTERVAL-YYYY-MM[-DD]. Sort by
                    // the trailing YYYY-MM[-DD] part. Strip everything up to
                    // the first 4-digit run that looks like a year.
                    var name = Path.GetFileNameWithoutExtension(f);
                    var idx = name.IndexOf('-', prefix.Length);
                    if (idx > 0 && idx + 1 < name.Length)
                        return name[(idx + 1)..];
                    return name;
                }, StringComparer.Ordinal)
                .ToList();

            if (files.Count == 0)
                throw new FileNotFoundException(
                    $"No kline CSV files found for {symbol.ToUpperInvariant()} in {path}. " +
                    $"For --feeder Klines, BacktestCsvPath must point at a kline-CSV folder " +
                    $"(e.g. files like {symbol.ToUpperInvariant()}-1h-2024-01.csv), not aggTrades.");

            return files;
        }

        public async Task PlayHistoricalDataAsync(IReadOnlyList<string> filePaths)
        {
            TotalTicksProcessed = 0;
            TotalFiles = filePaths.Count;

            foreach (var fp in filePaths)
            {
                if (!File.Exists(fp))
                    throw new FileNotFoundException("Kline CSV file not found.", fp);
            }

            // Sanity-check the first data row of the first file before we
            // commit to a multi-hour run. The most common foot-gun is pointing
            // --feeder Klines at an aggTrades folder, where col 6 is a
            // "True"/"False" flag rather than a CloseTime.
            ValidateKlineSchema(filePaths[0]);

            for (int fileIdx = 0; fileIdx < filePaths.Count; fileIdx++)
            {
                CurrentFileIndex = fileIdx + 1;
                CurrentFileName = Path.GetFileName(filePaths[fileIdx]);
                Logger.LogInformation(
                    $"[Backtest] Playing kline file {CurrentFileIndex}/{TotalFiles}: {CurrentFileName}");

                using var stream = new FileStream(
                    filePaths[fileIdx], FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 1 << 20, FileOptions.SequentialScan);
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || !char.IsAsciiDigit(line[0]))
                        continue;

                    var span = line.AsSpan();

                    long openTime = 0, closeTime = 0;
                    decimal open = 0m, high = 0m, low = 0m, close = 0m;
                    int col = 0, start = 0;

                    for (int i = 0; i <= span.Length; i++)
                    {
                        if (i == span.Length || span[i] == ',')
                        {
                            var field = span[start..i];
                            switch (col)
                            {
                                case 0: openTime = long.Parse(field, CultureInfo.InvariantCulture); break;
                                case 1: open  = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                                case 2: high  = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                                case 3: low   = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                                case 4: close = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                                case 6: closeTime = long.Parse(field, CultureInfo.InvariantCulture); break;
                            }
                            col++;
                            start = i + 1;
                            if (col > 6) break;
                        }
                    }

                    // Some Binance kline archives ship close-time in microseconds
                    // (matches the aggTrades switch); normalise to ms so the
                    // timeline stays uniform with the rest of the engine.
                    if (openTime > 10_000_000_000_000L) openTime /= 1000L;
                    if (closeTime > 10_000_000_000_000L) closeTime /= 1000L;
                    if (closeTime <= openTime) closeTime = openTime + 1;

                    bool isUptrend = false;
                    bool hasTrend = false;
                    if (_trendManager != null)
                    {
                        var snapshot = _trendManager.GetTrend(openTime);
                        if (snapshot.HasValue)
                        {
                            var s = snapshot.Value;
                            // Trend is evaluated at the bar's close price (best
                            // single-price proxy); the tick loop below applies
                            // the same flag to all four synthetic ticks. This
                            // avoids overshoot at the bar's high triggering a
                            // false-positive uptrend during a candle that
                            // ultimately closed below the SMA.
                            bool priceAboveSma = close > s.PriceSma;
                            bool volumeOk = !_useVolumeConfirmation
                                || s.VolumeSma <= 0m
                                || s.CandleVolume > s.VolumeSma;
                            bool notOverbought = !_useOverboughtFilter
                                || _maxDistanceFromSmaPct <= 0m
                                || s.PriceSma <= 0m
                                || (close - s.PriceSma) / s.PriceSma <= _maxDistanceFromSmaPct;
                            isUptrend = priceAboveSma && volumeOk && notOverbought;
                            hasTrend = true;
                        }
                    }

                    var span4 = closeTime - openTime;
                    long t0 = openTime;
                    long t1 = openTime + span4 / 3;
                    long t2 = openTime + 2 * span4 / 3;
                    long t3 = closeTime - 1;

                    decimal p1, p2;
                    if (close >= open) { p1 = low;  p2 = high; }
                    else               { p1 = high; p2 = low;  }

                    await EmitTickAsync(open,  t0, hasTrend ? isUptrend : (bool?)null);
                    await EmitTickAsync(p1,    t1, hasTrend ? isUptrend : (bool?)null);
                    await EmitTickAsync(p2,    t2, hasTrend ? isUptrend : (bool?)null);
                    await EmitTickAsync(close, t3, hasTrend ? isUptrend : (bool?)null);

                    TotalTicksProcessed += 4;
                }
            }

            Logger.LogInformation(
                $"[Backtest] Kline replay complete. Files={TotalFiles} Ticks={TotalTicksProcessed} " +
                $"Filled={_executor.TotalOrdersFilled} Canceled={_executor.TotalOrdersCanceled} " +
                $"MakerFees={_executor.TotalMakerFeesPaid:F6} TakerFees={_executor.TotalTakerFeesPaid:F6}");
        }

        private static void ValidateKlineSchema(string filePath)
        {
            using var reader = new StreamReader(filePath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || !char.IsAsciiDigit(line[0]))
                    continue; // skip header / blank rows

                // Find the 7th comma-separated field (index 6 = CloseTime). It
                // must parse as a long for any valid Binance kline row.
                var span = line.AsSpan();
                int col = 0, start = 0;
                for (int i = 0; i <= span.Length; i++)
                {
                    if (i == span.Length || span[i] == ',')
                    {
                        if (col == 6)
                        {
                            var field = span[start..i];
                            if (!long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                                throw new InvalidDataException(
                                    $"Kline schema check failed for '{Path.GetFileName(filePath)}': " +
                                    $"expected column 6 (CloseTime) to be an integer, got '{field}'. " +
                                    $"This usually means BacktestCsvPath points at aggTrades data — " +
                                    $"switch --feeder to AggTrades or point at kline CSVs.");
                            return; // first data row OK
                        }
                        col++;
                        start = i + 1;
                    }
                }

                throw new InvalidDataException(
                    $"Kline schema check failed for '{Path.GetFileName(filePath)}': row has fewer than 7 columns.");
            }

            throw new InvalidDataException(
                $"Kline schema check failed for '{Path.GetFileName(filePath)}': file contains no data rows.");
        }

        private async Task EmitTickAsync(decimal price, long timestampMs, bool? isUptrend)
        {
            // No spread on kline data — bid == ask. The strategy uses bid for
            // trailing-stop checks and ask for buy triggers; on a single-price
            // feed both decisions fire on the same value.
            _executor.ProcessTick(price, price);
            await _tickReceiver.OnBacktestTickAsync(new BidAsk(price, price, timestampMs), isUptrend);

            if (timestampMs > 0)
            {
                var day = timestampMs / 86_400_000L;
                if (day != _lastSampledDay)
                {
                    _lastSampledDay = day;
                    DailyBoundaryCrossed?.Invoke(timestampMs);
                }
            }
        }
    }
}
