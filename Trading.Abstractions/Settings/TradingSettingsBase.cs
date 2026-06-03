using GripTrader.Core.Models;

namespace GripTrader.Core.Settings
{
    /// <summary>
    /// Strategy-agnostic transport / exchange / backtest settings shared by every
    /// bot. A concrete strategy derives from this and adds its own parameters
    /// (e.g. <c>BotSettings : TradingSettingsBase</c>). Because the derived type's
    /// properties remain visible to flat name-based reflection, the Tuner's
    /// <c>ParameterApplier</c> binds base and derived fields uniformly and every
    /// existing flat tuner-config JSON key keeps working.
    /// </summary>
    public abstract class TradingSettingsBase
    {
        public string? Symbol { get; set; } // e.g., "BTCUSDT"
        public bool UseTestnet { get; set; } // spot testnet vs prod
        public int RecvWindowMs { get; set; } = 5000; // REST recvWindow
        public int PersistCooldownSeconds { get; set; } = 5; // state save throttle

        public bool BacktestMode { get; set; }
        public string? BacktestCsvPath { get; set; }

        /// <summary>
        /// Folder of kline CSVs used by the trend filter (SMA + volume + overbought).
        /// </summary>
        public string? BacktestKlineFolderPath { get; set; }

        /// <summary>
        /// Folder of kline CSVs used by the kline feeder when
        /// <see cref="BacktestFeederType"/> is <c>"klines"</c>. Falls back to
        /// <see cref="BacktestKlineFolderPath"/> when null/empty.
        /// </summary>
        public string? BacktestKlineFeederPath { get; set; }

        /// <summary>Which historical-data feeder drives replay: <c>"aggtrades"</c> or <c>"klines"</c>.</summary>
        public string BacktestFeederType { get; set; } = "aggtrades";

        /// <summary>One-sided market-order slippage as a decimal fraction (0.001 = 10 bps). Defaults to 10 bps.</summary>
        public decimal BacktestSlippagePct { get; set; } = 0.001m;

        /// <summary>
        /// Optional exchange filters for backtests (0 = not enforced). When any is set
        /// the mock executor floors price/qty and rejects sub-min orders, exactly like
        /// the live exchange.
        /// </summary>
        public decimal BacktestTickSize { get; set; }
        public decimal BacktestStepSize { get; set; }
        public decimal BacktestMinQty { get; set; }
        public decimal BacktestMinNotional { get; set; }

        /// <summary>
        /// Effective per-side trading commission as a decimal fraction (0.001 = 0.10%).
        /// Used by both the realized-profit math and the backtest mock executor fees.
        /// </summary>
        public decimal CommissionRate { get; set; } = 0.001m;

        /// <summary>Starting equity (USDT) allocated to the bot; baseline for backtest metrics + sweep math.</summary>
        public decimal InitialBankroll { get; set; } = 1000.0m;

        /// <summary>
        /// Builds a <see cref="TradingPair"/> carrying the configured backtest
        /// filters, or <c>null</c> when none are set (the legacy no-filter behaviour).
        /// </summary>
        public TradingPair? BuildBacktestExchangeFilters()
        {
            if (BacktestTickSize <= 0m && BacktestStepSize <= 0m && BacktestMinQty <= 0m && BacktestMinNotional <= 0m)
                return null;
            return new TradingPair(
                @base: string.Empty, quote: string.Empty,
                tickSize: BacktestTickSize, minPrice: 0m, maxPrice: 0m,
                minQty: BacktestMinQty, maxQty: decimal.MaxValue / 4m,
                stepSize: BacktestStepSize, minNotional: BacktestMinNotional, maxNotional: 0m);
        }
    }
}
