namespace GripTrader.Core.Settings
{
    /// <summary>
    /// Margin mode for the perp executor.
    /// <para>
    /// <see cref="Isolated"/> is the conservative falsification default: each leg
    /// has its own allocated wallet and is liquidated on its own when its
    /// allocated equity falls below maintenance — this is the only mode that can
    /// represent "one leg blows up during a divergence before the hedge pays off",
    /// the hazard the project must not smooth away.
    /// </para>
    /// <para>
    /// <see cref="Cross"/> is a separate, labelled scenario where liquidation is
    /// account-level (the winning leg nets against the loser). The account-level
    /// aggregation lives in the multi-symbol feeder/harness that sums the per-leg
    /// <c>EquityBreakdown</c>s — NOT in the single-symbol executor. The executor
    /// records the mode but always liquidates against its own (per-leg) equity.
    /// </para>
    /// </summary>
    public enum MarginMode
    {
        Isolated = 0,
        Cross = 1
    }

    /// <summary>
    /// How the liquidation check probes the mark bar.
    /// <para>
    /// <see cref="AdverseExtreme"/> (default) probes the adverse intra-bar mark
    /// extreme: <c>mark.High</c> for shorts, <c>mark.Low</c> for longs. On 1h bars
    /// this is an <b>upper-bound heuristic</b> — synthetic wicks may liquidate a
    /// leg that survived to the close — but it errs toward more-frequent
    /// liquidation, the safe falsification direction.
    /// </para>
    /// <para>
    /// <see cref="CloseOnly"/> restricts the probe to <c>mark.Close</c>, for when
    /// the synthetic intra-bar path is judged unreliable.
    /// </para>
    /// </summary>
    public enum LiquidationProbe
    {
        AdverseExtreme = 0,
        CloseOnly = 1
    }

    /// <summary>
    /// Strategy-agnostic configuration for the perp mock executor
    /// (<c>MockPerpExecutor</c> in <c>Trading.Backtest</c>). This is deliberately
    /// <b>not</b> added to <see cref="TradingSettingsBase"/> (which is
    /// spot/transport-shaped and shared with other bots): a consuming bot's own
    /// settings <i>construct</i> one of these and pass it to the executor ctor;
    /// core never sees a concrete strategy's settings type.
    /// <para>
    /// All monetary/percentage fields are <see cref="decimal"/>. Percentages are
    /// decimal fractions (<c>0.0005m</c> = 0.05%). The defaults are conservative —
    /// they over-state cost and hazard, never PnL.
    /// </para>
    /// </summary>
    /// <param name="Leverage">
    /// Position leverage. Initial margin = <c>notional / Leverage</c>. Keep modest
    /// so the strategy's hard stop dominates over the bracket-driven liquidation.
    /// </param>
    /// <param name="MarginMode">
    /// <see cref="Settings.MarginMode.Isolated"/> (conservative default, per-leg
    /// liquidation) or <see cref="Settings.MarginMode.Cross"/> (labelled scenario).
    /// </param>
    /// <param name="MaintMarginRatio">
    /// Maintenance margin ratio for the (single, static) bracket; a high value
    /// (default 0.05 = 5%) errs toward more liquidation since no historical
    /// brackets exist. Maintenance = <c>notional × MaintMarginRatio − MaintAmount</c>.
    /// </param>
    /// <param name="MaintAmount">
    /// The bracket's maintenance <b>amount</b> (Binance <c>cum</c>): an absolute
    /// USDT decimal, NOT a fraction. Default 0 (largest liquidation exposure). A
    /// larger value reduces maintenance and pushes the liquidation level out.
    /// </param>
    /// <param name="TakerFeeRate">Notional taker fee per leg (default 0.0005 = 0.05%). Paid on market fills.</param>
    /// <param name="MakerFeeRate">Notional maker fee per leg (default 0.0002 = 0.02%). Paid on resting-limit fills.</param>
    /// <param name="SlippagePct">
    /// One-sided market-fill slippage as a decimal fraction (default 0.001 = 10 bps).
    /// Since kline data carries <c>bid == ask</c> (no modeled spread), this stands
    /// in for half-spread + impact; keep a conservative non-zero floor.
    /// </param>
    /// <param name="LiquidationProbe">
    /// <see cref="Settings.LiquidationProbe.AdverseExtreme"/> (default) or
    /// <see cref="Settings.LiquidationProbe.CloseOnly"/>.
    /// </param>
    /// <param name="InitialWalletQuote">Starting quote-asset (USDT) wallet for the leg.</param>
    public sealed record PerpExecutorConfig(
        decimal Leverage = 3m,
        MarginMode MarginMode = MarginMode.Isolated,
        decimal MaintMarginRatio = 0.05m,
        decimal MaintAmount = 0m,
        decimal TakerFeeRate = 0.0005m,
        decimal MakerFeeRate = 0.0002m,
        decimal SlippagePct = 0.001m,
        LiquidationProbe LiquidationProbe = LiquidationProbe.AdverseExtreme,
        decimal InitialWalletQuote = 10_000m);
}
