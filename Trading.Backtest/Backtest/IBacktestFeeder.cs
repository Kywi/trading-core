using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GripTrader.Core.Backtest
{
    /// <summary>
    /// Common surface implemented by every historical-data feeder
    /// (<see cref="HistoricalCsvFeeder"/> for tick-level aggTrades,
    /// <see cref="HistoricalKlineFeeder"/> for OHLC bars). The trader
    /// owns one feeder at a time and delegates the replay loop to it;
    /// the tuner subscribes to <see cref="DailyBoundaryCrossed"/> for
    /// equity-curve sampling regardless of which feeder is active.
    /// </summary>
    public interface IBacktestFeeder
    {
        int TotalTicksProcessed { get; }
        string? CurrentFileName { get; }
        int CurrentFileIndex { get; }
        int TotalFiles { get; }

        /// <summary>
        /// Raised once per UTC-day boundary while replaying. Argument is the
        /// timestamp (Unix ms) of the first tick of the new day. Subscribers
        /// typically poll the bot's equity at this point to build a regular
        /// equity curve without per-tick overhead.
        /// </summary>
        event Action<long>? DailyBoundaryCrossed;

        /// <summary>Replay every tick of every file in <paramref name="filePaths"/> through the bot and executor.</summary>
        Task PlayHistoricalDataAsync(IReadOnlyList<string> filePaths);
    }
}
