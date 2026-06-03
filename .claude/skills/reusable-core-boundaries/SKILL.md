---
name: reusable-core-boundaries
description: The strategy-agnostic boundary rules for the trading-core libraries. Use when adding code to Trading.Abstractions/Exchange.Binance/Backtest/Tuning, introducing or changing a seam interface, or whenever you might be tempted to reference a concrete strategy from the reusable core.
---

# trading-core — Reusable Boundary Rules

These libraries are consumed as a submodule by multiple bots. The whole value is that they are **strategy-agnostic**. Breaking that quietly couples every consumer to one strategy.

## The cardinal rule

**No library here may reference a concrete strategy.** Forbidden anywhere in `Trading.*`, `Log`, `Telegram`, `Websockets`:

- Strategy types: `GridBot`, `GridConfig`, `GridState`, `ScoreboardData`, `TradeOpenedEventArgs`, etc.
- The composition root (`Trader`) or the strategy host (`BacktestRunner`, `StatsCollector`).
- A `ProjectReference` to the consuming app (`GripTrader.Core` / `GripTrader.Tuner`).

If you need a strategy concept, expose it as an **interface or value type in `Trading.Abstractions`** and let the consumer implement it. The strategy lives in the consuming repo, not here.

Acceptance check before committing:
```
grep -rIn -e GridBot -e GridConfig -e ScoreboardData -e "GripTrader.Core.csproj" Trading.* Log Telegram Websockets
```
Should return nothing (only the harmless `TradingPair` MARKET_LOT_SIZE comment is allowed).

## Keep the DAG one-way

```
Trading.Abstractions   <-  Trading.Exchange.Binance
        ^               <-  Trading.Backtest
        |               <-  Trading.Tuning
   (depends on nothing)
```

- `Trading.Abstractions` depends on **nothing** (BCL only). Never add a package/project ref to it — that ripples to every consumer. New shared value types and interfaces go here.
- The other three libs depend on Abstractions (+ infra), never on each other.
- A new interface that both a lib and a strategy must share goes in **Abstractions** — putting it in `Backtest`/`Exchange.Binance` creates a cycle the moment a strategy implements it.

## The seams (don't reshape casually)

These interfaces are how strategies plug in. Changing their shape breaks every consumer:

- `IOrderExecutor` — strategy → exchange. Small by design (one `await` point per call); rate limiting is a decorator (`ThrottledOrderExecutor`), not the strategy's concern.
- `IPriceSink` (live) / `IBacktestTickReceiver` (backtest) — feed → strategy. The backtest one carries `bool? isUptrend` ("macro filter permissive?"); strategies may ignore it.
- `IStateStore<TState>` — generic over the strategy's own state blob.
- `TradingSettingsBase` — transport/backtest settings a bot's settings type derives from.

When you genuinely must extend a seam, add a new member with a default-preserving overload rather than changing an existing signature, and update every implementer in this repo.

## When adding new infra

1. Decide the right lib: live-exchange code → `Exchange.Binance`; backtest code → `Backtest`; metrics/sweep → `Tuning`; a contract/value type shared across them → `Abstractions`.
2. Add only the package/project refs that lib truly needs (don't pollute Abstractions).
3. If tests need internals, grant `InternalsVisibleTo` to the test assembly — don't make it public just for tests.
4. Re-run the acceptance grep + `dotnet build trading-core.slnx` (standalone) before committing.

## Don't

- Don't rename the `GripTrader.Core.*` / `GripTrader.Tuner.*` namespaces (assembly names are `Trading.*`, namespaces are legacy — consumers depend on them).
- Don't add a strategy parameter to a reusable type "just for now."
- Don't reach into a consumer to fix a coupling — invert it with an interface in Abstractions.
