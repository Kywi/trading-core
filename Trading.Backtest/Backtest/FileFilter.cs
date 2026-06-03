using System.Collections.Generic;
using System.IO;

namespace GripTrader.Core.Backtest
{
    /// <summary>
    /// Helpers for restricting a sorted list of historical-data CSV files
    /// (aggTrades or klines) to a YYYY-MM window. Used by the walk-forward
    /// harness so train and test runs replay disjoint subsets of the same
    /// data folder without copying files around.
    /// </summary>
    public static class FileFilter
    {
        /// <summary>
        /// Keep only the files whose filename contains a YYYY-MM token in
        /// the inclusive range <paramref name="fromYearMonth"/> ..
        /// <paramref name="toYearMonth"/>. Either bound may be null/empty
        /// to leave that side unbounded. Files lacking a parseable YYYY-MM
        /// token are silently dropped — the file collectors only ever
        /// produce monthly-archive names so this is safe in practice.
        /// </summary>
        public static List<string> FilterByYearMonth(
            IReadOnlyList<string> files, string? fromYearMonth, string? toYearMonth)
        {
            var result = new List<string>(files.Count);
            foreach (var f in files)
            {
                var ym = ExtractYearMonth(f);
                if (ym == null) continue;
                if (!string.IsNullOrWhiteSpace(fromYearMonth)
                    && string.CompareOrdinal(ym, fromYearMonth) < 0)
                    continue;
                if (!string.IsNullOrWhiteSpace(toYearMonth)
                    && string.CompareOrdinal(ym, toYearMonth) > 0)
                    continue;
                result.Add(f);
            }
            return result;
        }

        /// <summary>
        /// Pull the first <c>YYYY-MM</c> substring out of a filename. Works
        /// for both <c>BNBUSDT-aggTrades-2024-03.csv</c> and
        /// <c>BNBUSDT-1h-2024-03.csv</c>; daily archives like
        /// <c>BNBUSDT-1h-2024-03-15.csv</c> still collapse to the
        /// <c>2024-03</c> bucket which is the right behaviour for monthly
        /// inclusion bounds.
        /// </summary>
        public static string? ExtractYearMonth(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            // Walk left-to-right; first '-'-bounded YYYY-MM wins.
            for (int i = 0; i + 7 <= name.Length; i++)
            {
                if (i > 0 && name[i - 1] != '-') continue;
                if (!IsDigit(name, i, 4)) continue;
                if (name[i + 4] != '-') continue;
                if (!IsDigit(name, i + 5, 2)) continue;
                int mm = (name[i + 5] - '0') * 10 + (name[i + 6] - '0');
                if (mm < 1 || mm > 12) continue;
                return name.Substring(i, 7);
            }
            return null;
        }

        private static bool IsDigit(string s, int start, int len)
        {
            if (start + len > s.Length) return false;
            for (int i = 0; i < len; i++)
                if (s[start + i] < '0' || s[start + i] > '9')
                    return false;
            return true;
        }
    }
}
