# AGENTS.md

This file provides guidance to Codex when working with code in this repository.

## Counterpart Guidance

This is the Codex counterpart to [CLAUDE.md](CLAUDE.md). Keep both files synchronized: when submodule guidance changes, update `AGENTS.md` and `CLAUDE.md` in the same change. If they ever disagree, read both, follow the stricter/current instruction, and reconcile the files before continuing.

## What this repo is

`trading-core` is a set of **strategy-agnostic, reusable trading libraries**. It was extracted from the GridBot app and is consumed as a **git submodule** by that app and by any other bot. It contains NO trading strategy — only the engine a strategy plugs into.

The cardinal rule: **nothing here may reference a concrete strategy** (no GridBot, GridConfig, ScoreboardData, GridState, etc.). If you find yourself reaching for a strategy type, you're in the wrong repo — that belongs in the consuming bot. Keep the dependency graph one-way.

## Libraries (one-way dependency DAG)

| Library (assembly) | Purpose | Depends on |
|---|---|---|
| `Trading.Abstractions` | contracts + value types: `IOrderExecutor`, `IPriceSink`, `IBacktestTickReceiver`, `IStateStore<T>`, `IQuoteSource`, `BidAsk`, `TradingPair`, `MathHelpers`, `Symbols`, `TradingSettingsBase` | **nothing** (BCL only) |
| `Trading.Exchange.Binance` | live Binance integration: `BinanceConnectorExecutor`, `ThrottledOrderExecutor`, `Connection` (combined-stream WS), `BinanceResponseParser`, `IBinanceTradeClient`, `QuoteSource`, `PublicService`, signing | Abstractions, Websockets, Log, Binance.Spot |
| `Trading.Backtest` | in-memory backtest engine: `MockBinanceExecutor`, `HistoricalCsvFeeder`, `HistoricalKlineFeeder`, `HistoricalTrendManager`, `IBacktestFeeder`, `NullStateStore<T>` | Abstractions, Log |
| `Trading.Tuning` | sweep/walk-forward/metrics/reports: `MetricsCalculator`, DTOs, `SweepConfig`, `SweepExpander`, `SweepAggregator`, `ParameterApplier` | Abstractions |
| `Log` / `Telegram` / `Websockets` | shared infra (Serilog file logger, Telegram notifier, WebSocket manager) | Log is leaf |

`Trading.Abstractions` is the hub: everything depends on it, it depends on nothing. The other libs never reference each other except through Abstractions.

## Build

```powershell
dotnet build trading-core.slnx
```

There are **no test projects in this repo yet** — the libraries are currently exercised by the consuming app's test suite (it gets `InternalsVisibleTo` grants, see below). If you add tests here, add per-lib `*.Tests` projects and grant them internals from the lib under test. Standalone-buildability (`trading-core.slnx` with no consuming app present) is the self-containment gate — keep it green.

## Seams a strategy plugs into

A consuming bot wires these — do not break their shapes:

- **`IOrderExecutor`** (`PlaceLimitAsync`/`PlaceMarketAsync`/`QueryOrderAsync`/`CancelOrderAsync`/`GetCurrentTotalEquityAsync`) — implemented by `BinanceConnectorExecutor` (live, wrapped by `ThrottledOrderExecutor`) and `MockBinanceExecutor` (backtest). Strategies call it; they never touch the concrete executor.
- **`IPriceSink`** (`OnPrice`) — the live price feed (`Connection`) fans out ticks to sinks; a strategy implements it.
- **`IBacktestTickReceiver`** (`OnBacktestTickAsync(BidAsk, bool? isUptrend)`) — the backtest seam. The feeders push one synchronous tick into whatever strategy is under test; they hold this interface, never a concrete strategy. `isUptrend` means "is the macro long-filter permissive?" — a strategy with no trend filter may ignore it.
- **`IStateStore<TState>`** — strategy state persistence, generic over the strategy's own state blob (`NullStateStore<T>` is the backtest no-op).
- **`TradingSettingsBase`** — transport/exchange/backtest settings (`Symbol`, `RecvWindowMs`, `CommissionRate`, `InitialBankroll`, all `Backtest*`); a bot derives its own settings from this so the Tuner's flat name-based `ParameterApplier` reflection binds them.

## Invariants worth preserving

- **Decimal-only math.** Every price, quantity, fee, percentage is `decimal`. Percentages are stored as decimal fractions (`0.001` = 0.1 %). Never `float`/`double`.
- **Single-threaded backtest tick path.** `IBacktestFeeder` drives `IBacktestTickReceiver.OnBacktestTickAsync` synchronously, executor-`ProcessTick` **before** the strategy tick (so limit fills are visible the same tick). Don't reorder or parallelize this — it changes backtest results.
- **Determinism.** Backtests must be 100 % reproducible: no wall-clock, no `Task.Delay`, no `Math.Random` in the replay path; time comes from the data feed timestamps.
- **Epoch normalization.** Binance kline/aggTrades archives switched from millisecond to **microsecond** epochs during 2025. Both feeders AND `HistoricalTrendManager` normalize `>10^13 ? /1000` — keep these in lockstep, or a backtest crossing the boundary silently freezes the trend lookup on stale data. (This was a real, hard-to-find bug.)
- **Base-asset commission netting.** When a buy fills with commission paid in the base asset, the credited quantity is reduced by the commission so subsequent sells use a quantity the exchange will accept. The mock executor mirrors this so backtests match live.
- **Namespaces ≠ assembly names (legacy).** Types still live under `GripTrader.Core.*` / `GripTrader.Tuner.*` namespaces even though the assemblies are `Trading.*` — a deliberate low-churn extraction choice. Don't mass-rename namespaces casually; consumers depend on them.

## InternalsVisibleTo

Each lib grants `InternalsVisibleTo` to the consuming app's test assembly so live/backtest seams stay testable without going public: `BinanceResponseParser` / `Connection.Dispatch` / `BinanceConnectorExecutor` (Exchange.Binance), `MockBinanceExecutor` / `HistoricalTrendManager` (Backtest), the metric helpers (Tuning). When you split tests into this repo, re-point each grant at the local test assembly.

## Skills

This submodule's `codex/skills/` directory ships rules tuned to these libraries — read the relevant one before changing that area:
- `reusable-core-boundaries` — the strategy-agnostic invariant + the seam contracts (read this first).
- `binance-integration` — exchange API rules (signing, recvWindow, rate limits, fees, idempotent orders).
- `backtest-engine` — feeder parity, mock-executor realism, determinism, epoch normalization.
- `dotnet-performance` — high-frequency parsing, streaming, async-in-libraries.

## Keeping this file current

Update this file in the same change that adds/removes a library, changes the DAG, retires a seam interface, or shifts an invariant above.
