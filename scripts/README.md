# scripts — historical data downloaders

PowerShell-free Python downloaders for Binance spot historical data from
[data.binance.vision](https://data.binance.vision), written into the on-disk layout
the `Trading.Backtest` feeders expect.

Requires Python 3.9+ (stdlib only — no pip installs).

## Layout produced

```
<out>/<Base>Usdt/<interval>klines/<SYM>-<interval>-YYYY-MM.csv   # klines
<out>/<Base>Usdt/aggTrades/<SYM>-aggTrades-YYYY-MM.csv           # aggTrades
```

`<out>` defaults to `D:/GridTrader` (override with `--out` or the `BINANCE_DATA_DIR`
env var). Point a feeder's `BacktestKlineFeederPath` / `BacktestKlineFolderPath` /
`BacktestCsvPath` at the matching folder.

## Klines (use for multi-year sweeps)

```bash
python download-binance-klines.py BNB SOL XRP
python download-binance-klines.py --out D:/data --intervals 1h,4h --from 2019-01 --to 2026-02 BNB
```
Concurrent, skips files already present, tolerates not-yet-listed months (404).
Klines are small (≈10 KB–2 MB/month) — a full multi-year history is fast.

## aggTrades (use for execution-sensitive validation)

```bash
python download-binance-aggtrades.py BCH --from 2025-01 --to 2025-03
```
Tick-level, **much larger** (tens of MB to several GB per month) — streamed to disk,
downloaded one month at a time. Use only for short windows where trailing-stop /
slippage fidelity matters; use klines for everything else.

## Notes

- 2025+ archives switched to **microsecond** timestamps — `Trading.Backtest`
  normalizes that, so the downloaded files work as-is.
- The `<Base>Usdt` folder name (e.g. `BnbUsdt`) is just the convention these scripts
  use; feeders take an explicit path, so the name is arbitrary.
