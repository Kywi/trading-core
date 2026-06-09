using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GripTrader.Core.Abstractions;
using GripTrader.Core.Backtest;
using GripTrader.Core.Models;
using GripTrader.Core.Settings;
using Xunit;

namespace GripTrader.Core.Backtest.Tests
{
    /// <summary>
    /// Tests for the multi-symbol merge feeder + multi-tick seam. The engine paths are
    /// integer/decimal, so determinism is asserted byte-identical. A
    /// <see cref="MultiTickRecordingReceiver"/> COPIES each tick's legs/funding (the
    /// feeder buffer is do-not-retain — the copy is the assertion vehicle).
    /// </summary>
    public class MultiSymbolKlineFeederTests
    {
        // ============ Test doubles & fixture builders ============

        private sealed record TickRecord(
            MultiTickLeg[] Legs, int CloseLegIndex, FundingEvent[] Funding, bool AccountLiquidatable);

        // Records a deep COPY of each tick (the seam buffer is reused/do-not-retain).
        // Also captures the live IReadOnlyList reference so a test can assert the same
        // instance is handed back across ticks (no per-tick allocation).
        private sealed class MultiTickRecordingReceiver : IBacktestMultiTickReceiver
        {
            public readonly List<TickRecord> Ticks = new();
            public readonly List<IReadOnlyList<MultiTickLeg>> LegListRefs = new();

            public Task OnBacktestTickAsync(
                IReadOnlyList<MultiTickLeg> legs, int closeLegIndex,
                IReadOnlyList<FundingEvent> dueFunding, bool accountLiquidatable)
            {
                LegListRefs.Add(legs);

                var legCopy = new MultiTickLeg[legs.Count];
                for (int i = 0; i < legs.Count; i++) legCopy[i] = legs[i];
                var fundCopy = new FundingEvent[dueFunding.Count];
                for (int i = 0; i < dueFunding.Count; i++) fundCopy[i] = dueFunding[i];

                Ticks.Add(new TickRecord(legCopy, closeLegIndex, fundCopy, accountLiquidatable));
                return Task.CompletedTask;
            }
        }

        private static PerpExecutorConfig Cfg(
            decimal leverage = 3m, decimal maintRatio = 0.05m, decimal maintAmount = 0m,
            decimal taker = 0m, decimal maker = 0m, decimal slippage = 0m,
            decimal wallet = 100_000m, MarginMode mode = MarginMode.Isolated,
            LiquidationProbe probe = LiquidationProbe.AdverseExtreme)
            => new PerpExecutorConfig(leverage, mode, maintRatio, maintAmount, taker, maker,
                slippage, probe, wallet);

        // One kline row: cols 0=open,1=O,2=H,3=L,4=C,5=vol,6=closeTime (mirrors the
        // real layout the feeder span-parses).
        private static string KlineRow(long openTime, decimal o, decimal h, decimal l, decimal c, long closeTime)
            => $"{openTime},{o},{h},{l},{c},0,{closeTime},0,0,0,0,0\n";

        // Mark row: same OHLC layout, volume 0 (never read as real).
        private static string MarkRow(long openTime, decimal o, decimal h, decimal l, decimal c, long closeTime)
            => $"{openTime},{o},{h},{l},{c},0,{closeTime},0,0,0,0,0\n";

        private const string KlineHeader = "open_time,open,high,low,close,volume,close_time,qav,trades,tbb,tbq,ignore\n";

        // A leg fixture: symbol, kline CSV, mark CSV, funding events, executor.
        private sealed class LegFixture
        {
            public string Symbol = "";
            public string Klines = "";
            public string Mark = "";
            public List<FundingEvent> Funding = new();
            public MockPerpExecutor Executor = null!;
        }

        private static (MultiSymbolKlineFeeder feeder, MultiTickRecordingReceiver rec)
            BuildFeeder(IReadOnlyList<LegFixture> legs, MarginMode accountMode = MarginMode.Isolated)
        {
            var rec = new MultiTickRecordingReceiver();
            var tuples = new List<(string, MockPerpExecutor, TextReader, TextReader, IReadOnlyList<FundingEvent>)>();
            foreach (var l in legs)
                tuples.Add((l.Symbol, l.Executor, new StringReader(l.Klines), new StringReader(l.Mark), l.Funding));
            var feeder = MultiSymbolKlineFeeder.ForTextReaders(rec, tuples, accountMode);
            return (feeder, rec);
        }

