using System.Threading.Tasks;
using GripTrader.Core.Backtest;
using GripTrader.Core.Models;
using GripTrader.Core.Settings;
using Xunit;

namespace GripTrader.Core.Backtest.Tests
{
    /// <summary>
    /// Unit tests for the perp mock executor. Every monetary assertion is exact
    /// decimal arithmetic — the executor over-states cost/hazard, never PnL, so
    /// these traps must hold to the cent.
    /// </summary>
    public class MockPerpExecutorTests
    {
        private const string Sym = "BTCUSDT";

        // A flat, no-slippage, taker=0.05% / maker=0.02% config with a large
        // wallet and modest leverage, so most tests isolate one mechanic.
        private static PerpExecutorConfig BaseConfig(
            decimal leverage = 3m,
            decimal maintRatio = 0.05m,
            decimal maintAmount = 0m,
            decimal taker = 0.0005m,
            decimal maker = 0.0002m,
            decimal slippage = 0m,
            decimal wallet = 100_000m,
            MarginMode mode = MarginMode.Isolated,
            LiquidationProbe probe = LiquidationProbe.AdverseExtreme)
            => new PerpExecutorConfig(
                Leverage: leverage,
                MarginMode: mode,
                MaintMarginRatio: maintRatio,
                MaintAmount: maintAmount,
                TakerFeeRate: taker,
                MakerFeeRate: maker,
                SlippagePct: slippage,
                LiquidationProbe: probe,
                InitialWalletQuote: wallet);

        private static BidAsk Quote(decimal px, long ts = 0) => new BidAsk(px, px, ts);

        private static MarkBar Mark(decimal px, long ts = 0) => new MarkBar(px, px, px, px, ts);

        // ---- 1. Fee leaves |size| exact (inverse of spot base-asset netting) ----
        [Fact]
        public async Task Fee_LeavesSizeExact_AndDebitsQuoteWallet()
        {
            var px = new MockPerpExecutor(BaseConfig());
            px.ProcessTick(Sym, Quote(100m), Mark(100m));

            var qty = 10m;
            var res = await px.PlaceMarketAsync(Sym, qty, isBuy: true);

            // Signed size is EXACTLY +qty — the fee never touches size.
            Assert.Equal(qty, px.NetQty);
            Assert.Equal(100m, res.Price);
            Assert.Equal(qty, res.Quantity);

            // Fee = notional × taker = 100 × 10 × 0.0005 = 0.5, debited from quote.
            var expectedFee = 100m * 10m * 0.0005m;
            Assert.Equal(expectedFee, px.CumFeesQuote);

            // Wallet dropped by exactly fee + initial margin (notional/leverage).
            var initialMargin = (100m * 10m) / 3m;
            Assert.Equal(100_000m - expectedFee - initialMargin, px.Wallet);
            Assert.Equal(initialMargin, px.AllocatedMargin);
        }

        // ---- 2. Short PnL sign: mark below entry => profit ----
        [Fact]
        public async Task ShortPnl_MarkBelowEntry_IsProfit_AboveEntry_IsLoss()
        {
            var px = new MockPerpExecutor(BaseConfig(taker: 0m, maker: 0m));
            px.ProcessTick(Sym, Quote(100m), Mark(100m));
            await px.PlaceMarketAsync(Sym, 5m, isBuy: false); // open short at 100

            Assert.Equal(-5m, px.NetQty);
            Assert.Equal(100m, px.AvgEntryPrice);

            // Equity at entry (mark 100): wallet + allocated + 0 unrealized.
            var atEntry = await px.GetCurrentTotalEquityAsync(Sym, 100m);
            Assert.Equal(100_000m, atEntry); // no fees, so equity == initial wallet

            // Mark BELOW entry (90) => short profit = signedSize×(mark−entry)
            //   = -5 × (90 − 100) = +50.
            var below = await px.GetCurrentTotalEquityAsync(Sym, 90m);
            Assert.Equal(100_050m, below);

            // Mark ABOVE entry (110) => loss = -5 × (110 − 100) = -50.
            var above = await px.GetCurrentTotalEquityAsync(Sym, 110m);
            Assert.Equal(99_950m, above);
        }

