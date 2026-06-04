# trading-core

Reusable, **strategy-agnostic** trading libraries for building crypto trading bots on **.NET 10**. Extracted from a Binance grid-bot and consumed as a **git submodule** — so any bot can connect to Binance, backtest, and tune a strategy without the engine ever depending on that strategy.

## Libraries

| Library | Purpose | Depends on |
|---|---|---|
| **`Trading.Abstractions`** | Contracts + value types every consumer shares: `IOrderExecutor`, `IPriceSink`, `IBacktestTickReceiver`, `IStateStore<T>`, `IQuoteSource`, `BidAsk`, `TradingPair`, `MathHelpers`, `Symbols`, `TradingSettingsBase`. | *nothing (BCL only)* |
| **`Trading.Exchange.Binance`** | Live Binance integration: signed REST, combined-stream websocket feed, rate-limited order executor, response parsing, exchange-rule loading. | Abstractions, Websockets, Log |
| **`Trading.Backtest`** | Deterministic in-memory backtest engine: mock matching executor (fees/slippage/filters), kline + aggTrades feeders, trend manager. | Abstractions, Log |
| **`Trading.Tuning`** | Parameter sweep / walk-forward / metrics (Sharpe, Sortino, Calmar, drawdown) + report DTOs. | Abstractions |
| `Log` · `Telegram` · `Websockets` | Shared infra (Serilog file logging, Telegram notifications, websocket manager). | — |

The dependency graph is strictly one-way: `Trading.Abstractions` is the hub (depends on nothing), and nothing here references a concrete strategy.

## Build

```bash
dotnet build trading-core.slnx
```

There are no tests in this repo yet — the libraries are exercised by the consuming app's suite via `InternalsVisibleTo`. `trading-core.slnx` building standalone (with no consuming app present) is the self-containment gate.

## Historical data (for backtests)

`scripts/` has PowerShell-free Python downloaders for Binance spot history, writing
the on-disk layout the `Trading.Backtest` feeders expect:

```bash
python scripts/download-binance-klines.py BNB SOL XRP          # klines (multi-year sweeps)
python scripts/download-binance-aggtrades.py BCH --from 2025-01 --to 2025-03   # ticks (execution validation)
```

See [`scripts/README.md`](scripts/README.md) for options. Point a feeder's
`BacktestKlineFolderPath` / `BacktestCsvPath` at the resulting folder.

## Consume it in a bot

```bash
# in your bot's repo
git submodule add git@github.com:Kywi/trading-core.git external/trading-core
```

Then reference the libs you need from your project:

```xml
<ProjectReference Include="..\external\trading-core\Trading.Abstractions\Trading.Abstractions.csproj" />
<ProjectReference Include="..\external\trading-core\Trading.Backtest\Trading.Backtest.csproj" />
```

Pin the submodule to a tag/SHA so a bump is always deliberate.

## Write a strategy against it

1. Implement the seams: **`IBacktestTickReceiver`** (backtest) and/or **`IPriceSink`** (live).
2. Derive your settings from **`TradingSettingsBase`** (so the Tuner's parameter binding works for free).
3. Provide a thin composition root that wires an `IOrderExecutor` + a feed to your strategy — `MockBinanceExecutor` for backtest, `BinanceConnectorExecutor` (+ `ThrottledOrderExecutor`) for live.

Minimal backtest, no GridBot involved:

```csharp
using GripTrader.Core.Abstractions; // IBacktestTickReceiver
using GripTrader.Core.Backtest;     // MockBinanceExecutor, HistoricalKlineFeeder
using GripTrader.Core.Models;       // BidAsk

var exec  = new MockBinanceExecutor(initialBankroll: 1000m, slippagePct: 0.001m);
var strat = new MyStrategy(exec, "BNBUSDT");                  // : IBacktestTickReceiver
var feeder = new HistoricalKlineFeeder(strat, exec, "BNBUSDT");

var files = HistoricalKlineFeeder.CollectSortedKlineFiles("D:/data/BnbUsdt/1hklines", "BNBUSDT");
await feeder.PlayHistoricalDataAsync(files);

var equity = await exec.GetCurrentTotalEquityAsync("BNBUSDT", strat.LastPrice);
```

(The grid-bot repo's `new-bot/` is a runnable reference of exactly this pattern.)

## What lives where

- **Here (trading-core):** the engine — exchange I/O, backtest, tuning, the seam interfaces.
- **In your bot:** the strategy logic, its settings (derived from `TradingSettingsBase`), and the composition root that wires it all together.

## For AI coding assistants

`CLAUDE.md` and `.claude/skills/` encode the boundary rules and load-bearing invariants (strategy-agnostic DAG, decimal-only math, deterministic single-threaded backtest path, ms↔microsecond epoch normalization). Read `reusable-core-boundaries` before adding code.

> Note: namespaces are still `GripTrader.Core.*` / `GripTrader.Tuner.*` even though the assemblies are `Trading.*` — a deliberate low-churn extraction choice. Don't mass-rename them; consumers depend on them.
