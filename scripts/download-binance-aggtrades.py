"""
Download Binance spot monthly AGGTRADES CSVs from data.binance.vision into the layout
the Trading.Backtest aggTrades feeder expects:

    <out>/<Base>Usdt/aggTrades/<SYM>-aggTrades-YYYY-MM.csv
    e.g. D:/data/BchUsdt/aggTrades/BCHUSDT-aggTrades-2025-01.csv

Tick-level data — much larger than klines (tens of MB to several GB per month), so
the zip is streamed to a temp file and unzipped, never held whole in RAM. Use
aggTrades only for execution-sensitive validation (trailing-stop / slippage) over
short windows; use klines for multi-year sweeps. 2025+ archives are
microsecond-stamped — Trading.Backtest normalizes that.

Examples:
    python download-binance-aggtrades.py BCH --from 2025-01 --to 2025-03
    python download-binance-aggtrades.py --out D:/data --from 2024-10 --to 2024-12 BNB SOL
"""
import argparse, os, shutil, sys, tempfile, zipfile, urllib.request, urllib.error

BASE_URL = "https://data.binance.vision/data/spot/monthly/aggTrades"


def parse_ym(s):
    y, m = s.split("-"); return int(y), int(m)


def months(start, end):
    y, m = start
    while (y, m) <= end:
        yield y, m
        m += 1
        if m > 12:
            m, y = 1, y + 1


def folder_for(base, quote):
    return base.capitalize() + quote.capitalize()


def fetch_one(base, quote, y, m, out_root):
    sym = base + quote
    d = os.path.join(out_root, folder_for(base, quote), "aggTrades")
    csv_path = os.path.join(d, f"{sym}-aggTrades-{y:04d}-{m:02d}.csv")
    if os.path.exists(csv_path):
        return "skip", csv_path, 0
    url = f"{BASE_URL}/{sym}/{sym}-aggTrades-{y:04d}-{m:02d}.zip"
    os.makedirs(d, exist_ok=True)
    tmp_zip = csv_path + ".zip.tmp"
    try:
        with urllib.request.urlopen(url, timeout=120) as r, open(tmp_zip, "wb") as f:
            shutil.copyfileobj(r, f, length=1 << 20)  # stream 1 MB chunks to disk
    except urllib.error.HTTPError as e:
        if os.path.exists(tmp_zip):
            os.remove(tmp_zip)
        return ("404" if e.code == 404 else f"err{e.code}"), csv_path, 0
    except Exception as e:
        if os.path.exists(tmp_zip):
            os.remove(tmp_zip)
        return "err:" + type(e).__name__, csv_path, 0
    with zipfile.ZipFile(tmp_zip) as z:
        with z.open(z.namelist()[0]) as src, open(csv_path, "wb") as out:
            shutil.copyfileobj(src, out, length=1 << 20)
    os.remove(tmp_zip)
    return "ok", csv_path, os.path.getsize(csv_path)


def main():
    ap = argparse.ArgumentParser(description="Download Binance spot monthly aggTrades CSVs (large).")
    ap.add_argument("symbols", nargs="+", help="base assets, e.g. BCH XRP")
    ap.add_argument("--out", default=os.environ.get("BINANCE_DATA_DIR", "D:/GridTrader"))
    ap.add_argument("--quote", default="USDT")
    ap.add_argument("--from", dest="frm", required=True, help="start YYYY-MM")
    ap.add_argument("--to", required=True, help="end YYYY-MM (inclusive)")
    a = ap.parse_args()

    symbols = [s.upper() for s in a.symbols]
    start, end = parse_ym(a.frm), parse_ym(a.to)
    jobs = [(b, a.quote, y, m) for b in symbols for (y, m) in months(start, end)]
    print(f"aggTrades {symbols} {a.frm}..{a.to} -> {a.out}  ({len(jobs)} months) — sequential, can be large")

    counts, total = {}, 0
    # Sequential on purpose: aggTrades months can be multi-GB; parallel downloads would
    # thrash disk/bandwidth. One at a time, streamed.
    for j in jobs:
        st, path, size = fetch_one(*j, a.out)
        counts[st] = counts.get(st, 0) + 1
        total += size
        mb = f"{size // 1024 // 1024} MB" if size else ""
        print(f"  {st:6} {os.path.basename(path)} {mb}")
    print("DONE", {k: v for k, v in sorted(counts.items())}, f"~{total // 1024 // 1024} MB written")


if __name__ == "__main__":
    main()
