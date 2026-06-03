namespace GripTrader.Core.Backtest
{
    /// <summary>
    /// Selects which historical-data feeder drives a backtest replay.
    /// </summary>
    public enum BacktestFeederType
    {
        /// <summary>Tick-level Binance aggTrades CSVs. Highest fidelity, largest files.</summary>
        AggTrades = 0,

        /// <summary>OHLC kline CSVs replayed as four synthetic ticks per bar. Coarser but fast enough for multi-year sweeps.</summary>
        Klines = 1
    }
}
