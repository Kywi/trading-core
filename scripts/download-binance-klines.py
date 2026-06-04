"""
Download Binance spot monthly KLINE CSVs from data.binance.vision into the layout
the Trading.Backtest kline feeder expects:

    <out>/<Base>Usdt/<interval>klines/<SYM>-<interval>-YYYY-MM.csv
    e.g. D:/data/BnbUsdt/4hklines/BNBUSDT-4h-2024-03.csv

PowerShell-free, concurrent, skips files already present, tolerates not-yet-listed
months (HTTP 404). 2025+ archives are microsecond-stamped — Trading.Backtest
normalizes that (HistoricalTrendManager + feeders), so no special handling here.

Examples:
    python download-binance-klines.py BNB SOL XRP
    python download-binance-klines.py --out D:/data --intervals 1h,4h --from 2019-01 --to 2026-02 BNB
    BINANCE_DATA_DIR=D:/data python download-binance-klines.py            # uses the default symbol set
"""
import argparse, io, os, sys, zipfile, urllib.request, urllib.error
from concurrent.futures import ThreadPoolExecutor, as_completed

BASE_URL = "https://data.binance.vision/data/spot/monthly/klines"
DEFAULT_SYMBOLS = ["BTC", "ETH", "BNB", "SOL", "XRP", "ADA", "DOGE", "AVAX", "LINK", "LTC"]


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


def folder_for(base, quote):  # XRP -> XrpUsdt (matches the BnbUsdt/BtcUsdt convention)
    return base.capitalize() + quote.capitalize()


def fetch_one(base, quote, interval, y, m, out_root):
    sym = base + quote
    d = os.path.join(out_root, folder_for(base, quote), interval + "klines")
    csv_path = os.path.join(d, f"{sym}-{interval}-{y:04d}-{m:02d}.csv")
    if os.path.exists(csv_path):
        return "skip"
    url = f"{BASE_URL}/{sym}/{interval}/{sym}-{interval}-{y:04d}-{m:02d}.zip"
    try:
        with urllib.request.urlopen(url, timeout=30) as r:
            data = r.read()
    except urllib.error.HTTPError as e:
        return "404" if e.code == 404 else f"err{e.code}"
    except Exception as e:
        return "err:" + type(e).__name__
    os.makedirs(d, exist_ok=True)
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        with z.open(z.namelist()[0]) as f, open(csv_path, "wb") as out:
            out.write(f.read())
    return "ok"


def main():
    ap = argparse.ArgumentParser(description="Download Binance spot monthly kline CSVs.")
    ap.add_argument("symbols", nargs="*", help="base assets, e.g. BNB SOL (default: a liquid set)")
    ap.add_argument("--out", default=os.environ.get("BINANCE_DATA_DIR", "D:/GridTrader"), help="output root dir")
    ap.add_argument("--quote", default="USDT")
    ap.add_argument("--intervals", default="1h,4h", help="comma list, e.g. 1h,4h,1d")
    ap.add_argument("--from", dest="frm", default="2019-01", help="start YYYY-MM")
    ap.add_argument("--to", default="2026-02", help="end YYYY-MM (inclusive)")
    ap.add_argument("--workers", type=int, default=16)
    a = ap.parse_args()

    symbols = [s.upper() for s in (a.symbols or DEFAULT_SYMBOLS)]
    intervals = [i.strip() for i in a.intervals.split(",") if i.strip()]
    start, end = parse_ym(a.frm), parse_ym(a.to)
    jobs = [(b, a.quote, iv, y, m, a.out) for b in symbols for iv in intervals for (y, m) in months(start, end)]
    print(f"Symbols={symbols} intervals={intervals} {a.frm}..{a.to} -> {a.out}  ({len(jobs)} files)")

    counts = {}
    with ThreadPoolExecutor(max_workers=a.workers) as ex:
        futs = [ex.submit(fetch_one, *j) for j in jobs]
        done = 0
        for fut in as_completed(futs):
            st = fut.result()
            counts[st] = counts.get(st, 0) + 1
            done += 1
            if done % 200 == 0:
                print(f"  {done}/{len(jobs)} ok={counts.get('ok',0)} skip={counts.get('skip',0)} 404={counts.get('404',0)}")
    print("DONE", {k: v for k, v in sorted(counts.items())})


if __name__ == "__main__":
    main()
