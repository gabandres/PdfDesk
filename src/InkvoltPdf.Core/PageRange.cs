namespace InkvoltPdf.Core;

public enum PageSubset { All, Even, Odd }

/// <summary>Parses user page ranges such as "1-3, 5, 8-" or "2-end" (1-based) into sorted 0-based page indices.</summary>
public static class PageRange
{
    public static bool TryParse(string? text, int pageCount, out int[] pages, out string? error)
    {
        pages = [];
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter one or more pages, e.g. 1-3, 5, 8-";
            return false;
        }

        var set = new SortedSet<int>();
        foreach (var raw in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = raw.IndexOfAny(['-', '–', '—']); // en and em dashes come in from copied text and autocorrect
            int from, to;
            if (dash < 0)
            {
                if (!TryPage(raw, pageCount, out from)) return Fail(raw, out error);
                to = from;
            }
            else
            {
                var a = raw[..dash].Trim();
                var b = raw[(dash + 1)..].Trim();
                from = 1;
                to = pageCount;
                if (a.Length > 0 && !TryPage(a, pageCount, out from)) return Fail(raw, out error);
                if (b.Length > 0 && !TryPage(b, pageCount, out to)) return Fail(raw, out error);
            }

            if (from < 1 || to > pageCount || from > to)
            {
                error = $"\"{raw}\" is outside 1–{pageCount}.";
                return false;
            }
            for (var p = from; p <= to; p++) set.Add(p - 1);
        }

        pages = [.. set];
        return pages.Length > 0 || Fail(text, out error);
    }

    public static int[] Apply(IEnumerable<int> pages, PageSubset subset) => subset switch
    {
        // "Even" and "odd" refer to 1-based page numbers, as the user sees them.
        PageSubset.Even => [.. pages.Where(p => (p + 1) % 2 == 0)],
        PageSubset.Odd => [.. pages.Where(p => (p + 1) % 2 == 1)],
        _ => [.. pages],
    };

    /// <summary>Formats 0-based indices back to a compact 1-based range string ("1-3, 5").</summary>
    public static string Format(IEnumerable<int> pages)
    {
        var sorted = pages.Distinct().Order().ToArray();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Length;)
        {
            var j = i;
            while (j + 1 < sorted.Length && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(i == j ? $"{sorted[i] + 1}" : $"{sorted[i] + 1}-{sorted[j] + 1}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    private static bool TryPage(string s, int pageCount, out int page)
    {
        if (s.Equals("end", StringComparison.OrdinalIgnoreCase) || s.Equals("last", StringComparison.OrdinalIgnoreCase))
        {
            page = pageCount;
            return true;
        }
        return int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out page);
    }

    private static bool Fail(string raw, out string? error)
    {
        error = $"\"{raw}\" is not a valid page range.";
        return false;
    }
}
