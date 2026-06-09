using GripTrader.Core.Abstractions;
using GripTrader.Core.Models;
using GripTrader.Core.Settings;
using Log;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace GripTrader.Core.Backtest
{
    /// <summary>
    /// Merges N per-symbol kline + mark-price + funding streams into ONE
    /// timestamp-ordered tick stream and drives an
    /// <see cref="IBacktestMultiTickReceiver"/> plus one
    /// <see cref="MockPerpExecutor"/> per leg. This is the multi-asset sibling of
    /// <see cref="HistoricalKlineFeeder"/> (single-symbol); it implements the existing
    /// <see cref="IBacktestFeeder"/> surface additively (a new <see cref="PlayAsync"/>
    /// the legacy <see cref="PlayHistoricalDataAsync"/> delegates to) so the tuner's
    /// progress/equity-sampling hooks keep working.
    ///
    /// <para><b>One tick per bar at close (no synthetic intra-bar ticks).</b> The
    /// single-symbol feeder fabricates four guessed O/H/L/C ticks per bar; merging two
    /// legs' independently fabricated paths would manufacture spread/liquidation events
    /// that never occurred. So this feeder emits exactly one tick per (symbol, bar) at
    /// the bar's normalized <b>close_time</b>, passing the FULL mark bar (O/H/L/C) so the
    /// executor's adverse-extreme liquidation probe still sees the intra-bar wick via
    /// <c>mark.High</c>/<c>mark.Low</c> — while the strategy's spread signal sees only
    /// the real close. Tick count == bar count.</para>
    ///
    /// <para><b>Single epoch site.</b> Every kline open_time, mark open_time, and
    /// funding calc_time passes through <see cref="NormalizeEpochMs"/>
    /// (<c>&gt;10^13 ? /1000</c>, the same constant as
    /// <see cref="HistoricalKlineFeeder"/>) at parse time, before anything enters the
    /// merge. The executor does no epoch math.</para>
    ///
    /// <para><b>Total deterministic order at each merged T</b> (single-threaded, no
    /// wall-clock/RNG/<c>Task.Delay</c>): (1) apply every due <see cref="FundingEvent"/>
    /// (symbol-ascending, then ts-ascending) via <c>ApplyFunding</c> so the wallet move
    /// feeds this tick's liquidation; (2) each leg's <c>ProcessTick</c> in fixed leg
    /// order 0..N-1 (matches resting limits + runs its own isolated liquidation probe);
    /// (3) advisory cross aggregation (only under <see cref="MarginMode.Cross"/>: ΣEquity
    /// &lt; ΣMaintenance — NO core force-close); (4) the receiver's
    /// <c>OnBacktestTickAsync</c> exactly once.</para>
    ///
    /// <para><b>Missing-bar policy</b> (skip-spread-but-mark-positions): when a leg has
    /// no fresh bar at T it is still included with its LAST-KNOWN quote/mark and
    /// <c>IsStale=true</c>; its <c>ProcessTick</c> still runs (open positions keep
    /// marking/liquidating; no new limit fills) and <c>closeLegIndex</c> is forced to
    /// <c>-1</c> when the close set is incomplete. A missing side is NEVER forward-filled
    /// into the spread and NEVER fabricated from the present side.</para>
    ///
    /// <para><b>Constant memory / hot path:</b> each file is streamed with a
    /// sequential-scan <see cref="FileStream"/> + <see cref="StreamReader"/> +
    /// <see cref="ReadOnlySpan{T}"/> span-parsing; no <c>ReadAllLines</c>/<c>Split</c>/
    /// LINQ in the merge loop. The <see cref="MultiTickLeg"/>[] and the due-funding list
    /// are reused (do-not-retain) buffers — no per-tick dictionary allocation.</para>
    /// </summary>
    public sealed class MultiSymbolKlineFeeder : IBacktestFeeder
    {
        /// <summary>
        /// The three sorted file lists for one leg: klines, mark-price klines, and
        /// funding-rate archives. Fixed leg order is the order these are supplied to the
        /// feeder ctor (index 0..N-1).
        /// </summary>
        public sealed record LegSource(
            string Symbol,
            IReadOnlyList<string> KlineFiles,
            IReadOnlyList<string> MarkPriceFiles,
            IReadOnlyList<string> FundingRateFiles);

        // Same epoch constant/direction as HistoricalKlineFeeder (~line 215).
        private const long EpochMicrosThreshold = 10_000_000_000_000L;

        internal static long NormalizeEpochMs(long t) => t > EpochMicrosThreshold ? t / 1000L : t;

        private readonly IBacktestMultiTickReceiver _receiver;
        private readonly LegCursor[] _cursors;
        private readonly MockPerpExecutor[] _executors;
        private readonly string[] _symbols;
        private readonly MarginMode _accountMode;
        private readonly int _legCount;

        // Reused, do-not-retain buffers (hot-path: no per-tick allocation).
        private readonly MultiTickLeg[] _legBuffer;
        private readonly List<FundingEvent> _dueFunding = new();

        private long _lastSampledDay = -1L;

        public event Action<long>? DailyBoundaryCrossed;

        public int TotalTicksProcessed { get; private set; }
        public string? CurrentFileName { get; private set; }
        public int CurrentFileIndex { get; private set; }
        public int TotalFiles { get; private set; }

        /// <summary>
        /// File-backed ctor: streams each leg's klines/mark/funding from the sorted file
        /// lists in <paramref name="legs"/>. Fixed leg order = list order = the
        /// <see cref="MultiTickLeg"/>[] order = the meaning of <c>closeLegIndex</c>.
        /// </summary>
        public MultiSymbolKlineFeeder(
            IBacktestMultiTickReceiver receiver,
            IReadOnlyList<(LegSource source, MockPerpExecutor executor)> legs,
            MarginMode accountMode = MarginMode.Isolated)
            : this(receiver, BuildFileCursors(legs), Extract(legs), accountMode)
        {
        }

        // Private shared ctor: cursors + executors already built (file or in-memory).
        private MultiSymbolKlineFeeder(
            IBacktestMultiTickReceiver receiver,
            LegCursor[] cursors,
            (string symbol, MockPerpExecutor executor)[] legs,
            MarginMode accountMode)
        {
            _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
            if (cursors is null || legs is null || cursors.Length == 0 || cursors.Length != legs.Length)
                throw new ArgumentException("At least one leg is required and cursors must match legs.");

            _cursors = cursors;
            _legCount = cursors.Length;
            _executors = new MockPerpExecutor[_legCount];
            _symbols = new string[_legCount];
            for (int i = 0; i < _legCount; i++)
            {
                _executors[i] = legs[i].executor ?? throw new ArgumentNullException(nameof(legs));
                _symbols[i] = legs[i].symbol ?? throw new ArgumentNullException(nameof(legs));
            }

            _accountMode = accountMode;
            _legBuffer = new MultiTickLeg[_legCount];
        }

        private static (string, MockPerpExecutor)[] Extract(
            IReadOnlyList<(LegSource source, MockPerpExecutor executor)> legs)
        {
            if (legs is null || legs.Count == 0)
                throw new ArgumentException("At least one leg is required.", nameof(legs));
            var arr = new (string, MockPerpExecutor)[legs.Count];
            for (int i = 0; i < legs.Count; i++)
                arr[i] = (legs[i].source.Symbol, legs[i].executor);
            return arr;
        }

        private static LegCursor[] BuildFileCursors(
            IReadOnlyList<(LegSource source, MockPerpExecutor executor)> legs)
        {
            if (legs is null || legs.Count == 0)
                throw new ArgumentException("At least one leg is required.", nameof(legs));
            var cursors = new LegCursor[legs.Count];
            for (int i = 0; i < legs.Count; i++)
            {
                var s = legs[i].source;
                var funding = FundingRateReader.ReadFundingRates(s.FundingRateFiles, s.Symbol);
                cursors[i] = new LegCursor(
                    s.Symbol,
                    new FileLineSource(s.KlineFiles),
                    new FileLineSource(s.MarkPriceFiles),
                    funding);
            }
            return cursors;
        }

        /// <summary>
        /// In-memory ctor for tests: each leg supplies kline + mark <see cref="TextReader"/>s
        /// and a pre-parsed funding list. Same merge/ordering logic as the file path.
        /// </summary>
        internal static MultiSymbolKlineFeeder ForTextReaders(
            IBacktestMultiTickReceiver receiver,
            IReadOnlyList<(string symbol, MockPerpExecutor executor, TextReader klines, TextReader mark, IReadOnlyList<FundingEvent> funding)> legs,
            MarginMode accountMode = MarginMode.Isolated)
        {
            var cursors = new LegCursor[legs.Count];
            var pairs = new (string, MockPerpExecutor)[legs.Count];
            for (int i = 0; i < legs.Count; i++)
            {
                var l = legs[i];
                cursors[i] = new LegCursor(l.symbol, new ReaderLineSource(l.klines), new ReaderLineSource(l.mark), l.funding);
                pairs[i] = (l.symbol, l.executor);
            }
            return new MultiSymbolKlineFeeder(receiver, cursors, pairs, accountMode);
        }

        /// <summary>
        /// Resolve a data root + symbol + interval into the three sorted file lists for
        /// a leg. Klines: <c>klines/{SYM}/{interval}/{SYM}-{interval}-YYYY-MM.csv</c>;
        /// mark: <c>markPriceKlines/{SYM}/{interval}/...</c>; funding (monthly-only, no
        /// interval segment): <c>fundingRate/{SYM}/{SYM}-fundingRate-YYYY-MM.csv</c>.
        /// </summary>
        public static LegSource CollectLegFiles(string root, string symbol, string interval)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentException("root required", nameof(root));
            var sym = symbol.ToUpperInvariant();

            var klineDir = Path.Combine(root, "klines", sym, interval);
            var markDir = Path.Combine(root, "markPriceKlines", sym, interval);
            var fundingDir = Path.Combine(root, "fundingRate", sym);

            var klineFiles = HistoricalKlineFeeder.CollectSortedKlineFiles(klineDir, sym);
            var markFiles = HistoricalKlineFeeder.CollectSortedKlineFiles(markDir, sym);
            var fundingFiles = FundingRateReader.CollectSortedFundingFiles(fundingDir, sym);

            return new LegSource(sym, klineFiles, markFiles, fundingFiles);
        }

        /// <summary>
        /// <see cref="IBacktestFeeder"/> shim: the N legs × 3 streams come from the ctor,
        /// so the supplied <paramref name="filePaths"/> is ignored — this feeder is
        /// driven by <see cref="PlayAsync"/>, to which this delegates. Additive: the
        /// seam is not reshaped.
        /// </summary>
        public Task PlayHistoricalDataAsync(IReadOnlyList<string> filePaths) => PlayAsync();

        /// <summary>
        /// Replay the merged N-leg stream once. Streaming k-way merge over the leg
        /// cursors; the merge key is the normalized close_time of each leg's current
        /// bar. At each merged T the §3 deterministic order runs and the receiver is
        /// called exactly once.
        /// </summary>
        public async Task PlayAsync()
        {
            TotalTicksProcessed = 0;
            TotalFiles = 0;
            for (int i = 0; i < _legCount; i++)
                TotalFiles += _cursors[i].TotalFiles;
            CurrentFileIndex = 0;
            CurrentFileName = null;

            // Prime each cursor to its first bar.
            for (int i = 0; i < _legCount; i++)
                _cursors[i].Advance();

            while (true)
            {
                // Find the minimum normalized close_time across all non-exhausted legs.
                long minClose = long.MaxValue;
                bool any = false;
                for (int i = 0; i < _legCount; i++)
                {
                    var c = _cursors[i];
                    if (c.HasBar && c.CloseTime < minClose)
                    {
                        minClose = c.CloseTime;
                        any = true;
                    }
                    else if (c.HasBar)
                    {
                        any = true;
                    }
                }
                if (!any)
                    break; // all legs exhausted

                await EmitMergedTickAsync(minClose).ConfigureAwait(false);
            }

            Logger.LogInformation(
                $"[Backtest] Multi-symbol replay complete. Legs={_legCount} Ticks={TotalTicksProcessed}");
        }

        // Emit one merged tick for normalized close_time T. Builds the reused leg
        // buffer, applies due funding, runs ProcessTick in fixed order, computes the
        // advisory cross flag, and calls the receiver once. Advances every leg whose
        // current bar closes at T.
        private async Task EmitMergedTickAsync(long t)
        {
            // ---- Build the reused leg buffer + determine the close set ----------
            // A leg "participates" (fresh) at T iff its current bar closes at T. Any
            // other leg is stale (carries last-known quote/mark). closeLegIndex is the
            // anchor (leg 0 when fresh) only if the WHOLE close set is complete — i.e.
            // every leg is fresh at T. A market-neutral pair needs both legs to compute
            // the spread, so an incomplete close set ⇒ closeLegIndex = -1.
            int freshCount = 0;
            for (int i = 0; i < _legCount; i++)
            {
                var c = _cursors[i];
                bool barAtT = c.HasBar && c.CloseTime == t;
                if (barAtT && !c.CurrentMarkStale)
                {
                    // Fully fresh: a real kline AND a mark bar aligned by open_time.
                    freshCount++;
                    _legBuffer[i] = new MultiTickLeg(c.Symbol, c.FillQuote, c.Mark, isStale: false);
                }
                else if (barAtT)
                {
                    // Kline closed at T but its mark bar is ABSENT (forward-filled). The
                    // fill quote is real (so the executor still marks/fills on it), but the
                    // leg is STALE for the spread — the strategy must not use a forward-
                    // filled mark in the signal. Forces closeLegIndex = -1 below.
                    _legBuffer[i] = new MultiTickLeg(c.Symbol, c.FillQuote, c.Mark, isStale: true);
                }
                else
                {
                    // No fresh bar at all: last-known quote/mark; never forward-filled
                    // into the spread.
                    _legBuffer[i] = new MultiTickLeg(c.Symbol, c.LastFillQuote, c.LastMark, isStale: true);
                }
            }

            // closeLegIndex: anchor leg 0 only when EVERY leg is fresh (complete close
            // set). Otherwise -1 (incomplete/stale ⇒ strategy must not touch the spread).
            int closeLegIndex = freshCount == _legCount ? 0 : -1;

            // ---- (1) Apply all due funding (symbol-ascending, then ts-ascending) ----
            _dueFunding.Clear();
            CollectDueFunding(t);

            // Apply each due event; keep ONLY the ones that actually settled (moved a
            // wallet) in _dueFunding, so the receiver's dueFunding is an honest record of
            // applied funding. An event due on the bar that first opens a position no-ops
            // (funding runs before ProcessTick) and must NOT be reported as applied.
            int write = 0;
            for (int k = 0; k < _dueFunding.Count; k++)
            {
                var ev = _dueFunding[k];
                int legIdx = SymbolIndex(ev.Symbol);
                bool applied = legIdx >= 0
                    && _executors[legIdx].ApplyFunding(ev.Symbol, ev.MarkPrice, ev.Rate, ev.TimestampMs);
                if (applied)
                    _dueFunding[write++] = ev;
            }
            if (write < _dueFunding.Count)
                _dueFunding.RemoveRange(write, _dueFunding.Count - write);

            // ---- (2) ProcessTick in fixed leg order 0..N-1 ----------------------
            // Every leg — fresh or stale — gets ProcessTick so open positions keep
            // marking and liquidating (a stale leg fills nothing new on its old quote).
            for (int i = 0; i < _legCount; i++)
            {
                var leg = _legBuffer[i];
                _executors[i].ProcessTick(leg.Symbol, leg.FillQuote, leg.Mark);
            }

            // ---- (3) Advisory cross aggregation (no core force-close) -----------
            bool accountLiquidatable = false;
            if (_accountMode == MarginMode.Cross)
                accountLiquidatable = await ComputeAccountLiquidatableAsync().ConfigureAwait(false);

            // ---- (4) Receiver exactly once --------------------------------------
            await _receiver.OnBacktestTickAsync(_legBuffer, closeLegIndex, _dueFunding, accountLiquidatable)
                .ConfigureAwait(false);

            TotalTicksProcessed++;

            // Daily boundary for equity sampling (normalized ms).
            if (t > 0)
            {
                var day = t / 86_400_000L;
                if (day != _lastSampledDay)
                {
                    _lastSampledDay = day;
                    DailyBoundaryCrossed?.Invoke(t);
                }
            }

            // Advance every leg whose current bar closed at T (consumed this tick).
            for (int i = 0; i < _legCount; i++)
            {
                var c = _cursors[i];
                if (c.HasBar && c.CloseTime == t)
                {
                    CurrentFileName = c.CurrentFileName;
                    c.Advance();
                }
            }
        }

        // Collect every leg's funding events with calc_time <= T not yet applied, in
        // symbol-ascending then ts-ascending order, into the reused _dueFunding list.
        // The mark price carried on the event is taken from the leg's last-known mark
        // (funding settles on mark; the funding archive carries no price). Funding is
        // per-leg independent so the order can't change results — fixing it kills any
        // nondeterminism in the dueFunding listing.
        private void CollectDueFunding(long t)
        {
            // Stable order: sort by symbol index (legs already in a fixed order), and
            // within a leg the funding list is already ts-ascending from the archive.
            for (int i = 0; i < _legCount; i++)
            {
                var c = _cursors[i];
                while (c.HasFunding && c.NextFundingTs <= t)
                {
                    var raw = c.NextFunding;
                    // Fill the mark from the leg's best-available mark close at T (the
                    // price funding settles on): the CURRENT bar's mark when the leg is
                    // fresh here, else its last-known mark. If the leg has never had a
                    // mark, skip the event (cannot settle without a price).
                    if (!c.TryGetBestMarkClose(out var markClose))
                    {
                        c.AdvanceFunding();
                        continue;
                    }
                    var withMark = new FundingEvent(
                        raw.Symbol, raw.Rate, markClose, raw.TimestampMs, raw.IntervalHours);
                    _dueFunding.Add(withMark);
                    c.AdvanceFunding();
                }
            }

            // Re-sort by (symbol-ascending, ts-ascending). Symbol order is the canonical
            // determinism tiebreak the design fixes; within a symbol, ts-ascending.
            _dueFunding.Sort(static (a, b) =>
            {
                int s = string.CompareOrdinal(a.Symbol, b.Symbol);
                return s != 0 ? s : a.TimestampMs.CompareTo(b.TimestampMs);
            });
        }

        private async Task<bool> ComputeAccountLiquidatableAsync()
        {
            decimal totalEquity = 0m, totalMaint = 0m;
            for (int i = 0; i < _legCount; i++)
            {
                var leg = _legBuffer[i];
                var bd = await _executors[i].GetEquityBreakdownAsync(leg.Symbol, leg.Mark.Close).ConfigureAwait(false);
                totalEquity += bd.Equity;
                totalMaint += bd.MaintenanceMargin;
            }
            return totalEquity < totalMaint;
        }

        private int SymbolIndex(string symbol)
        {
            for (int i = 0; i < _legCount; i++)
                if (string.Equals(_symbols[i], symbol, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        // ====================================================================
        // Line sources — abstract over file streams (production) vs TextReaders
        // (in-memory test fixtures). Both stream line-by-line; neither buffers all.
        // ====================================================================
        private abstract class LineSource
        {
            public abstract string? CurrentFileName { get; }
            public abstract int FileCount { get; }
            public abstract string? ReadLine();
        }

        private sealed class FileLineSource : LineSource
        {
            private readonly IReadOnlyList<string> _files;
            private int _fileIdx = -1;
            private FileStream? _stream;
            private StreamReader? _reader;

            public FileLineSource(IReadOnlyList<string> files) => _files = files;

            public override string? CurrentFileName =>
                _fileIdx >= 0 && _fileIdx < _files.Count ? Path.GetFileName(_files[_fileIdx]) : null;

            public override int FileCount => _files.Count;

            public override string? ReadLine()
            {
                while (true)
                {
                    if (_reader == null)
                    {
                        _fileIdx++;
                        if (_fileIdx >= _files.Count)
                            return null;
                        _stream = new FileStream(
                            _files[_fileIdx], FileMode.Open, FileAccess.Read, FileShare.Read,
                            bufferSize: 1 << 20, FileOptions.SequentialScan);
                        _reader = new StreamReader(_stream);
                    }

                    var line = _reader.ReadLine();
                    if (line != null)
                        return line;

                    _reader.Dispose();
                    _stream?.Dispose();
                    _reader = null;
                    _stream = null;
                }
            }
        }

        private sealed class ReaderLineSource : LineSource
        {
            private readonly TextReader _reader;
            public ReaderLineSource(TextReader reader) => _reader = reader;
            public override string? CurrentFileName => "in-memory";
            public override int FileCount => 1;
            public override string? ReadLine() => _reader.ReadLine();
        }

        // ====================================================================
        // Per-leg streaming cursor: pairs each kline bar with its same-open_time mark
        // bar, exposes the current bar's normalized close_time as the merge key, and
        // carries the funding list with a cursor index. Holds last-known quote/mark for
        // the missing-bar (stale) policy.
        // ====================================================================
        private sealed class LegCursor
        {
            public readonly string Symbol;
            private readonly LineSource _klineSource;
            private readonly LineSource _markSource;
            private readonly IReadOnlyList<FundingEvent> _funding;
            private int _fundingIdx;

            // Current paired bar.
            public bool HasBar { get; private set; }
            public long CloseTime { get; private set; }
            public BidAsk FillQuote { get; private set; }
            public MarkBar Mark { get; private set; }
            // True when the current bar's mark was NOT aligned by open_time (mark absent)
            // and had to be forward-filled — the leg is stale for the spread even though its
            // kline (fill quote) is fresh.
            public bool CurrentMarkStale { get; private set; }

            // Last-known (for stale legs). Updated whenever a fresh bar is consumed.
            public BidAsk LastFillQuote { get; private set; }
            public MarkBar LastMark { get; private set; }
            public bool HaveLastMark { get; private set; }

            // One-bar lookahead buffer for the mark stream (mark may lead/lag kline by
            // open_time; we align by matching open_time).
            private bool _markBuffered;
            private long _markOpenTime;
            private MarkBar _markBar;

            public LegCursor(string symbol, LineSource klineSource, LineSource markSource,
                             IReadOnlyList<FundingEvent> funding)
            {
                Symbol = symbol;
                _klineSource = klineSource;
                _markSource = markSource;
                _funding = funding ?? Array.Empty<FundingEvent>();
            }

            public int TotalFiles => _klineSource.FileCount + _markSource.FileCount;
            public string? CurrentFileName => _klineSource.CurrentFileName;

            public bool HasFunding => _fundingIdx < _funding.Count;
            public long NextFundingTs => _funding[_fundingIdx].TimestampMs;
            public FundingEvent NextFunding => _funding[_fundingIdx];
            public void AdvanceFunding() => _fundingIdx++;

            // Best-available mark close at the current T: the current bar's mark when the
            // leg is fresh, else the last-known mark. False only if the leg has never had
            // any mark (funding cannot settle without a price).
            public bool TryGetBestMarkClose(out decimal markClose)
            {
                if (HasBar) { markClose = Mark.Close; return true; }
                if (HaveLastMark) { markClose = LastMark.Close; return true; }
                markClose = 0m;
                return false;
            }

            // Read the next kline bar; align it with the mark bar of the same open_time
            // (buffering one mark bar when the mark stream is ahead). Sets HasBar=false
            // when the kline stream is exhausted.
            public void Advance()
            {
                if (HasBar)
                {
                    // Promote current bar to last-known before consuming the next.
                    LastFillQuote = FillQuote;
                    LastMark = Mark;
                    HaveLastMark = true;
                }

                if (!ReadKline(out long openTime, out long closeTime,
                               out decimal open, out _, out _, out decimal close))
                {
                    HasBar = false;
                    return;
                }

                // Align the mark bar by open_time.
                MarkBar mark;
                if (TryGetMark(openTime, out var alignedMark))
                {
                    mark = alignedMark;
                    CurrentMarkStale = false;
                }
                else
                {
                    // No mark for this open_time (mark side missing for this bar):
                    // fall back to the last-known mark if we have one, else synthesize
                    // the mark from this bar's CLOSE only as a degenerate marker (no
                    // O/H/L wick — the missing side is never fabricated as a wick). This
                    // is the "kline present, mark absent" case: the open position still
                    // marks on this fallback mark, but the leg is flagged stale (below) so
                    // the strategy excludes it from the spread — no forward-filled mark
                    // ever enters the signal. Use last-known mark when available so the
                    // probe stays conservative; otherwise the close.
                    mark = HaveLastMark
                        ? LastMark
                        : new MarkBar(close, close, close, close, NormalizeEpochMs(closeTime));
                    CurrentMarkStale = true;
                }

                // Fill quote: kline close drives the last-trade quote (bid == ask,
                // no spread on kline data — slippage in the executor stands in).
                FillQuote = new BidAsk(close, close, NormalizeEpochMs(closeTime));
                Mark = mark;
                CloseTime = NormalizeEpochMs(closeTime);
                HasBar = true;
            }

            // Returns the mark bar whose open_time == targetOpenTime, advancing the mark
            // stream as needed; buffers a mark bar that is ahead of the kline.
            private bool TryGetMark(long targetOpenTime, out MarkBar mark)
            {
                // Drain any buffered/streamed mark bars up to targetOpenTime.
                while (true)
                {
                    if (!_markBuffered)
                    {
                        if (!ReadMark(out _markOpenTime, out _markBar))
                        {
                            mark = default;
                            return false; // mark stream exhausted
                        }
                        _markBuffered = true;
                    }

                    if (_markOpenTime == targetOpenTime)
                    {
                        mark = _markBar;
                        _markBuffered = false; // consume
                        return true;
                    }
                    if (_markOpenTime < targetOpenTime)
                    {
                        // Mark bar is behind the kline (a kline gap or extra mark row):
                        // drop it and read the next.
                        _markBuffered = false;
                        continue;
                    }
                    // Mark bar is ahead of the kline (mark missing for this open_time):
                    // keep it buffered for a later kline.
                    mark = default;
                    return false;
                }
            }

            // Parse one kline data row (cols 0=open_time,1=open,2=high,3=low,4=close,
            // 6=close_time; header skipped via first-char-is-digit). open_time is
            // normalized for alignment; close_time is normalized by the caller.
            private bool ReadKline(out long openTime, out long closeTime,
                                   out decimal open, out decimal high, out decimal low, out decimal close)
            {
                openTime = 0; closeTime = 0; open = 0m; high = 0m; low = 0m; close = 0m;
                string? line;
                while ((line = _klineSource.ReadLine()) != null)
                {
                    if (line.Length == 0 || !char.IsAsciiDigit(line[0]))
                        continue;

                    var span = line.AsSpan();
                    int col = 0, start = 0;
                    for (int i = 0; i <= span.Length; i++)
                    {
                        if (i == span.Length || span[i] == ',')
                        {
                            var field = span[start..i];
                            switch (col)
                            {
                                case 0: openTime = long.Parse(field, CultureInfo.InvariantCulture); break;
                                case 1: open = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                                case 2: high = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                                case 3: low = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                                case 4: close = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                                case 6: closeTime = long.Parse(field, CultureInfo.InvariantCulture); break;
                            }
                            col++;
                            start = i + 1;
                            if (col > 6) break;
                        }
                    }

                    openTime = NormalizeEpochMs(openTime);
                    closeTime = closeTime == 0 ? openTime + 1 : NormalizeEpochMs(closeTime);
                    if (closeTime <= openTime) closeTime = openTime + 1;
                    return true;
                }
                return false;
            }

            // Parse one mark-price row (same OHLC layout; volume=0 never read as real).
            // Returns the mark bar with its NORMALIZED open_time for alignment.
            private bool ReadMark(out long openTime, out MarkBar mark)
            {
                openTime = 0; mark = default;
                string? line;
                while ((line = _markSource.ReadLine()) != null)
                {
                    if (line.Length == 0 || !char.IsAsciiDigit(line[0]))
                        continue;

                    var span = line.AsSpan();
                    long rawOpen = 0, rawClose = 0;
                    decimal o = 0m, h = 0m, l = 0m, c = 0m;
                    int col = 0, start = 0;
                    for (int i = 0; i <= span.Length; i++)
                    {
                        if (i == span.Length || span[i] == ',')
                        {
                            var field = span[start..i];
                            switch (col)
                            {
                                case 0: rawOpen = long.Parse(field, CultureInfo.InvariantCulture); break;
                                case 1: o = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                                case 2: h = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                                case 3: l = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                                case 4: c = decimal.Parse(field, NumberStyles.Any, CultureInfo.InvariantCulture); break;
                                case 6: rawClose = long.Parse(field, CultureInfo.InvariantCulture); break;
                            }
                            col++;
                            start = i + 1;
                            if (col > 6) break;
                        }
                    }

                    openTime = NormalizeEpochMs(rawOpen);
                    long markTs = rawClose == 0 ? openTime : NormalizeEpochMs(rawClose);
                    mark = new MarkBar(o, h, l, c, markTs);
                    return true;
                }
                return false;
            }
        }
    }
}