        // ---- 3. Funding sign + separate accounting ----
        [Fact]
        public async Task Funding_PositiveRateLong_DebitsWallet_TrackedSeparately()
        {
            var px = new MockPerpExecutor(BaseConfig(taker: 0m, maker: 0m));
            px.ProcessTick(Sym, Quote(100m), Mark(100m));
            await px.PlaceMarketAsync(Sym, 10m, isBuy: true); // long 10 @ 100

            var realizedBefore = px.RealizedPricePnl;
            var walletBefore = px.Wallet;

            // Positive rate + long => longs pay shorts => wallet DOWN.
            // Δwallet = -signedSize × mark × rate = -(+10) × 100 × 0.0001 = -0.1
            px.ApplyFunding(Sym, markPrice: 100m, fundingRate: 0.0001m, timestampMs: 1_000L);

            var expectedDelta = -10m * 100m * 0.0001m; // -0.1
            Assert.Equal(walletBefore + expectedDelta, px.Wallet);
            Assert.Equal(expectedDelta, px.CumFunding);
            Assert.Equal(expectedDelta, px.RealizedFunding);

            // Funding is NEVER folded into price PnL.
            Assert.Equal(realizedBefore, px.RealizedPricePnl);
        }

        // ---- 4. Funding interval-agnostic: arbitrary non-8h timestamps ----
        [Fact]
        public async Task Funding_IntervalAgnostic_TwoArbitraryEventsApplyIndependently()
        {
            var px = new MockPerpExecutor(BaseConfig(taker: 0m, maker: 0m));
            px.ProcessTick(Sym, Quote(100m), Mark(100m));
            await px.PlaceMarketAsync(Sym, 4m, isBuy: false); // short 4 @ 100

            // Short + positive rate => shorts RECEIVE: Δ = -(-4)×mark×rate = +.
            // Two events at arbitrary (non-8h) timestamps, different marks/rates.
            px.ApplyFunding(Sym, markPrice: 100m, fundingRate: 0.0002m, timestampMs: 12_345L);
            px.ApplyFunding(Sym, markPrice: 110m, fundingRate: 0.0001m, timestampMs: 17_777L);

            var d1 = -(-4m) * 100m * 0.0002m; // +0.08
            var d2 = -(-4m) * 110m * 0.0001m; // +0.044
            Assert.Equal(d1 + d2, px.CumFunding);
        }

