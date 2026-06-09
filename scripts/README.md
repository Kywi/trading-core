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

## USDⓈ-M futures (perpetuals) — for the pairs-on-perps build

`download-binance-futures.py` pulls the **futures** archive (`data/futures/um/...`, a
different base path than spot — the spot scripts cannot reach it). Three public streams:

```bash
python download-binance-futures.py BTC ETH --intervals 1h --from 2020-01 --to 2025-12
python download-binance-futures.py SOL --streams klines,markprice,funding --from 2021-01 --to 2021-06
python download-binance-futures.py BTC --list-since      # earliest available month per stream
```

- **klines** (last-trade OHLCV) and **markPriceKlines** (MARK-price OHLC) — both replay
  through `HistoricalKlineFeeder` unchanged (same column order + epoch as spot). Funding
  settles and liquidation triggers on **mark**, so markPrice is a *separate required* stream;
  its `volume`/taker fields are 0 — do not treat as traded volume.
- **fundingRate** (monthly only) — CSV columns `calc_time,funding_interval_hours,last_funding_rate`.
  Read the per-row `funding_interval_hours` (8/4/1, dynamic) directly; never hard-code 8h.
- SHA256-verified against each file's `.zip.CHECKSUM`; every extracted file is logged to
  `<out>/futures/um/manifest.jsonl` (symbol, stream, interval, month, url, sha256, bytes) for
  reproducibility. Re-runs skip files already present.
- Layout: `<out>/futures/um/<SYM>/<stream>/<interval?>/<SYM>-...-YYYY-MM.csv`.
- Archive floor is ~2020-01 (BTC); alts start later (SOL 2020-09) — use `--list-since` to find
  each symbol's listing month. Maintenance-margin **leverage brackets are NOT here** (signed
  REST `/fapi/v1/leverageBracket` only) — fetched out-of-band as a dated static snapshot.

`enumerate-futures-universe.py` builds the **point-in-time, survivorship-bias-free universe**
straight from the archive (the live `exchangeInfo` omits delisted names — the survivorship trap):

```bash
python enumerate-futures-universe.py --out futures-universe.json   # full roster (incl. delisted)
python enumerate-futures-universe.py --spans BTCUSDT SRMUSDT SOLUSDT  # listing/delisting window
```
Lists all symbols under `klines/` (paginated S3) and derives each symbol's first/last available
month; delisted names (e.g. `SRMUSDT`, archive ends 2024-05) appear in the roster and are flagged.

## Notes

- 2025+ archives switched to **microsecond** timestamps — `Trading.Backtest`
  normalizes that, so the downloaded files work as-is.
- The `<Base>Usdt` folder name (e.g. `BnbUsdt`) is just the convention these scripts
  use; feeders take an explicit path, so the name is arbitrary.
