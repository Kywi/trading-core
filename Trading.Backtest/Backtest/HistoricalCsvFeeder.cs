using GripTrader.Core.Abstractions;
using GripTrader.Core.Exchange;
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
    /// Reads Binance aggTrades CSV files (from data.binance.vision) and replays
    /// every tick through both the <see cref="MockBinanceExecutor"/> (fill matching)
    /// and the strategy under test (<see cref="IBacktestTickReceiver"/>).
    /// <para>
    /// Expected CSV columns (no header row):
    /// <c>agg_trade_id, price, quantity, first_trade_id, last_trade_id, transact_time, is_buyer_maker</c>
    /// </para>
    /// <para>
    /// Bid/ask are derived from the <c>is_buyer_maker</c> flag:
    /// <c>true</c> → trade hit the bid, <c>false</c> → trade hit the ask.
    /// A running best-bid / best-ask is maintained across rows and files.
    /// </para>
    /// <para>
    /// <see cref="MockBinanceExecutor.ProcessTick"/> is called <b>before</b>
    /// <see cref="IBacktestTickReceiver.OnBacktestTickAsync"/> so that limit-order
    /// fills are already visible when the strategy queries the executor during the
    /// same tick.
    /// </para>
    /// </summary>
    public sealed class HistoricalCsvFeeder : IBacktestFeeder
    {
        private readonly IBacktestTickReceiver _tickReceiver;
        private readonly MockBinanceExecutor _executor;
        private readonly string _symbol;
        private readonly HistoricalTrendManager? _trendManager;
        private readonly bool _useVolumeConfirmation;
        private readonly bool _useOverboughtFilter;
        private readonly decimal _maxDistanceFromSmaPct;

        // Tracks the most recent UTC day (Unix-day index) for which
        // DailyBoundaryCrossed has been raised; -1 means "no tick seen yet".
        private long _lastSampledDay = -1L;

        /// <summary>
        /// Fires once per UTC day boundary while replaying historical data.
        /// Argument is the timestamp (Unix ms) of the first tick of the new
        /// day. Subscribers (e.g. StatsCollector) typically poll the bot's
        /// scoreboard at this point to record an equity-curve sample.
        /// </summary>
        public event System.Action<long>? DailyBoundaryCrossed;

        public int TotalTicksProcessed { get; private set; }

        /// <summary>
        /// Name of the CSV file currently being replayed (updated during
        /// <see cref="PlayHistoricalDataAsync"/>).
        /// </summary>
        public string? CurrentFileName { get; private set; }

        /// <summary>
        /// 1-based index of the file currently being replayed.
        /// </summary>
        public int CurrentFileIndex { get; private set; }

        /// <summary>
        /// Total number of files queued for replay.
        /// </summary>
        public int TotalFiles { get; private set; }

        /// <param name="gridBot">
        /// The grid bot that will process each tick synchronously.
        /// </param>
        /// <param name="executor">
        /// The mock matching engine whose <c>ProcessTick</c> is invoked every line.
        /// </param>
        /// <param name="symbol">
        /// Trading pair symbol (lower-case, e.g. <c>"btcusdt"</c>) forwarded to
        /// <see cref="IBacktestTickReceiver.OnBacktestTickAsync"/>.
        /// </param>
        /// <param name="trendManager">
        /// Optional historical trend manager that provides precomputed SMA values
        /// for each 4h candle. When supplied, the feeder derives an
        /// <c>isUptrend</c> flag per tick and passes it to the grid bot.
        /// </param>
        public HistoricalCsvFeeder(IBacktestTickReceiver tickReceiver, MockBinanceExecutor executor, string symbol,
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
        /// Derives a synthetic (bid, ask) from a single aggTrade. <c>isBuyerMaker</c>
        /// true means a sell aggressor hit the bid; false means a buy aggressor
        /// lifted the ask. The extra rule — the ask can never sit above a price
        /// that just traded — is the B17 fix: previously the ask only moved on
        /// buy-aggressor trades, so during a sell-aggressor down-move it went
        /// stale-high and resting buy limits (and dynamic dip buys) missed fills
        /// the market had actually traded through. Pure + testable.
        /// </summary>
        internal static (decimal bid, decimal ask) DeriveBidAsk(bool isBuyerMaker, decimal price, decimal lastBid, decimal lastAsk)
        {
            if (isBuyerMaker)
                lastBid = price;
            else
                lastAsk = price;

            if (price < lastAsk) lastAsk = price;

            if (lastBid == 0m) lastBid = price;
            if (lastAsk == 0m) lastAsk = price;
            return (lastBid, lastAsk);
        }

        /// <summary>
        /// Read the first data row of an aggTrades CSV and return its price
        /// as the initial ask. Used to seed the strategy's backtest start
        /// before replay begins.
        /// </summary>
        public static decimal ReadInitialAsk(string filePath)
        {
            using var reader = new StreamReader(filePath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || !char.IsAsciiDigit(line[0]))
                    continue; // skip empty lines and optional header

                var span = line.AsSpan();
                int col = 0, start = 0;
                for (int i = 0; i <= span.Length; i++)
                {
                    if (i == span.Length || span[i] == ',')
                    {
                        if (col == 1) // price
                            return decimal.Parse(span[start..i], NumberStyles.Any, CultureInfo.InvariantCulture);
                        col++;
                        start = i + 1;
                    }
                }
            }
            throw new InvalidOperationException("CSV file contains no data rows.");
        }

        /// <summary>
        /// Resolve a path to an ordered list of aggTrades CSV files.
        /// <list type="bullet">
        /// <item>If <paramref name="path"/> is a file, returns that single file.</item>
        /// <item>If it is a directory, scans for
        /// <c>{SYMBOL}-aggTrades-*.csv</c> files and sorts them by the date
        /// portion of the filename (YYYY-MM or YYYY-MM-DD).</item>
        /// </list>
        /// </summary>
        public static List<string> CollectSortedCsvFiles(string path, string symbol)
        {
            if (File.Exists(path))
                return new List<string> { path };

            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Path not found: {path}");

            var prefix = symbol.ToUpperInvariant() + "-aggTrades-";
            var files = Directory.GetFiles(path, "*.csv", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f =>
                {
                    // Extract the date key after the prefix: "2026-02" or "2026-02-15"
                    var name = Path.GetFileNameWithoutExtension(f);
                    return name.Length > prefix.Length
                        ? name[prefix.Length..]
                        : name;
                }, StringComparer.Ordinal)
                .ToList();

            if (files.Count == 0)
                throw new FileNotFoundException(
                    $"No aggTrades CSV files found for {symbol.ToUpperInvariant()} in {path}");

            return files;
        }

        /// <summary>
        /// Replay every row of each file in <paramref name="filePaths"/> through
        /// the backtest engine. Bid/ask state is carried across files so there is
        /// no gap between consecutive months/days.
        /// <para>
        /// Files are streamed line-by-line via <see cref="StreamReader"/> so a
        /// multi-GB aggTrades CSV never materialises as a <c>string[]</c> on the
        /// heap. The OS prefetcher already overlaps disk I/O with the tick loop,
        /// so no manual read-ahead pipeline is needed.
        /// </para>
        /// </summary>
        public async Task PlayHistoricalDataAsync(IReadOnlyList<string> filePaths)
        {
            TotalTicksProcessed = 0;
            TotalFiles = filePaths.Count;
            decimal lastBid = 0m, lastAsk = 0m;

            // Validate all paths before starting replay
            foreach (var fp in filePaths)
            {
                if (!File.Exists(fp))
                    throw new FileNotFoundException("CSV file not found.", fp);
            }

            for (int fileIdx = 0; fileIdx < filePaths.Count; fileIdx++)
            {
                CurrentFileIndex = fileIdx + 1;
                CurrentFileName = Path.GetFileName(filePaths[fileIdx]);
                Logger.LogInformation(
                    $"[Backtest] Playing file {CurrentFileIndex}/{TotalFiles}: {CurrentFileName}");

                // 1 MB buffer keeps disk reads coarse without holding the whole
                // file in memory. ReadLine returns one row at a time, which is
                // immediately parsed and discarded.
                using var stream = new FileStream(
                    filePaths[fileIdx], FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 1 << 20, FileOptions.SequentialScan);
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0)
                        continue;

                    if (!char.IsAsciiDigit(line[0]))
                        continue;

                    var span = line.AsSpan();

                    int col = 0;
                    int start = 0;
                    decimal price = 0m;
                    long transactTime = 0;
                    bool isBuyerMaker = false;

                    for (int i = 0; i <= span.Length; i++)
                    {
                        if (i == span.Length || span[i] == ',')
                        {
                            var field = span[start..i];
                            switch (col)
                            {
                                case 1:
                                    price = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture);
                                    break;
                                case 5:
                                    transactTime = long.Parse(field, CultureInfo.InvariantCulture);
                                    // Binance aggTrades switched to microseconds in newer
                                    // monthly archives (~2024+); klines stay in milliseconds
                                    // and HistoricalTrendManager + downstream code assume ms.
                                    // Anything past ~year 2286 in ms is implausible, so values
                                    // above 10^13 must be microseconds and need to be scaled
                                    // down to ms for a uniform timeline.
                                    if (transactTime > 10_000_000_000_000L)
                                        transactTime /= 1000L;
                                    break;
                                case 6:
                                    isBuyerMaker = field.Length >= 4 && (field[0] == 't' || field[0] == 'T');
                                    break;
                            }
                            col++;
                            start = i + 1;
                            if (col > 6) break;
                        }
                    }

                    (lastBid, lastAsk) = DeriveBidAsk(isBuyerMaker, price, lastBid, lastAsk);

                    // Derive trend direction from historical SMA when available
                    bool? isUptrend = null;
                    if (_trendManager != null)
                    {
                        var snapshot = _trendManager.GetTrend(transactTime);
                        if (snapshot.HasValue)
                        {
                            var s = snapshot.Value;
                            bool priceAboveSma = price > s.PriceSma;
                            bool volumeOk = !_useVolumeConfirmation
                                || s.VolumeSma <= 0m
                                || s.CandleVolume > s.VolumeSma;
                            bool notOverbought = !_useOverboughtFilter
                                || _maxDistanceFromSmaPct <= 0m
                                || s.PriceSma <= 0m
                                || (price - s.PriceSma) / s.PriceSma <= _maxDistanceFromSmaPct;
                            isUptrend = priceAboveSma && volumeOk && notOverbought;
                        }
                    }

                    _executor.ProcessTick(lastBid, lastAsk);
                    await _tickReceiver.OnBacktestTickAsync(new BidAsk(lastBid, lastAsk, transactTime), isUptrend);

                    // Day-boundary equity-curve sample: cheap subscription point
                    // for the StatsCollector. Fires once per UTC day in backtest
                    // time so we get a regular equity series without per-tick cost.
                    if (transactTime > 0)
                    {
                        var day = transactTime / 86_400_000L;
                        if (day != _lastSampledDay)
                        {
                            _lastSampledDay = day;
                            DailyBoundaryCrossed?.Invoke(transactTime);
                        }
                    }

                    TotalTicksProcessed++;
                }
            }

            Logger.LogInformation(
                $"[Backtest] Replay complete. Files={TotalFiles} Ticks={TotalTicksProcessed} " +
                $"Filled={_executor.TotalOrdersFilled} Canceled={_executor.TotalOrdersCanceled} " +
                $"MakerFees={_executor.TotalMakerFeesPaid:F6} TakerFees={_executor.TotalTakerFeesPaid:F6}");
        }
    }
}