        // Build a simple aligned two-leg fixture over `bars` hourly bars starting at
        // baseOpen. Each bar: O=H=L=C=price[i] for the leg (flat candles unless wicks
        // supplied). Returns the fixtures (executors fresh).
        private const long HourMs = 3_600_000L;

        private static LegFixture FlatLeg(string sym, long baseOpen, decimal[] prices, MockPerpExecutor exec)
        {
            var k = new StringBuilder(KlineHeader);
            var m = new StringBuilder(KlineHeader);
            for (int i = 0; i < prices.Length; i++)
            {
                long ot = baseOpen + i * HourMs;
                long ct = ot + HourMs - 1;
                k.Append(KlineRow(ot, prices[i], prices[i], prices[i], prices[i], ct));
                m.Append(MarkRow(ot, prices[i], prices[i], prices[i], prices[i], ct));
            }
            return new LegFixture { Symbol = sym, Klines = k.ToString(), Mark = m.ToString(), Executor = exec };
        }

        // ============ 1. Timestamp-ordered merge of two legs ============
        [Fact]
        public async Task Merge_TwoLegs_NonDecreasingTimestamps_EachBarOnce()
        {
            long b = 1_700_000_000_000L;
            var a = FlatLeg("AAAUSDT", b, new[] { 100m, 101m, 102m }, new MockPerpExecutor(Cfg()));
            var c = FlatLeg("BBBUSDT", b, new[] { 200m, 201m, 202m }, new MockPerpExecutor(Cfg()));
            var (feeder, rec) = BuildFeeder(new[] { a, c });

            await feeder.PlayAsync();

            // Aligned single-interval: 3 merged ticks (one per bar), all legs fresh.
            Assert.Equal(3, rec.Ticks.Count);
            Assert.Equal(3, feeder.TotalTicksProcessed);

            long prev = long.MinValue;
            foreach (var t in rec.Ticks)
            {
                Assert.Equal(2, t.Legs.Length);
                // Tick keyed at close_time (non-decreasing).
                long ts = t.Legs[0].Mark.TimestampMs;
                Assert.True(ts >= prev, "timestamps must be non-decreasing");
                prev = ts;
                // Every leg fresh -> closeLegIndex == 0 (anchor leg).
                Assert.Equal(0, t.CloseLegIndex);
                Assert.False(t.Legs[0].IsStale);
                Assert.False(t.Legs[1].IsStale);
            }

            // Leg-0 closes: 100, 101, 102; leg-1: 200, 201, 202.
            Assert.Equal(100m, rec.Ticks[0].Legs[0].FillQuote.Bid);
            Assert.Equal(202m, rec.Ticks[2].Legs[1].FillQuote.Bid);
        }

        // ============ 2. Funding -> ProcessTick(fixed order) -> cross-agg -> strategy ordering ============
        [Fact]
        public async Task Ordering_FundingBeforeTick_DueFundingOnSeam_CrossFlagOnlyUnderCross()
        {
            long b = 1_700_000_000_000L;
            // Leg 0: open a long via a scripted resting LIMIT before replay; the limit
            // fills on bar 0's tick (ProcessTick before strategy), visible same tick.
            var execA = new MockPerpExecutor(Cfg());
            var execB = new MockPerpExecutor(Cfg());

            // Pre-place a resting buy limit on leg A at 100 (fills when ask<=100 on bar 0,
            // in ProcessTick — before the strategy tick).
            await execA.PlaceLimitAsync("AAAUSDT", quantity: 10m, price: 100m, isBuy: true);

            var a = FlatLeg("AAAUSDT", b, new[] { 100m, 100m }, execA);
            var bb = FlatLeg("BBBUSDT", b, new[] { 200m, 200m }, execB);

            // Funding event for leg A at BAR-1 close (the long is already open from bar 0),
            // so funding applies BEFORE bar 1's ProcessTick and moves the open position's
            // wallet — feeding this tick's liquidation/marking.
            long bar1Close = b + 2 * HourMs - 1;
            a.Funding.Add(new FundingEvent("AAAUSDT", rate: 0.0001m, markPrice: 0m, timestampMs: bar1Close, intervalHours: 8));

            var (feeder, rec) = BuildFeeder(new[] { a, bb }, MarginMode.Isolated);
            await feeder.PlayAsync();

            Assert.Equal(2, rec.Ticks.Count);

            // Bar 0: limit fills (net +10 on A) in ProcessTick, before the strategy tick.
            Assert.Equal(10m, execA.NetQty);
            // No funding due at bar 0 (calc_time is bar-1 close).
            Assert.Empty(rec.Ticks[0].Funding);

            // Bar 1: funding due (calc_time <= close) applied BEFORE the tick; surfaced on
            // the seam exactly once with the mark filled from the leg's mark close.
            Assert.Single(rec.Ticks[1].Funding);
            Assert.Equal("AAAUSDT", rec.Ticks[1].Funding[0].Symbol);
            Assert.True(rec.Ticks[1].Funding[0].MarkPrice > 0m);
            // Funding moved the wallet: long + positive rate => longs pay => wallet down.
            Assert.True(execA.CumFunding < 0m);

            // Isolated mode => accountLiquidatable always false.
            Assert.False(rec.Ticks[0].AccountLiquidatable);
            Assert.False(rec.Ticks[1].AccountLiquidatable);
        }

