---
name: backtest-engine
description: Rules for the Trading.Backtest library (mock executor + historical feeders + trend manager). Use when adding/changing a feeder, the mock matching engine, trend simulation, or anything that must keep backtests deterministic and aligned with live semantics.
---

# Trading.Backtest — Engine Rules

The in-memory backtest engine must produce results a real run would, and produce them identically every time. It is strategy-agnostic: feeders drive `IBacktestTickReceiver`, never a concrete strategy.

## Parity with live

The only differences between backtest and live should be the injected `IOrderExecutor` (`MockBinanceExecutor` vs `BinanceConnectorExecutor`) and the feed (`IBacktestFeeder` vs `Connection`). Same strategy code, same `IOrderExecutor` contract. Don't add backtest-only branches into shared logic.

## Determinism (non-negotiable)

- No wall-clock, no `Task.Delay`, no `Math.Random`, no `DateTime.UtcNow` in the replay path. Time comes from the feed's timestamps.
- Given the same CSVs + settings, final PnL and trade count must be byte-identical across runs. When you change anything in this lib, re-run a known config and diff the summary against a golden run.

## Tick ordering inside a bar/tick

`MockBinanceExecutor.ProcessTick(...)` runs **before** `IBacktestTickReceiver.OnBacktestTickAsync(...)` for the same tick, so a resting limit order that fills on this tick is visible when the strategy queries the executor in the same tick. **Never reorder this** — it silently changes fills.

## Mock executor realism

`MockBinanceExecutor` must simulate what the real exchange does, or backtests over-state PnL:
- **Filters:** when `exchangeFilters` (a `TradingPair`) is set, floor price to tick / qty to step and reject sub-`MIN_NOTIONAL` orders — exactly like live.
- **Fees:** deduct the per-side commission; mirror **base-asset commission netting** on buys (credited qty reduced by commission) so sells use a real quantity.
- **Slippage:** apply one-sided market slippage (`BacktestSlippagePct`); limit orders fill at the exact resting price. Default to a non-zero slippage for honest tuning — zero slippage overstates PnL on liquid majors by ~5–20 % and hides the slippage cliff.
- Track a finite quote balance; reject buys it can't fund.

## Epoch normalization (a real bug, keep it fixed)

Binance archives switched OpenTime/transactTime from **milliseconds to microseconds** during 2025. Every place that parses an epoch normalizes `value > 10^13 ? value / 1000 : value`:
- `HistoricalCsvFeeder` (transact_time), `HistoricalKlineFeeder` (open/close time), AND `HistoricalTrendManager` (candle OpenTime, in `BuildFromCandles` + defensively in `GetTrend`).
- These MUST stay in lockstep. If the trend manager isn't normalized but the feeder is, a backtest crossing the ms→µs boundary resolves every later tick to the last pre-boundary candle — the trend/volume gate freezes on stale data and the bot silently stops trading for the rest of the run. (This corrupted months of results before it was found.)

## Trend simulation

`HistoricalTrendManager` precomputes SMA + volume-SMA per candle with **no lookahead** (the snapshot for candle i uses only candles that closed before i opened) and de-duplicates overlapping OpenTimes. Keep both rules — peeking at candle i's own close inflates results; double-counting a duplicated candle skews the SMA.

## Memory / large data

Feeders stream CSVs (line-by-line, span parsing) — do not buffer months of ticks into a `List<T>`. aggTrades files run to multiple GB; the feeder must stay constant-memory.

## When adding a feeder

Implement `IBacktestFeeder` (with `DailyBoundaryCrossed` for equity sampling), normalize epochs, drive `IBacktestTickReceiver` after `executor.ProcessTick`, and validate the data schema up front with a clear error (the classic foot-gun is pointing the kline feeder at an aggTrades folder).
