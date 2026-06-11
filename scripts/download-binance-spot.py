"""
Download Binance SPOT monthly klines from data.binance.vision into a layout the
Trading.Backtest feeders can consume. Separate from the FUTURES downloader
(download-binance-futures.py) because the spot archive lives under a different base path
(data/spot/monthly/klines/...), and separate from the GridBot-legacy download-binance-klines.py
(different on-disk layout, NO manifest) — leave that one untouched.

Stream (PUBLIC — no API key needed):
  - klines   last-trade OHLCV   .../klines/{SYM}/{interval}/{SYM}-{interval}-YYYY-MM.zip

Why spot at all (see docs/carry/carry-design.md S1 / C1):
  - The carry build's spot leg is a MockPerpExecutor in a spot-equivalent config (1x long,
    maint 0). It needs spot OHLCV to fill the long leg; the same sliced klines file doubles as
    its mark source (mark aligns by open_time -> never stale). Spot has NO funding and NO
    markPriceKlines stream — klines is the only spot dataset this build consumes; other streams
    are rejected with a clear message.

Verified live against data.binance.vision (2026-06-11) before coding:
  - Path shape: data/spot/monthly/klines/{SYM}/{interval}/{SYM}-{interval}-YYYY-MM.zip (+ .CHECKSUM).
  - .CHECKSUM format identical to futures: "<sha256_hex>  <filename>" -> split()[0] is the hash.
  - Spot 1h CSVs ship NO header row (first line is data); the downloader stores the raw CSV
    bytes verbatim either way, so the header question is the consumer's, not ours.
  - Epoch unit: 2024 months are millisecond-stamped (13-digit open_time); 2025 months are
    microsecond-stamped (16-digit) — the spot archive switched to microseconds during 2025, same
    as the futures/klines archives. Trading.Backtest normalizes that (>10^13 ? /1000), so no
    special handling here.

On-disk layout produced (mirrors the archive; feeders take an explicit path so names are flexible):
    <out>/spot/<SYM>/klines/<interval>/<SYM>-<interval>-YYYY-MM.csv

Reproducibility: every extracted file is appended to <out>/spot/manifest.jsonl with the SAME
line schema as the futures manifest (symbol, stream, interval, month, source URL, the Binance
SHA256 verified against the .CHECKSUM sibling, the extracted CSV byte size, and the data-root-
relative csv_path) so the bot's HistoryCatalog parses both manifests identically. `csv_path` is
relative to the data root (os.path.relpath against --out), e.g. spot/BTCUSDT/klines/1h/...csv.
Re-running skips files already present.

Requires Python 3.9+ (stdlib only).

Examples:
    python download-binance-spot.py BTC ETH --intervals 1h --from 2020-01 --to 2025-12
    python download-binance-spot.py --list-since BTC      # earliest available month
"""
import argparse, hashlib, io, json, os, sys, urllib.request, urllib.error, zipfile
from concurrent.futures import ThreadPoolExecutor, as_completed

BASE = "https://data.binance.vision/data/spot/monthly"
DEFAULT_SYMBOLS = ["BTC", "ETH", "BNB", "SOL", "XRP", "ADA", "DOGE", "AVAX", "LINK", "LTC"]
# Spot exposes only klines for this build. funding/markPrice are futures-only — reject them so a
# stale --streams copied from the futures invocation fails loudly instead of silently 404ing.
SPOT_STREAM = "klines"
STREAM_ALIASES = {"klines": "klines", "kline": "klines"}
FUTURES_ONLY_ALIASES = {
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


def url_for(sym, interval, y, m):
    return f"{BASE}/{SPOT_STREAM}/{sym}/{interval}/{sym}-{interval}-{y:04d}-{m:02d}.zip"


def csv_path_for(out, sym, interval, y, m):
    d = os.path.join(out, "spot", sym, SPOT_STREAM, interval)
    name = f"{sym}-{interval}-{y:04d}-{m:02d}.csv"
    return d, os.path.join(d, name)


def _get(url, timeout=60):
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return r.read()


def fetch_one(base_sym, quote, interval, y, m, out_root, verify):
    sym = base_sym + quote
    d, csv_path = csv_path_for(out_root, sym, interval, y, m)
    if os.path.exists(csv_path):
        return ("skip", sym, interval, y, m, None)
    url = url_for(sym, interval, y, m)
    try:
        zip_bytes = _get(url)
    except urllib.error.HTTPError as e:
        # 404 = month not listed (pre-listing, current partial month, or no USDT spot market) — expected.
        return (("404" if e.code == 404 else f"err{e.code}"), sym, interval, y, m, None)
    except Exception as e:
        return ("err:" + type(e).__name__, sym, interval, y, m, None)

    sha = hashlib.sha256(zip_bytes).hexdigest()
    if verify:
        try:
            checksum_txt = _get(url + ".CHECKSUM").decode("utf-8", "replace")
            expected = checksum_txt.split()[0].lower()
            if expected != sha:
                return ("checksum_mismatch", sym, interval, y, m, None)
        except urllib.error.HTTPError:
            pass  # some months predate the .CHECKSUM sibling; keep the file, sha still recorded

    os.makedirs(d, exist_ok=True)
    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as z:
        with z.open(z.namelist()[0]) as f, open(csv_path, "wb") as out:
            data = f.read()
            out.write(data)
    rec = {
        "symbol": sym, "stream": SPOT_STREAM, "interval": interval,
        "month": f"{y:04d}-{m:02d}", "url": url, "sha256": sha,
        "csv_bytes": len(data), "csv_path": os.path.relpath(csv_path, out_root),
    }
    return ("ok", sym, interval, y, m, rec)


def append_manifest(out_root, records):
    if not records:
        return
    path = os.path.join(out_root, "spot", "manifest.jsonl")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "a", encoding="utf-8") as f:
        for rec in records:
            f.write(json.dumps(rec, sort_keys=True) + "\n")