        // ---- 5. Isolated liquidation on adverse extreme before convergence ----
        [Fact]
        public async Task Liquidation_ShortBreachedByMarkHigh_UnderAdverseExtreme_NotUnderCloseOnly()
        {
            // Leverage 10 so a short's allocated equity is thin; high maint ratio.
            // Open short 1 @ 100 => allocated margin = notional/lev = 100/10 = 10.
            // At mark 130: notional=130, maint=130×0.10=13; unrealized for short =
            //   -1×(130-100) = -30; allocated equity = 10 + (-30) = -20 < 13 => liq.
            // At mark 105 (close): unrealized = -5; equity = 10-5 = 5; maint=105×0.10=10.5
            //   => 5 < 10.5 ALSO liquidates. Use a close that SURVIVES: close = 100.
            var cfgAdverse = BaseConfig(leverage: 10m, maintRatio: 0.10m, taker: 0m, maker: 0m);
            var pxAdverse = new MockPerpExecutor(cfgAdverse);
            pxAdverse.ProcessTick(Sym, Quote(100m), Mark(100m));
            await pxAdverse.PlaceMarketAsync(Sym, 1m, isBuy: false);

            // Bar: open 100, HIGH 130 (adverse for short), low 99, CLOSE 100 (survives).
            var bar = new MarkBar(100m, 130m, 99m, 100m, 2_000L);
            pxAdverse.ProcessTick(Sym, Quote(100m), bar);
            Assert.True(pxAdverse.WasLiquidated);
            Assert.Equal(0m, pxAdverse.NetQty);

            // Same bar under CloseOnly: close=100 == entry, no loss => NOT liquidated.
            var cfgClose = BaseConfig(leverage: 10m, maintRatio: 0.10m, taker: 0m, maker: 0m,
                                      probe: LiquidationProbe.CloseOnly);
            var pxClose = new MockPerpExecutor(cfgClose);
            pxClose.ProcessTick(Sym, Quote(100m), Mark(100m));
            await pxClose.PlaceMarketAsync(Sym, 1m, isBuy: false);
            pxClose.ProcessTick(Sym, Quote(100m), bar);
            Assert.False(pxClose.WasLiquidated);
            Assert.Equal(-1m, pxClose.NetQty);

            // maintAmount > 0 pushes the level OUT (less liquidation): with a big
            // maintAmount the same adverse high no longer breaches.
            // equity at high 130 = 10 + (-30) = -20. maint = 130×0.10 - amount.
            // To survive we'd need equity >= maint, i.e. -20 >= 13 - amount =>
            // amount >= 33. Use amount=40 => maint = clamp(13-40,>=0)=0; -20 < 0
            // is still a breach (equity negative). So instead show the SIGN with a
            // milder bar where amount flips the verdict:
            //   mark high 108 => equity = 10 + (-8) = 2; maint(amount0)=108×0.10=10.8
            //   => 2 < 10.8 LIQUIDATES with amount 0; with amount 9 => maint=1.8 =>
            //   2 >= 1.8 SURVIVES.
            var milderBar = new MarkBar(100m, 108m, 99m, 100m, 2_000L);

            var pxAmt0 = new MockPerpExecutor(BaseConfig(leverage: 10m, maintRatio: 0.10m,
                maintAmount: 0m, taker: 0m, maker: 0m));
            pxAmt0.ProcessTick(Sym, Quote(100m), Mark(100m));
            await pxAmt0.PlaceMarketAsync(Sym, 1m, isBuy: false);
            pxAmt0.ProcessTick(Sym, Quote(100m), milderBar);
            Assert.True(pxAmt0.WasLiquidated); // amount 0 => liquidated

            var pxAmt9 = new MockPerpExecutor(BaseConfig(leverage: 10m, maintRatio: 0.10m,
                maintAmount: 9m, taker: 0m, maker: 0m));
            pxAmt9.ProcessTick(Sym, Quote(100m), Mark(100m));
            await pxAmt9.PlaceMarketAsync(Sym, 1m, isBuy: false);
            pxAmt9.ProcessTick(Sym, Quote(100m), milderBar);
            Assert.False(pxAmt9.WasLiquidated); // larger maintAmount pushes level out

            // Higher ratio pulls the level IN: same milder bar, ratio 0.20.
            //   maint(amount0) = 108×0.20 = 21.6; equity 2 < 21.6 => liquidates
            //   (already liquidates at 0.10 too) — instead show a bar that survives
            //   at low ratio but not high ratio:
            //   mark high 101 => equity = 10 + (-1) = 9.
            //     ratio 0.05 => maint=101×0.05=5.05 => 9 >= 5.05 SURVIVES.
            //     ratio 0.10 => maint=101×0.10=10.1 => 9 < 10.1 LIQUIDATES.
            var tinyBar = new MarkBar(100m, 101m, 99m, 100m, 2_000L);

            var pxLowRatio = new MockPerpExecutor(BaseConfig(leverage: 10m, maintRatio: 0.05m,
                taker: 0m, maker: 0m));
            pxLowRatio.ProcessTick(Sym, Quote(100m), Mark(100m));
            await pxLowRatio.PlaceMarketAsync(Sym, 1m, isBuy: false);
            pxLowRatio.ProcessTick(Sym, Quote(100m), tinyBar);
            Assert.False(pxLowRatio.WasLiquidated);

            var pxHighRatio = new MockPerpExecutor(BaseConfig(leverage: 10m, maintRatio: 0.10m,
                taker: 0m, maker: 0m));
            pxHighRatio.ProcessTick(Sym, Quote(100m), Mark(100m));
            await pxHighRatio.PlaceMarketAsync(Sym, 1m, isBuy: false);
            pxHighRatio.ProcessTick(Sym, Quote(100m), tinyBar);
            Assert.True(pxHighRatio.WasLiquidated);
        }

