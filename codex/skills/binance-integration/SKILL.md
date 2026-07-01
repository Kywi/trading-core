---
name: binance-integration
description: Binance exchange API rules for the Trading.Exchange.Binance library. Use when changing order placement, signed REST calls, recvWindow/server-time handling, rate-limit/throttle logic, fee accounting, idempotent order submission, or the combined-stream websocket feed.
---

# Trading.Exchange.Binance — Integration Rules

All live Binance knowledge lives in this one lib. Strategies never see Binance specifics — they see `IOrderExecutor` / `IPriceSink`.

## Signed REST + time

- Signed calls (`myTrades`, `account`, order ops) are HMAC-signed (`SignatureHelper`) with a `recvWindow` and a timestamp. **Sync the server-time offset on startup** (`ComputeServerTimeOffset`) and apply it to every signed call — a clock skew larger than `recvWindow` makes Binance reject everything with `-1021`.
- `recvWindow` is clamped (`ClampRecvWindow`) to Binance's max. Don't pass an unclamped user value straight through.
- Hand-rolled signed endpoints live alongside the `Binance.Spot` SDK usage in `BinanceConnectorExecutor` / `IBinanceTradeClient`. Keep the SDK seam (`IBinanceTradeClient` / `SpotAccountTradeClient`) narrow so it stays fakeable in tests.

## Rate limiting is a decorator, not the bot's job

- `ThrottledOrderExecutor` wraps `IOrderExecutor` and enforces **45 orders / 10 s + 250 ms minimum spacing** via `OrderRateLimiter`. Order-placing code calls `IOrderExecutor` and never thinks about limits.
- If Binance raises a `-1003`/`429`/`418`, back off — do not hammer. Classify transient (network/server/rate-limit) vs terminal (order-not-found) errors; `BinanceConnectorExecutor.ClassifyByErrorCode` is the single place for this. A transient error must NOT be treated as "order absent" (that would duplicate a resting order).

## Idempotent order submission

- Mint a deterministic `clientOrderId` and **persist it before** the network call, so a placement that times out after the exchange accepted it can be reconciled (`QueryOrderByClientIdAsync`) instead of duplicated.
- On startup / reconnect, reconcile any pending order by id before acting on it — it may have filled while offline.

## Fees / quantity math (must match backtest)

- Commission is a per-side decimal fraction (`CommissionRate`: `0.001` standard, `0.00075` with BNB discount). It feeds both realized-PnL math and the mock executor, so live and backtest agree.
- **Base-asset commission netting:** when a buy fills with commission paid in the base asset, reduce the credited quantity by the commission before computing any sell quantity — otherwise sells bounce with `INSUFFICIENT_BALANCE`. `BinanceResponseParser` does this; the mock mirrors it.
- Respect exchange filters: `TickSize` (price), `StepSize`/`LOT_SIZE` (qty), `MIN_NOTIONAL`. `PublicService` loads them into `TradingPair`; `MathHelpers.ClampPrice/ClampQuantity` apply them. A sub-`MIN_NOTIONAL` order is rejected live — don't assume it fills.

## Websocket feed

- `Connection` consumes the Binance **combined-stream** bookTicker WS, parses `CombinedStreamEventDto`, and fans out `BidAsk` to `IPriceSink[]`. It owns reconnect + an idle watchdog — keep those; a silent dead socket starves the strategy.
- `Connection.Dispatch` is the internal parse/fan-out seam (test-covered via `InternalsVisibleTo`). Keep it side-effect-isolated so tests can drive synthetic frames.

## Decimal everywhere

Prices, quantities, fees, equity are `decimal`. Never parse a price into `double`. Use `NumberStyles.Any` + `CultureInfo.InvariantCulture` for exchange payloads.

## Not network-tested

Real socket connect + signed REST round-trips have no automated coverage. After changing them, verify by build + a manual run (live testnet). The offline seams (parsing, error classification, dispatch fan-out, reconnect loop) ARE unit-tested in the consuming repo — keep them so.