        [Fact]
        public async Task CrossMode_AccountLiquidatable_TrueWhenAggregateEquityBelowMaint()
        {
            long b = 1_700_000_000_000L;
            // The per-leg executor ALWAYS runs its own isolated liquidation probe in
            // ProcessTick (step 2), regardless of margin mode. The cross flag is the
            // advisory aggregate read AFTER that: leg A's short blows up on an adverse
            // spike at a loss EXCEEDING its allocated margin (wallet goes negative), and
            // even though leg B survives individually, the summed account equity
            // (negative A wallet + B equity) falls below the summed maintenance.
            var execA = new MockPerpExecutor(Cfg(leverage: 10m, maintRatio: 0.10m, wallet: 200m));
            var execB = new MockPerpExecutor(Cfg(leverage: 10m, maintRatio: 0.10m, wallet: 200m));
            await execA.PlaceLimitAsync("AAAUSDT", 5m, 100m, isBuy: false); // short fills bar 0 (bid>=100)
            await execB.PlaceLimitAsync("BBBUSDT", 5m, 100m, isBuy: true);  // long survives

            // Leg A: bar 1 mark spikes hard (high 300) so the short loss (-5×(300-100)=
            // -1000) far exceeds allocated margin (notional/lev = 500/10 = 50) => wallet
            // negative after forced close. Leg B: flat candles (survives, holds maint).
            var ka = new StringBuilder(KlineHeader); var ma = new StringBuilder(KlineHeader);
            long o0 = b, c0 = b + HourMs - 1, o1 = b + HourMs, c1 = o1 + HourMs - 1;
            ka.Append(KlineRow(o0, 100m, 100m, 100m, 100m, c0));
            ma.Append(MarkRow(o0, 100m, 100m, 100m, 100m, c0));
            ka.Append(KlineRow(o1, 100m, 300m, 100m, 300m, c1));
            ma.Append(MarkRow(o1, 100m, 300m, 100m, 300m, c1)); // adverse spike -> A liq, wallet negative
            var a = new LegFixture { Symbol = "AAAUSDT", Klines = ka.ToString(), Mark = ma.ToString(), Executor = execA };

            var bb = FlatLeg("BBBUSDT", b, new[] { 100m, 100m }, execB);

            var (feeder, rec) = BuildFeeder(new[] { a, bb }, MarginMode.Cross);
            await feeder.PlayAsync();

            // Bar 0: both survive -> account not liquidatable.
            Assert.False(rec.Ticks[0].AccountLiquidatable);
            // Bar 1: A blown up with wallet negative; B still holds an open long (maint>0).
            // Sum(equity) < sum(maint) -> advisory cross flag true. Core did NOT force B.
            Assert.True(execA.WasLiquidated);
            Assert.True(execA.Wallet < 0m);
            Assert.Equal(5m, execB.NetQty); // B not force-closed by core
            Assert.True(rec.Ticks[1].AccountLiquidatable);
        }

        // ============ 3. Epoch lockstep: ms vs microsecond fixture identical ============
        [Fact]
        public async Task EpochLockstep_MsAndMicrosecondFixtures_ByteIdenticalMergedStream()
        {
            var msTicks = await RunEpochFixture(scaleToMicros: false);
            var usTicks = await RunEpochFixture(scaleToMicros: true);

            Assert.Equal(msTicks.Count, usTicks.Count);
            for (int i = 0; i < msTicks.Count; i++)
            {
                Assert.Equal(msTicks[i].CloseLegIndex, usTicks[i].CloseLegIndex);
                Assert.Equal(msTicks[i].Legs.Length, usTicks[i].Legs.Length);
                for (int j = 0; j < msTicks[i].Legs.Length; j++)
                {
                    Assert.Equal(msTicks[i].Legs[j].Mark.TimestampMs, usTicks[i].Legs[j].Mark.TimestampMs);
                    Assert.Equal(msTicks[i].Legs[j].FillQuote.Bid, usTicks[i].Legs[j].FillQuote.Bid);
                }
                // Funding application identical to the cent.
                Assert.Equal(msTicks[i].Funding.Length, usTicks[i].Funding.Length);
                for (int j = 0; j < msTicks[i].Funding.Length; j++)
                {
                    Assert.Equal(msTicks[i].Funding[j].TimestampMs, usTicks[i].Funding[j].TimestampMs);
                    Assert.Equal(msTicks[i].Funding[j].Rate, usTicks[i].Funding[j].Rate);
                }
            }
        }