        // ---- 6. Maintenance formula + initial margin ----
        [Fact]
        public async Task MaintenanceAndInitialMargin_ExactFormulas()
        {
            // notional = mark × |size|; maint = notional×ratio − amount (clamp >=0);
            // initial = notional/leverage.
            var cfg = BaseConfig(leverage: 4m, maintRatio: 0.05m, maintAmount: 2m,
                                 taker: 0m, maker: 0m);
            var px = new MockPerpExecutor(cfg);
            px.ProcessTick(Sym, Quote(200m), Mark(200m));
            await px.PlaceMarketAsync(Sym, 3m, isBuy: true); // long 3 @ 200

            // initial = (200×3)/4 = 150.
            Assert.Equal(150m, px.AllocatedMargin);

            // Breakdown at mark 200: notional=600, maint = 600×0.05 - 2 = 28.
            var bd = await px.GetEquityBreakdownAsync(Sym, 200m);
            Assert.Equal(600m * 0.05m - 2m, bd.MaintenanceMargin);

            // Clamp >= 0: a huge maintAmount drives the raw formula negative.
            var cfgClamp = BaseConfig(leverage: 4m, maintRatio: 0.05m, maintAmount: 10_000m,
                                      taker: 0m, maker: 0m);
            var pxClamp = new MockPerpExecutor(cfgClamp);
            pxClamp.ProcessTick(Sym, Quote(200m), Mark(200m));
            await pxClamp.PlaceMarketAsync(Sym, 3m, isBuy: true);
            var bdClamp = await pxClamp.GetEquityBreakdownAsync(Sym, 200m);
            Assert.Equal(0m, bdClamp.MaintenanceMargin); // 30 - 10000 clamped to 0
        }

        // ---- 7. Multi-leg equity signs short correctly + default-overload parity ----
        [Fact]
        public async Task MultiLeg_ShortSignAggregates_AndDefaultOverloadParity()
        {
            // A pair: long leg (ETH) + short leg (BTC), one executor each.
            var longLeg = new MockPerpExecutor(BaseConfig(taker: 0m, maker: 0m, wallet: 50_000m));
            var shortLeg = new MockPerpExecutor(BaseConfig(taker: 0m, maker: 0m, wallet: 50_000m));

            longLeg.ProcessTick("ETHUSDT", Quote(2_000m), Mark(2_000m));
            shortLeg.ProcessTick("BTCUSDT", Quote(50_000m), Mark(50_000m));
            await longLeg.PlaceMarketAsync("ETHUSDT", 5m, isBuy: true);   // long 5 @ 2000
            await shortLeg.PlaceMarketAsync("BTCUSDT", 0.2m, isBuy: false); // short 0.2 @ 50000

            // Move: ETH up to 2100 (long +500), BTC down to 49000 (short +200).
            var longEq = await longLeg.GetCurrentTotalEquityAsync("ETHUSDT", 2_100m);
            var shortEq = await shortLeg.GetCurrentTotalEquityAsync("BTCUSDT", 49_000m);

            // long unrealized = +5×(2100-2000) = +500 => 50500.
            Assert.Equal(50_500m, longEq);
            // short unrealized = -0.2×(49000-50000) = +200 => 50200 (short sign correct).
            Assert.Equal(50_200m, shortEq);

            // Account (cross) aggregation lives in the harness: it just sums.
            var bdLong = await longLeg.GetEquityBreakdownAsync("ETHUSDT", 2_100m);
            var bdShort = await shortLeg.GetEquityBreakdownAsync("BTCUSDT", 49_000m);
            var accountMaint = bdLong.MaintenanceMargin + bdShort.MaintenanceMargin;
            Assert.True(accountMaint > 0m);

            // Default-overload parity: the spot MockBinanceExecutor uses the DEFAULT
            // interface implementation and returns (spotEquity, 0m) unchanged.
            var spot = new GripTrader.Core.Backtest.MockBinanceExecutor(initialBankroll: 1_000m);
            GripTrader.Core.Bot.IOrderExecutor spotAsSeam = spot;
            var spotBd = await spotAsSeam.GetEquityBreakdownAsync(Sym, 100m);
            var spotEq = await spotAsSeam.GetCurrentTotalEquityAsync(Sym, 100m);
            Assert.Equal(spotEq, spotBd.Equity);
            Assert.Equal(0m, spotBd.MaintenanceMargin);
        }

