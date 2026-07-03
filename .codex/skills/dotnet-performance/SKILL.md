---
name: dotnet-performance
description: .NET 10 performance patterns for the trading-core libraries. Use when parsing high-frequency market data (klines/aggTrades), streaming large CSVs, writing hot-path executor/feeder code, or authoring async library methods.
---

# trading-core — .NET Performance Rules

These libs sit on the hot path: a backtest replays hundreds of millions of ticks, and the live feed parses every bookTicker update. Allocations and boxing here cost real wall-clock.

## Parsing market data

- Parse CSV/JSON lines with `ReadOnlySpan<char>` slicing and `decimal.Parse(span, NumberStyles.Any, CultureInfo.InvariantCulture)` — do not `string.Split` per line in a multi-GB replay (it allocates an array + N substrings per row).
- Skip header/blank rows by checking the first char is a digit, not by try/catching a parse.
- Use `CultureInfo.InvariantCulture` always — exchange payloads use `.` decimals; a comma-locale machine will mis-parse otherwise.

## decimal, not double

Every price/quantity/fee/percentage is `decimal` (exactness matters for money + determinism). Don't introduce `double` on the hot path "for speed" — it breaks the never-sell-at-a-loss / fee math and makes backtests non-reproducible.

## Streaming, not buffering

Feeders read files with a buffered `StreamReader` / `FileStream` (`FileOptions.SequentialScan`, large buffer) and process line-by-line. Never `ReadAllLines` or accumulate ticks into a `List<T>` — keep memory constant regardless of file size.

## Allocation discipline on the tick path

- Reuse buffers; avoid LINQ in per-tick loops (it allocates iterators/closures). A plain `for`/`foreach` over an array is fine and faster.
- Throttle anything that allocates per tick but isn't needed every tick (e.g. snapshot/scoreboard objects in a backtest are rebuilt every Nth tick, not every tick).
- Prefer `struct` readonly value types (`BidAsk`, `TrendSnapshot`) over classes for tick-rate data; avoid boxing them into `object`.

## Async in a library

- These are libraries, not an app. Inside library code use `await ... .ConfigureAwait(false)` to avoid capturing a synchronization context a host might have.
- Don't spin up threads or `Task.Run` in the backtest path — it must stay single-threaded and deterministic. The live path uses one pump; don't add a second writer to shared state.
- Don't block on async (`.Result` / `.Wait()`) — it can deadlock under a host's context and stalls the throttle.

## Measure before optimizing

The hot paths are: feeder line parsing, `MockBinanceExecutor.ProcessTick`, and `Connection.Dispatch`. If you're optimizing elsewhere, you're probably not on the hot path — keep that code simple and readable instead.