        private static async Task<List<TickRecord>> RunEpochFixture(bool scaleToMicros)
        {
            long b = 1_700_000_000_000L;
            long scale = scaleToMicros ? 1000L : 1L;

            var execA = new MockPerpExecutor(Cfg());
            var execB = new MockPerpExecutor(Cfg());
            await execA.PlaceLimitAsync("AAAUSDT", 3m, 100m, isBuy: true);

            // Build fixtures with timestamps in ms or microseconds (×1000).
            var ka = new StringBuilder(KlineHeader); var ma = new StringBuilder(KlineHeader);
            var kb = new StringBuilder(KlineHeader); var mb = new StringBuilder(KlineHeader);
            decimal[] pa = { 100m, 101m }; decimal[] pb = { 200m, 201m };
            for (int i = 0; i < 2; i++)
            {
                long ot = (b + i * HourMs) * scale;
                long ct = (b + i * HourMs + HourMs - 1) * scale;
                ka.Append(KlineRow(ot, pa[i], pa[i], pa[i], pa[i], ct));
                ma.Append(MarkRow(ot, pa[i], pa[i], pa[i], pa[i], ct));
                kb.Append(KlineRow(ot, pb[i], pb[i], pb[i], pb[i], ct));
                mb.Append(MarkRow(ot, pb[i], pb[i], pb[i], pb[i], ct));
            }

            long fundTs = (b + HourMs - 1) * scale; // bar-0 close, scaled
            var fundA = new List<FundingEvent>
            {
                new FundingEvent("AAAUSDT", 0.0001m, 0m, FundingRateReader.NormalizeEpochMs(fundTs), 8),
            };

            var rec = new MultiTickRecordingReceiver();
            var tuples = new List<(string, MockPerpExecutor, TextReader, TextReader, IReadOnlyList<FundingEvent>)>
            {
                ("AAAUSDT", execA, new StringReader(ka.ToString()), new StringReader(ma.ToString()), fundA),
                ("BBBUSDT", execB, new StringReader(kb.ToString()), new StringReader(mb.ToString()), new List<FundingEvent>()),
            };
            var feeder = MultiSymbolKlineFeeder.ForTextReaders(rec, tuples);
            await feeder.PlayAsync();
            return rec.Ticks;
        }

        // ============ 4. Missing-bar skip + staleness ============
        [Fact]
        public async Task MissingBar_StaleLegCarriesOwnLastKnown_NotForwardFilled_CloseIndexMinusOne()
        {
            long b = 1_700_000_000_000L;
            // Leg A has 3 bars. Leg B is MISSING bar 1 (only bars 0 and 2). At bar 1's T,
            // B is stale: carries its OWN bar-0 quote/mark (200), never A's value.
            var execA = new MockPerpExecutor(Cfg());
            var execB = new MockPerpExecutor(Cfg());

            var a = FlatLeg("AAAUSDT", b, new[] { 100m, 101m, 102m }, execA);

            // Leg B: bar 0 @ open b (close 200), bar 2 @ open b+2h (close 202). Bar 1 absent.
            var kb = new StringBuilder(KlineHeader); var mb = new StringBuilder(KlineHeader);
            long ot0 = b, ct0 = b + HourMs - 1;
            long ot2 = b + 2 * HourMs, ct2 = ot2 + HourMs - 1;
            kb.Append(KlineRow(ot0, 200m, 200m, 200m, 200m, ct0));
            mb.Append(MarkRow(ot0, 200m, 200m, 200m, 200m, ct0));
            kb.Append(KlineRow(ot2, 202m, 202m, 202m, 202m, ct2));
            mb.Append(MarkRow(ot2, 202m, 202m, 202m, 202m, ct2));
            var bb = new LegFixture { Symbol = "BBBUSDT", Klines = kb.ToString(), Mark = mb.ToString(), Executor = execB };

            // Open a short on B at bar 0 so an open position keeps marking on stale ticks.
            await execB.PlaceLimitAsync("BBBUSDT", 1m, 200m, isBuy: false); // fills bar 0 (bid>=200)

            var (feeder, rec) = BuildFeeder(new[] { a, bb });
            await feeder.PlayAsync();

            // Three merged Ts: A closes at all three; B closes only at bar 0 and bar 2.
            Assert.Equal(3, rec.Ticks.Count);

            // Bar 1 (index 1): B is stale.
            var t1 = rec.Ticks[1];
            Assert.False(t1.Legs[0].IsStale);    // A fresh
            Assert.True(t1.Legs[1].IsStale);     // B stale
            Assert.Equal(-1, t1.CloseLegIndex);  // incomplete close set

            // B's stale value is its OWN last-known (200), NOT contaminated by A (101).
            Assert.Equal(200m, t1.Legs[1].FillQuote.Bid);
            Assert.Equal(200m, t1.Legs[1].Mark.Close);
            Assert.NotEqual(t1.Legs[0].FillQuote.Bid, t1.Legs[1].FillQuote.Bid);

            // B's executor still got ProcessTick on the stale mark (position still open,
            // could liquidate) — its position remains short 1 (no new fills).
            Assert.Equal(-1m, execB.NetQty);

            // Bars 0 and 2 are complete -> closeLegIndex 0.
            Assert.Equal(0, rec.Ticks[0].CloseLegIndex);
            Assert.Equal(0, rec.Ticks[2].CloseLegIndex);
            // Bar 2: B fresh again with its real bar-2 value (202), not forward-filled.
            Assert.False(rec.Ticks[2].Legs[1].IsStale);
            Assert.Equal(202m, rec.Ticks[2].Legs[1].FillQuote.Bid);
        }

