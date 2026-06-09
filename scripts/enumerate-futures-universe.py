"""
Enumerate the USDⓈ-M (um) perpetual universe straight from the data.binance.vision archive —
the SURVIVORSHIP-BIAS-FREE source. Do NOT build the point-in-time universe from the live
/fapi/v1/exchangeInfo (it lists only currently-trading symbols; delisted names vanish — the
survivorship trap). Delisted perps RETAIN full archives bounded by their trading lifespan, so
the archive's klines/ symbol prefixes ARE the historical roster, and each symbol's first/last
available month gives its listing/delisting window.

The data.binance.vision `?prefix=` URL serves a JS HTML viewer, so we query the backing S3
bucket directly (verified working): https://s3-ap-northeast-1.amazonaws.com/data.binance.vision

Usage:
    python enumerate-futures-universe.py                       # list all USDT perps -> universe.json
    python enumerate-futures-universe.py --quote USDT --out u.json
    python enumerate-futures-universe.py --spans BTCUSDT SRMUSDT SOLUSDT   # first/last month per symbol

Requires Python 3.9+ (stdlib only).
"""
import argparse, json, re, sys, urllib.request, urllib.error
from urllib.parse import quote as urlquote

S3 = "https://s3-ap-northeast-1.amazonaws.com/data.binance.vision"
KLINES_PREFIX = "data/futures/um/monthly/klines/"

_PREFIX_RE = re.compile(r"<Prefix>([^<]+)</Prefix>")
_KEY_RE = re.compile(r"<Key>([^<]+)</Key>")
_TRUNC_RE = re.compile(r"<IsTruncated>([^<]+)</IsTruncated>")
_MONTH_RE = re.compile(r"-(\d{4}-\d{2})\.zip$")


def _fetch(prefix, delimiter, marker):
    url = f"{S3}?list-type=2&prefix={urlquote(prefix)}"
    if delimiter:
        url += f"&delimiter={urlquote(delimiter)}"
    if marker:
        url += f"&start-after={urlquote(marker)}"
    with urllib.request.urlopen(url, timeout=60) as r:
        return r.read().decode("utf-8", "replace")


def list_common_prefixes(prefix):
    """All immediate sub-folder names under `prefix` (e.g. symbol dirs under klines/)."""
    out, marker = [], None
    while True:
        xml = _fetch(prefix, "/", marker)
        # CommonPrefixes come back as <Prefix>...child/</Prefix>; the top-level <Prefix> echo
        # equals the request prefix — filter it out.
        page = [p for p in _PREFIX_RE.findall(xml) if p != prefix]
        out.extend(page)
        if (_TRUNC_RE.findall(xml) or ["false"])[0].lower() != "true" or not page:
            break
        marker = page[-1]  # list-type=2 start-after continuation
    return out


def list_keys(prefix):
    out, marker = [], None
    while True:
        xml = _fetch(prefix, None, marker)
        page = _KEY_RE.findall(xml)
        out.extend(page)
        if (_TRUNC_RE.findall(xml) or ["false"])[0].lower() != "true" or not page:
            break
        marker = page[-1]
    return out


def all_symbols(quote):
    prefixes = list_common_prefixes(KLINES_PREFIX)
    syms = []
    for p in prefixes:
        # p like "data/futures/um/monthly/klines/BTCUSDT/"
        seg = p[len(KLINES_PREFIX):].strip("/")
        if seg and (not quote or seg.endswith(quote)):
            syms.append(seg)
    return sorted(set(syms))


def symbol_span(sym, interval="1h"):
    """First and last available month for a symbol's klines (its listing/delisting window)."""
    keys = list_keys(f"{KLINES_PREFIX}{sym}/{interval}/")
    months = sorted({m.group(1) for k in keys for m in [_MONTH_RE.search(k)] if m})
    return (months[0], months[-1]) if months else (None, None)


def main():
    ap = argparse.ArgumentParser(description="Enumerate the USDⓈ-M perp universe from the archive (no survivorship).")
    ap.add_argument("--quote", default="USDT", help="quote filter (empty = all)")
    ap.add_argument("--out", default="futures-universe.json", help="output JSON for the full symbol list")
    ap.add_argument("--interval", default="1h", help="interval used for span probing")
    ap.add_argument("--spans", nargs="*", help="report first/last month for these symbols instead of listing all")
    a = ap.parse_args()

    if a.spans:
        latest = None
        rows = []
        for sym in [s.upper() for s in a.spans]:
            first, last = symbol_span(sym, a.interval)
            rows.append((sym, first, last))
            if last and (latest is None or last > latest):
                latest = last
        print(f"{'symbol':16} {'first':9} {'last':9} status")
        for sym, first, last in rows:
            if first is None:
                status = "NOT FOUND"
            elif last and latest and last < latest:
                status = f"likely DELISTED (archive ends {last}, others run to {latest})"
            else:
                status = "active / current"
            print(f"{sym:16} {first or '-':9} {last or '-':9} {status}")
        return

    syms = all_symbols(a.quote)
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump({"quote": a.quote, "count": len(syms), "symbols": syms}, f, indent=1)
    print(f"{len(syms)} {a.quote or 'all'} perp symbols in the archive (incl. delisted) -> {a.out}")
    print("sample:", ", ".join(syms[:12]), "...")
    print("Pick the economically-linked, liquid subset from this roster; use --spans to get each "
          "symbol's listing/delisting window. This roster is survivorship-bias-free (it includes "
          "delisted names absent from the live exchangeInfo).")


if __name__ == "__main__":
    main()