        // ---- 8. Resting short-limit match (the path the spot mock lacks) ----
        [Fact]
        public async Task RestingLimits_ShortSellFillsOnBidGEPrice_LongBuyOnAskLEPrice()
        {
            // SHORT sell limit @ 105: fills when Bid >= 105 (the path MockBinanceExecutor lacks).
            var pxShort = new MockPerpExecutor(BaseConfig(taker: 0m, maker: 0m));
            var sell = await pxShort.PlaceLimitAsync(Sym, 2m, 105m, isBuy: false);
            Assert.True(sell.OrderId > 0);

            // Bid below 105: no fill.
            pxShort.ProcessTick(Sym, Quote(104m), Mark(104m));
            Assert.Equal(0m, pxShort.NetQty);

            // Bid reaches 105: fills at the resting price, net goes short.
            pxShort.ProcessTick(Sym, Quote(105m), Mark(105m));
            Assert.Equal(-2m, pxShort.NetQty);
            Assert.Equal(105m, pxShort.AvgEntryPrice);

            // Symmetric LONG buy limit @ 95: fills when Ask <= 95.
            var pxLong = new MockPerpExecutor(BaseConfig(taker: 0m, maker: 0m));
            await pxLong.PlaceLimitAsync(Sym, 3m, 95m, isBuy: true);
            pxLong.ProcessTick(Sym, Quote(96m), Mark(96m)); // ask 96 > 95: no fill
            Assert.Equal(0m, pxLong.NetQty);
            pxLong.ProcessTick(Sym, Quote(95m), Mark(95m)); // ask 95 <= 95: fill
            Assert.Equal(3m, pxLong.NetQty);
            Assert.Equal(95m, pxLong.AvgEntryPrice);
        }

        // ---- 9. Determinism: replay the same scripted sequence twice ----
        [Fact]
        public async Task Determinism_SameScriptedSequence_ByteIdenticalState()
        {
            static async Task<(decimal wallet, decimal net, decimal funding, decimal fees,
                               decimal realized, decimal equity)> Run()
            {
                var px = new MockPerpExecutor(BaseConfig(leverage: 5m, slippage: 0.001m));

                px.ProcessTick(Sym, Quote(100m, 1_000L), Mark(100m, 1_000L));
                await px.PlaceMarketAsync(Sym, 3m, isBuy: false); // short
                px.ApplyFunding(Sym, 100m, 0.0001m, 1_000L);

                px.ProcessTick(Sym, new BidAsk(101m, 101m, 2_000L), new MarkBar(101m, 102m, 100m, 101m, 2_000L));
                await px.PlaceLimitAsync(Sym, 1m, 99m, isBuy: true); // partial cover limit
                px.ApplyFunding(Sym, 101m, -0.0002m, 2_000L);

                px.ProcessTick(Sym, new BidAsk(99m, 99m, 3_000L), new MarkBar(99m, 100m, 98m, 99m, 3_000L));
                await px.PlaceMarketAsync(Sym, 2m, isBuy: true); // cover more

                var equity = await px.GetCurrentTotalEquityAsync(Sym, 99m);
                return (px.Wallet, px.NetQty, px.CumFunding, px.CumFeesQuote, px.RealizedPricePnl, equity);
            }

            var a = await Run();
            var b = await Run();
            Assert.Equal(a.wallet, b.wallet);
            Assert.Equal(a.net, b.net);
            Assert.Equal(a.funding, b.funding);
            Assert.Equal(a.fees, b.fees);
            Assert.Equal(a.realized, b.realized);
            Assert.Equal(a.equity, b.equity);
        }