        // ============ 5. Two legs at identical timestamps deterministic; run twice ============
        [Fact]
        public async Task IdenticalTimestamps_OneMergedTick_ProcessTickOrderIsIndexOrder_DeterministicTwice()
        {
            var run1 = await RunIdenticalTimestampFixture();
            var run2 = await RunIdenticalTimestampFixture();

            Assert.Equal(run1.recTicks.Count, run2.recTicks.Count);
            for (int i = 0; i < run1.recTicks.Count; i++)
            {
                Assert.Equal(run1.recTicks[i].CloseLegIndex, run2.recTicks[i].CloseLegIndex);
                for (int j = 0; j < run1.recTicks[i].Legs.Length; j++)
                    Assert.Equal(run1.recTicks[i].Legs[j].FillQuote.Bid, run2.recTicks[i].Legs[j].FillQuote.Bid);
            }
            // Byte-identical executor state across the two runs.
            Assert.Equal(run1.aNet, run2.aNet);
            Assert.Equal(run1.bNet, run2.bNet);
            Assert.Equal(run1.aWallet, run2.aWallet);
            Assert.Equal(run1.bWallet, run2.bWallet);
        }

        private static async Task<(List<TickRecord> recTicks, decimal aNet, decimal bNet, decimal aWallet, decimal bWallet)>
            RunIdenticalTimestampFixture()
        {
            long b = 1_700_000_000_000L;
            var execA = new MockPerpExecutor(Cfg());
            var execB = new MockPerpExecutor(Cfg());
            await execA.PlaceLimitAsync("AAAUSDT", 5m, 100m, isBuy: true);
            await execB.PlaceLimitAsync("BBBUSDT", 5m, 100m, isBuy: false);

            var a = FlatLeg("AAAUSDT", b, new[] { 100m, 100m }, execA);
            var bb = FlatLeg("BBBUSDT", b, new[] { 100m, 100m }, execB);
            var (feeder, rec) = BuildFeeder(new[] { a, bb });
            await feeder.PlayAsync();

            foreach (var t in rec.Ticks)
                Assert.Equal(0, t.CloseLegIndex); // single merged tick, all fresh

            return (rec.Ticks, execA.NetQty, execB.NetQty, execA.Wallet, execB.Wallet);
        }

