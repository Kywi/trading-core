"""
Download Binance USDⓈ-M (um) FUTURES monthly history from data.binance.vision into a
layout the Trading.Backtest feeders can consume. Separate from the SPOT downloaders
(download-binance-klines.py / -aggtrades.py) because the futures archive lives under a
different base path (data/futures/um/...), verified against live data.binance.vision.

Streams (all PUBLIC — no API key needed):
  - klines           last-trade OHLCV         .../klines/{SYM}/{interval}/{SYM}-{interval}-YYYY-MM.zip
  - markPriceKlines  MARK price OHLC          .../markPriceKlines/{SYM}/{interval}/{SYM}-{interval}-YYYY-MM.zip
  - fundingRate      settled funding history  .../fundingRate/{SYM}/{SYM}-fundingRate-YYYY-MM.zip   (MONTHLY ONLY)

Why each matters (see traiding-core-pairs-plan.md Phase 1 / 2a):
  - Funding settles on MARK and liquidation triggers on MARK, not last trade -> markPriceKlines
    is a separate, required dataset (its high/low feed the adverse-extreme liquidation probe).
  - The fundingRate CSV ships an explicit `funding_interval_hours` column (8/4/1, dynamic per
    symbol) — read it directly; never hard-code 8h.
  - Maintenance-margin leverage BRACKETS are NOT in this archive (signed REST /fapi/v1/leverageBracket
    only) — handled out-of-band, not here.

On-disk layout produced (mirrors the archive; feeders take an explicit path so names are flexible):
    <out>/futures/um/<SYM>/klines/<interval>/<SYM>-<interval>-YYYY-MM.csv
    <out>/futures/um/<SYM>/markPriceKlines/<interval>/<SYM>-<interval>-YYYY-MM.csv
    <out>/futures/um/<SYM>/fundingRate/<SYM>-fundingRate-YYYY-MM.csv

Reproducibility: every extracted file is appended to <out>/futures/um/manifest.jsonl with the
symbol, stream, interval, month, source URL, the Binance SHA256 (verified against the .CHECKSUM
sibling), and the extracted CSV byte size. Re-running skips files already present.

Requires Python 3.9+ (stdlib only). 2025+ archives are microsecond-stamped — Trading.Backtest
normalizes that (>10^13 ? /1000), so no special handling here.

Examples:
    python download-binance-futures.py BTC ETH --intervals 1h --from 2020-01 --to 2025-12
    python download-binance-futures.py SOL --streams klines,markprice,funding --from 2021-01 --to 2021-06
    python download-binance-futures.py --list-since BTC      # earliest available month per stream
"""
import argparse, hashlib, io, json, os, sys, urllib.request, urllib.error, zipfile
from concurrent.futures import ThreadPoolExecutor, as_completed

BASE = "https://data.binance.vision/data/futures/um/monthly"
DEFAULT_SYMBOLS = ["BTC", "ETH", "BNB", "SOL", "XRP", "ADA", "DOGE", "AVAX", "LINK", "LTC"]
INTERVAL_STREAMS = {"klines", "markPriceKlines"}   # carry an interval segment
FLAT_STREAMS = {"fundingRate"}                      # no interval, monthly only
STREAM_ALIASES = {
    "klines": "klines", "kline": "klines",
    "markprice": "markPriceKlines", "markpriceklines": "markPriceKlines", "mark": "markPriceKlines",
    "funding": "fundingRate", "fundingrate": "fundingRate",
}


def parse_ym(s):
    y, m = s.split("-")
    return int(y), int(m)


def months(start, end):
    y, m = start
    while (y, m) <= end:
        yield y, m
        m += 1
        if m > 12:
            m, y = 1, y + 1


def url_for(stream, sym, interval, y, m):
    if stream in INTERVAL_STREAMS:
        return f"{BASE}/{stream}/{sym}/{interval}/{sym}-{interval}-{y:04d}-{m:02d}.zip"
    return f"{BASE}/{stream}/{sym}/{sym}-{stream}-{y:04d}-{m:02d}.zip"


def csv_path_for(out, stream, sym, interval, y, m):
    base = os.path.join(out, "futures", "um", sym, stream)
    if stream in INTERVAL_STREAMS:
        d = os.path.join(base, interval)
        name = f"{sym}-{interval}-{y:04d}-{m:02d}.csv"
    else:
        d = base
        name = f"{sym}-{stream}-{y:04d}-{m:02d}.csv"
    return d, os.path.join(d, name)


def _get(url, timeout=60):
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return r.read()


def fetch_one(stream, base_sym, quote, interval, y, m, out_root, verify):
    sym = base_sym + quote
    d, csv_path = csv_path_for(out_root, stream, sym, interval, y, m)
    if os.path.exists(csv_path):
        return ("skip", stream, sym, interval, y, m, None)
    url = url_for(stream, sym, interval, y, m)
    try:
        zip_bytes = _get(url)
    except urllib.error.HTTPError as e:
        # 404 = month not listed (pre-listing, current partial month, or after delist) — expected.
        return (("404" if e.code == 404 else f"err{e.code}"), stream, sym, interval, y, m, None)
    except Exception as e:
        return ("err:" + type(e).__name__, stream, sym, interval, y, m, None)

    sha = hashlib.sha256(zip_bytes).hexdigest()
    if verify:
        try:
            checksum_txt = _get(url + ".CHECKSUM").decode("utf-8", "replace")
            expected = checksum_txt.split()[0].lower()
            if expected != sha:
                return ("checksum_mismatch", stream, sym, interval, y, m, None)
        except urllib.error.HTTPError:
            pass  # some months predate the .CHECKSUM sibling; keep the file, sha still recorded

    os.makedirs(d, exist_ok=True)
    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as z:
        with z.open(z.namelist()[0]) as f, open(csv_path, "wb") as out:
            data = f.read()
            out.write(data)
    rec = {
        "symbol": sym, "stream": stream, "interval": interval if stream in INTERVAL_STREAMS else None,
        "month": f"{y:04d}-{m:02d}", "url": url, "sha256": sha,
        "csv_bytes": len(data), "csv_path": os.path.relpath(csv_path, out_root),
    }
    return ("ok", stream, sym, interval, y, m, rec)