        // ---- 10. Reject on insufficient margin: wallet unchanged ----
        [Fact]
        public async Task Reject_WhenInitialMarginExceedsFreeWallet_WalletUnchanged()
        {
            // Tiny wallet, leverage 1 => initial margin == full notional.
            var px = new MockPerpExecutor(BaseConfig(leverage: 1m, wallet: 100m));
            px.ProcessTick(Sym, Quote(100m), Mark(100m));

            var walletBefore = px.Wallet;

            // notional = 100 × 5 = 500; initial = 500/1 = 500 > free wallet 100 => reject.
            var res = await px.PlaceMarketAsync(Sym, 5m, isBuy: true);
            Assert.Equal(0m, res.Quantity); // default(PlaceMarketResult)
            Assert.Equal(0m, px.NetQty);
            Assert.Equal(walletBefore, px.Wallet);
            Assert.Equal(0m, px.AllocatedMargin);
            Assert.Equal(0m, px.CumFeesQuote);

            // A fill that DOES fit: notional=80, initial=80, fee=80×0.0005=0.04 =>
            // free after = 100 - 0.04 - 80 = 19.96 >= 0 => accepted.
            var ok = await px.PlaceMarketAsync(Sym, 0.8m, isBuy: true);
            Assert.Equal(0.8m, ok.Quantity);
            Assert.Equal(0.8m, px.NetQty);
        }

        // ---- 11. Funding from a CLOSED position must not contaminate the next ----
        // position's per-leg liquidation equity (regression: lifetime funding leaked
        // in and could hide a real liquidation).
        [Fact]
        public async Task ClosedPositionFunding_DoesNotContaminate_NextPositionLiquidation()
        {
            var px = new MockPerpExecutor(BaseConfig(leverage: 10m, maintRatio: 0.05m,
                                                     taker: 0m, maker: 0m));
            px.ProcessTick(Sym, Quote(100m), Mark(100m));

            // Open long 10 @ 100 (allocated = 1000/10 = 100), then RECEIVE big funding
            // (long receives when rate < 0): Δ = -(+10)×100×(-0.05) = +50.
            await px.PlaceMarketAsync(Sym, 10m, isBuy: true);
            px.ApplyFunding(Sym, markPrice: 100m, fundingRate: -0.05m, timestampMs: 1_000L);
            Assert.Equal(50m, px.RealizedFunding);

            // Close the long fully. Lifetime RealizedFunding keeps the +50 (reporting),
            // but the per-position funding must reset for the next position.
            await px.PlaceMarketAsync(Sym, 10m, isBuy: false);
            Assert.Equal(0m, px.NetQty);
            Assert.Equal(50m, px.RealizedFunding); // lifetime retained

            // Open a NEW short 10 @ 100 (allocated = 100, position funding = 0).
            await px.PlaceMarketAsync(Sym, 10m, isBuy: false);
            Assert.Equal(-10m, px.NetQty);

            // The new short's allocated equity must be 100 (allocated + 0 unrealized +
            // 0 current-position funding) — NOT 150 (the lifetime +50 must not leak in).
            var bd = await px.GetEquityBreakdownAsync(Sym, 100m);
            Assert.Equal(100m, bd.Equity);

            // Adverse mark high 109: unrealized = -10×(109-100) = -90; allocated equity
            // = 100 - 90 + 0 = 10 < maint = 109×10×0.05 = 54.5 ⇒ LIQUIDATE. Under the
            // contamination bug equity would be 10 + 50 = 60 > 54.5 and wrongly survive.
            px.ProcessTick(Sym, Quote(100m), new MarkBar(100m, 109m, 99m, 100m, 2_000L));
            Assert.True(px.WasLiquidated);
            Assert.Equal(0m, px.NetQty);
        }
    }
}