def list_since(base_sym, quote, interval):
    """Probe the earliest available month for a symbol (point-in-time spot listing date)."""
    sym = base_sym + quote
    found = None
    for y in range(2017, 2027):
        for mo in range(1, 13):
            url = url_for(sym, interval, y, mo) + ".CHECKSUM"
            try:
                urllib.request.urlopen(urllib.request.Request(url, method="HEAD"), timeout=20)
                found = f"{y:04d}-{mo:02d}"
                break
            except urllib.error.HTTPError:
                continue
            except Exception:
                continue
        if found:
            break
    print(f"Earliest available month for {sym}:")
    print(f"  klines {interval:18} -> {found or 'not found in 2017-2026 (no spot market?)'}")


def main():
    ap = argparse.ArgumentParser(description="Download Binance SPOT monthly klines history.")
    ap.add_argument("symbols", nargs="*", help="base assets, e.g. BTC ETH (default: a liquid set)")
    ap.add_argument("--out", default=os.environ.get("BINANCE_DATA_DIR", "./data"), help="output root dir")
    ap.add_argument("--quote", default="USDT")
    ap.add_argument("--intervals", default="1h", help="comma list, e.g. 1h,5m")
    ap.add_argument("--streams", default="klines", help="spot has only klines — anything else is rejected")
    ap.add_argument("--from", dest="frm", default="2020-01", help="start YYYY-MM (spot archive floor ~2017-08)")
    ap.add_argument("--to", default="2025-12", help="end YYYY-MM (inclusive)")
    ap.add_argument("--workers", type=int, default=12)
    ap.add_argument("--no-verify", action="store_true", help="skip SHA256 .CHECKSUM verification")
    ap.add_argument("--list-since", action="store_true", help="just probe earliest available month")
    a = ap.parse_args()

    symbols = [s.upper() for s in (a.symbols or DEFAULT_SYMBOLS)]
    intervals = [i.strip() for i in a.intervals.split(",") if i.strip()]

    # Validate --streams: klines only. A futures-only stream name is a likely copy-paste from the
    # futures invocation — reject it explicitly rather than silently 404 every month.
    for s in a.streams.split(","):
        key = s.strip().lower()
        if not key:
            continue
        if key in FUTURES_ONLY_ALIASES:
            sys.exit(f"--streams '{s.strip()}' is a FUTURES-only stream; spot has only klines. "
                     "(Use download-binance-futures.py for markPrice/funding.)")
        if key not in STREAM_ALIASES:
            sys.exit(f"--streams '{s.strip()}' is not a valid spot stream (only 'klines').")

    if a.list_since:
        for b in symbols:
            list_since(b, a.quote, intervals[0])
        return

    start, end = parse_ym(a.frm), parse_ym(a.to)

    jobs = []
    for b in symbols:
        for iv in intervals:
            for (y, m) in months(start, end):
                jobs.append((b, a.quote, iv, y, m, a.out, not a.no_verify))

    print(f"Spot: symbols={symbols} intervals={intervals} "
          f"{a.frm}..{a.to} -> {a.out}  ({len(jobs)} files)")

    counts, records = {}, []
    with ThreadPoolExecutor(max_workers=a.workers) as ex:
        futs = [ex.submit(fetch_one, *j) for j in jobs]
        done = 0
        for fut in as_completed(futs):
            st, sym, interval, y, m, rec = fut.result()
            counts[st] = counts.get(st, 0) + 1
            if rec:
                records.append(rec)
            done += 1
            if st.startswith("err") or st == "checksum_mismatch":
                print(f"  {st}: {sym} {interval} {y:04d}-{m:02d}")
            if done % 200 == 0:
                print(f"  {done}/{len(jobs)} ok={counts.get('ok',0)} skip={counts.get('skip',0)} 404={counts.get('404',0)}")

    append_manifest(a.out, records)
    print("DONE", {k: v for k, v in sorted(counts.items())},
          f"manifest+={len(records)} -> {os.path.join(a.out, 'spot/manifest.jsonl')}")


if __name__ == "__main__":
    main()