        // ============ 6. Spread signal only on close tag; tick count == bar count ============
        [Fact]
        public async Task CloseLegIndex_SetOnBarCloseEmissions_TickCountEqualsBarCount_No4xSynthetic()
        {
            long b = 1_700_000_000_000L;
            // 5 aligned bars each with a real intra-bar wick (H/L != C) — the FULL mark
            // bar must be passed (so the executor probe sees the wick) but only ONE tick
            // per bar is emitted (NOT 4 synthetic ticks).
            var ka = new StringBuilder(KlineHeader); var ma = new StringBuilder(KlineHeader);
            var kb = new StringBuilder(KlineHeader); var mb = new StringBuilder(KlineHeader);
            for (int i = 0; i < 5; i++)
            {
                long ot = b + i * HourMs, ct = ot + HourMs - 1;
                decimal c = 100m + i;
                ka.Append(KlineRow(ot, c, c + 5m, c - 5m, c, ct)); // wick H=c+5, L=c-5
                ma.Append(MarkRow(ot, c, c + 7m, c - 7m, c, ct));  // mark wick wider
                decimal c2 = 200m + i;
                kb.Append(KlineRow(ot, c2, c2 + 5m, c2 - 5m, c2, ct));
                mb.Append(MarkRow(ot, c2, c2 + 7m, c2 - 7m, c2, ct));
            }
            var a = new LegFixture { Symbol = "AAAUSDT", Klines = ka.ToString(), Mark = ma.ToString(), Executor = new MockPerpExecutor(Cfg()) };
            var bb = new LegFixture { Symbol = "BBBUSDT", Klines = kb.ToString(), Mark = mb.ToString(), Executor = new MockPerpExecutor(Cfg()) };
            var (feeder, rec) = BuildFeeder(new[] { a, bb });
            await feeder.PlayAsync();

            // Tick count == bar count (5), NOT 4× (=20).
            Assert.Equal(5, rec.Ticks.Count);

            foreach (var t in rec.Ticks)
            {
                // Aligned -> every emission is a complete bar-close -> closeLegIndex 0.
                Assert.Equal(0, t.CloseLegIndex);
                // The FULL mark bar (O/H/L/C) is passed so the probe sees the wick.
                var mark = t.Legs[0].Mark;
                Assert.True(mark.High > mark.Close); // wick preserved
                Assert.True(mark.Low < mark.Close);
                // FillQuote is the real CLOSE only (no synthetic O/H/L fill price).
                Assert.Equal(mark.Close, t.Legs[0].FillQuote.Bid);
            }
        }

        // ============ 7. Determinism golden-run: funding + liquidation + missing bar, twice ============
        [Fact]
        public async Task GoldenRun_FundingLiquidationMissingBar_ReplayedTwice_ByteIdentical()
        {
            var r1 = await RunGoldenFixture();
            var r2 = await RunGoldenFixture();

            // Recorded leg stream identical.
            Assert.Equal(r1.ticks.Count, r2.ticks.Count);
            for (int i = 0; i < r1.ticks.Count; i++)
            {
                Assert.Equal(r1.ticks[i].CloseLegIndex, r2.ticks[i].CloseLegIndex);
                for (int j = 0; j < r1.ticks[i].Legs.Length; j++)
                {
                    Assert.Equal(r1.ticks[i].Legs[j].IsStale, r2.ticks[i].Legs[j].IsStale);
                    Assert.Equal(r1.ticks[i].Legs[j].FillQuote.Bid, r2.ticks[i].Legs[j].FillQuote.Bid);
                    Assert.Equal(r1.ticks[i].Legs[j].Mark.Close, r2.ticks[i].Legs[j].Mark.Close);
                }
            }

            // Executor state byte-identical (integer/decimal path).
            Assert.Equal(r1.aWallet, r2.aWallet);
            Assert.Equal(r1.aNet, r2.aNet);
            Assert.Equal(r1.aRealized, r2.aRealized);
            Assert.Equal(r1.aFunding, r2.aFunding);
            Assert.Equal(r1.aFees, r2.aFees);
            Assert.Equal(r1.aLiq, r2.aLiq);
            Assert.Equal(r1.bWallet, r2.bWallet);
            Assert.Equal(r1.bNet, r2.bNet);
            Assert.Equal(r1.bFunding, r2.bFunding);

            // The scenario actually exercised a liquidation (leg A short blown up).
            Assert.True(r1.aLiq);
        }