def append_manifest(out_root, records):
    if not records:
        return
    path = os.path.join(out_root, "futures", "um", "manifest.jsonl")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "a", encoding="utf-8") as f:
        for rec in records:
            f.write(json.dumps(rec, sort_keys=True) + "\n")


def list_since(base_sym, quote, intervals):
    """Probe the earliest available month per stream for a symbol (point-in-time listing date)."""
    sym = base_sym + quote
    print(f"Earliest available month for {sym}:")
    probes = [("klines", intervals[0]), ("markPriceKlines", intervals[0]), ("fundingRate", None)]
    for stream, interval in probes:
        found = None
        for y in range(2019, 2027):
            for m in range(1, 13):
                url = url_for(stream, sym, interval, y, m) + ".CHECKSUM"
                try:
                    urllib.request.urlopen(urllib.request.Request(url, method="HEAD"), timeout=20)
                    found = f"{y:04d}-{m:02d}"
                    break
                except urllib.error.HTTPError:
                    continue
                except Exception:
                    continue
            if found:
                break
        label = f"{stream}" + (f" {interval}" if interval else "")
        print(f"  {label:24} -> {found or 'not found in 2019-2026'}")


def main():
    ap = argparse.ArgumentParser(description="Download Binance USDⓈ-M futures monthly history.")
    ap.add_argument("symbols", nargs="*", help="base assets, e.g. BTC ETH (default: a liquid set)")
    ap.add_argument("--out", default=os.environ.get("BINANCE_DATA_DIR", "./data"), help="output root dir")
    ap.add_argument("--quote", default="USDT")
    ap.add_argument("--intervals", default="1h", help="comma list for klines+markPrice, e.g. 1h,5m")
    ap.add_argument("--streams", default="klines,markprice,funding", help="comma list: klines,markprice,funding")
    ap.add_argument("--from", dest="frm", default="2020-01", help="start YYYY-MM (futures archive floor ~2020-01)")
    ap.add_argument("--to", default="2025-12", help="end YYYY-MM (inclusive)")
    ap.add_argument("--workers", type=int, default=12)
    ap.add_argument("--no-verify", action="store_true", help="skip SHA256 .CHECKSUM verification")
    ap.add_argument("--list-since", action="store_true", help="just probe earliest available month per stream")
    a = ap.parse_args()

    symbols = [s.upper() for s in (a.symbols or DEFAULT_SYMBOLS)]
    intervals = [i.strip() for i in a.intervals.split(",") if i.strip()]
    streams = []
    for s in a.streams.split(","):
        key = STREAM_ALIASES.get(s.strip().lower())
        if key and key not in streams:
            streams.append(key)
    if not streams:
        sys.exit("No valid --streams (use klines,markprice,funding).")

    if a.list_since:
        for b in symbols:
            list_since(b, a.quote, intervals)
        return

    start, end = parse_ym(a.frm), parse_ym(a.to)

    # Build the job list. Interval streams fan out over intervals; flat streams (funding) run once.
    jobs = []
    for b in symbols:
        for stream in streams:
            if stream in INTERVAL_STREAMS:
                for iv in intervals:
                    for (y, m) in months(start, end):
                        jobs.append((stream, b, a.quote, iv, y, m, a.out, not a.no_verify))
            else:
                for (y, m) in months(start, end):
                    jobs.append((stream, b, a.quote, "", y, m, a.out, not a.no_verify))

    print(f"Futures um: symbols={symbols} streams={streams} intervals={intervals} "
          f"{a.frm}..{a.to} -> {a.out}  ({len(jobs)} files)")

    counts, records = {}, []
    with ThreadPoolExecutor(max_workers=a.workers) as ex:
        futs = [ex.submit(fetch_one, *j) for j in jobs]
        done = 0
        for fut in as_completed(futs):
            st, stream, sym, interval, y, m, rec = fut.result()
            counts[st] = counts.get(st, 0) + 1
            if rec:
                records.append(rec)
            done += 1
            if st.startswith("err") or st == "checksum_mismatch":
                print(f"  {st}: {stream} {sym} {interval} {y:04d}-{m:02d}")
            if done % 200 == 0:
                print(f"  {done}/{len(jobs)} ok={counts.get('ok',0)} skip={counts.get('skip',0)} 404={counts.get('404',0)}")

    append_manifest(a.out, records)
    print("DONE", {k: v for k, v in sorted(counts.items())},
          f"manifest+={len(records)} -> {os.path.join(a.out, 'futures/um/manifest.jsonl')}")


if __name__ == "__main__":
    main()