        private static async Task<(List<TickRecord> ticks,
            decimal aWallet, decimal aNet, decimal aRealized, decimal aFunding, decimal aFees, bool aLiq,
            decimal bWallet, decimal bNet, decimal bFunding)> RunGoldenFixture()
        {
            long b = 1_700_000_000_000L;
            // Leg A: short 1 @ 100, then bar 1 spikes adversely (mark high 200) -> liq.
            // High leverage + high maint to force the liquidation.
            var execA = new MockPerpExecutor(Cfg(leverage: 10m, maintRatio: 0.10m, wallet: 1_000m));
            var execB = new MockPerpExecutor(Cfg(wallet: 1_000m));
            await execA.PlaceLimitAsync("AAAUSDT", 1m, 100m, isBuy: false); // short fills bar 0 (bid>=100)
            await execB.PlaceLimitAsync("BBBUSDT", 1m, 200m, isBuy: true);  // long fills bar 0 (ask<=200)

            // Leg A: 3 bars. Bar 1 has adverse mark high 200 (short pain).
            var ka = new StringBuilder(KlineHeader); var ma = new StringBuilder(KlineHeader);
            long o0 = b, c0 = b + HourMs - 1;
            long o1 = b + HourMs, c1 = o1 + HourMs - 1;
            long o2 = b + 2 * HourMs, c2 = o2 + HourMs - 1;
            ka.Append(KlineRow(o0, 100m, 100m, 100m, 100m, c0));
            ma.Append(MarkRow(o0, 100m, 100m, 100m, 100m, c0));
            ka.Append(KlineRow(o1, 100m, 200m, 100m, 150m, c1)); // last-trade close 150
            ma.Append(MarkRow(o1, 100m, 200m, 100m, 150m, c1));  // mark high 200 -> liq probe
            ka.Append(KlineRow(o2, 150m, 150m, 150m, 150m, c2));
            ma.Append(MarkRow(o2, 150m, 150m, 150m, 150m, c2));

            // Leg B: MISSING bar 1 (bars 0 and 2 only) -> stale at bar 1.
            var kb = new StringBuilder(KlineHeader); var mb = new StringBuilder(KlineHeader);
            kb.Append(KlineRow(o0, 200m, 200m, 200m, 200m, c0));
            mb.Append(MarkRow(o0, 200m, 200m, 200m, 200m, c0));
            kb.Append(KlineRow(o2, 205m, 205m, 205m, 205m, c2));
            mb.Append(MarkRow(o2, 205m, 205m, 205m, 205m, c2));

            // Funding for A at BAR-1 close: the short is already open (from bar 0) and
            // funding runs BEFORE that tick's ProcessTick/liquidation, so it actually
            // settles and feeds the liquidation decision (proves funding-before-mark).
            var fundA = new List<FundingEvent>
            {
                new FundingEvent("AAAUSDT", 0.0002m, 0m, c1, 8),
            };

            var rec = new MultiTickRecordingReceiver();
            var tuples = new List<(string, MockPerpExecutor, TextReader, TextReader, IReadOnlyList<FundingEvent>)>
            {
                ("AAAUSDT", execA, new StringReader(ka.ToString()), new StringReader(ma.ToString()), fundA),
                ("BBBUSDT", execB, new StringReader(kb.ToString()), new StringReader(mb.ToString()), new List<FundingEvent>()),
            };
            var feeder = MultiSymbolKlineFeeder.ForTextReaders(rec, tuples);
            await feeder.PlayAsync();

            return (rec.Ticks,
                execA.Wallet, execA.NetQty, execA.RealizedPricePnl, execA.CumFunding, execA.CumFeesQuote, execA.WasLiquidated,
                execB.Wallet, execB.NetQty, execB.CumFunding);
        }

        // ============ 8. No per-tick allocation: same IReadOnlyList instance across ticks ============
        [Fact]
        public async Task NoPerTickAllocation_SameLegListInstanceAcrossConsecutiveTicks()
        {
            long b = 1_700_000_000_000L;
            var a = FlatLeg("AAAUSDT", b, new[] { 100m, 101m, 102m, 103m }, new MockPerpExecutor(Cfg()));
            var c = FlatLeg("BBBUSDT", b, new[] { 200m, 201m, 202m, 203m }, new MockPerpExecutor(Cfg()));
            var (feeder, rec) = BuildFeeder(new[] { a, c });
            await feeder.PlayAsync();

            Assert.True(rec.LegListRefs.Count >= 2);
            for (int i = 1; i < rec.LegListRefs.Count; i++)
                Assert.True(ReferenceEquals(rec.LegListRefs[0], rec.LegListRefs[i]),
                    "feeder must hand back the SAME reused leg-list instance every tick");
        }

        // ============ 9. Kline present but MARK absent => leg stale for the spread ============
        [Fact]
        public async Task KlinePresentMarkAbsent_LegStaleForSpread_PositionStillMarks_CloseIndexMinusOne()
        {
            long b = 1_700_000_000_000L;
            var execA = new MockPerpExecutor(Cfg());
            var execB = new MockPerpExecutor(Cfg());

            var a = FlatLeg("AAAUSDT", b, new[] { 100m, 101m, 102m }, execA);

            // Leg B: 3 KLINE bars (200,201,202) but the MARK bar for bar 1 is ABSENT
            // (mark has only bars 0 and 2). At bar 1 B's kline is fresh, its mark is not.
            long o0 = b, c0 = b + HourMs - 1;
            long o1 = b + HourMs, c1 = o1 + HourMs - 1;
            long o2 = b + 2 * HourMs, c2 = o2 + HourMs - 1;
            var kb = new StringBuilder(KlineHeader);
            kb.Append(KlineRow(o0, 200m, 200m, 200m, 200m, c0));
            kb.Append(KlineRow(o1, 201m, 201m, 201m, 201m, c1));
            kb.Append(KlineRow(o2, 202m, 202m, 202m, 202m, c2));
            var mb = new StringBuilder(KlineHeader);
            mb.Append(MarkRow(o0, 200m, 200m, 200m, 200m, c0));
            // bar-1 mark ABSENT
            mb.Append(MarkRow(o2, 202m, 202m, 202m, 202m, c2));
            var bb = new LegFixture { Symbol = "BBBUSDT", Klines = kb.ToString(), Mark = mb.ToString(), Executor = execB };

            // Open a short on B at bar 0 so an open position keeps marking through the stale bar.
            await execB.PlaceLimitAsync("BBBUSDT", 1m, 200m, isBuy: false); // fills bar 0 (bid>=200)

            var (feeder, rec) = BuildFeeder(new[] { a, bb });
            await feeder.PlayAsync();

            Assert.Equal(3, rec.Ticks.Count);

            // Bar 1: B's kline is fresh but its MARK is absent -> the leg is STALE so the
            // strategy excludes it from the spread; closeLegIndex == -1 (no forward-filled
            // mark in the signal).
            var t1 = rec.Ticks[1];
            Assert.False(t1.Legs[0].IsStale);   // A fully fresh
            Assert.True(t1.Legs[1].IsStale);    // B mark-absent -> stale
            Assert.Equal(-1, t1.CloseLegIndex);

            // The carried mark is B's OWN last-known (bar-0 close 200) — NOT fabricated
            // from the present kline (201) and NOT forward-filled from A.
            Assert.Equal(200m, t1.Legs[1].Mark.Close);

            // The position still marks: B's executor got ProcessTick and stays short 1.
            Assert.Equal(-1m, execB.NetQty);

            // Bars 0 and 2 are fully fresh (kline + mark aligned) -> closeLegIndex 0.
            Assert.Equal(0, rec.Ticks[0].CloseLegIndex);
            Assert.False(rec.Ticks[2].Legs[1].IsStale);
            Assert.Equal(0, rec.Ticks[2].CloseLegIndex);
        }

        // ============ 10. dueFunding reports ONLY actually-settled events ============
        [Fact]
        public async Task DueFunding_OpeningBarNoOpNotReported_OpenPositionReported()
        {
            long b = 1_700_000_000_000L;
            var execA = new MockPerpExecutor(Cfg());
            var execB = new MockPerpExecutor(Cfg());

            // A opens a long via a resting limit that fills on bar 0 — inside ProcessTick,
            // i.e. AFTER funding (which runs first). So funding due on bar 0 sees a flat
            // position and no-ops; it must NOT be surfaced as applied.
            await execA.PlaceLimitAsync("AAAUSDT", 10m, 100m, isBuy: true);

            var a = FlatLeg("AAAUSDT", b, new[] { 100m, 100m }, execA);
            var bb = FlatLeg("BBBUSDT", b, new[] { 200m, 200m }, execB);

            long c0 = b + HourMs - 1;       // bar-0 close: position OPENS here (after funding)
            long c1 = b + 2 * HourMs - 1;   // bar-1 close: position already open
            a.Funding.Add(new FundingEvent("AAAUSDT", 0.0001m, 0m, c0, 8)); // no-op (opening bar)
            a.Funding.Add(new FundingEvent("AAAUSDT", 0.0001m, 0m, c1, 8)); // settles (open)

            var (feeder, rec) = BuildFeeder(new[] { a, bb });
            await feeder.PlayAsync();

            Assert.Equal(2, rec.Ticks.Count);

            // Bar 0: funding was DUE but no-op'd (position not open yet) -> NOT surfaced.
            Assert.Empty(rec.Ticks[0].Funding);
            // Bar 1: position open -> funding settles and IS surfaced exactly once.
            Assert.Single(rec.Ticks[1].Funding);

            // Exactly ONE funding settled (bar 1): -10 x 100 x 0.0001 = -0.1. The bar-0
            // event contributed nothing — proving it was never applied.
            Assert.Equal(-0.1m, execA.CumFunding);
        }
    }
}
