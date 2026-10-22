using System.Globalization;
using System.Text;

namespace PdfDesk.Core;

/// <summary>
/// The text layer of one page: a flat string (lines joined by '\n') with a box per UTF-16 unit, so selection,
/// copy and search work on plain string indices. A "caret" is a position between characters (0..Text.Length).
/// </summary>
public sealed class PageText
{
    private readonly RectD[] _boxes;
    private readonly int[] _lineOf;
    private readonly (int Start, int End, RectD Box)[] _lines;

    public static PageText Empty { get; } = new(new List<(string, RectD)>[0]);

    public string Text { get; }
    public bool IsEmpty => Text.Length == 0;

    /// <param name="lines">Each line is a list of (character string, box) in reading order.</param>
    public PageText(IReadOnlyList<List<(string Ch, RectD Box)>> lines)
    {
        var sb = new StringBuilder();
        var boxes = new List<RectD>();
        var lineOf = new List<int>();
        var lineSpans = new List<(int, int, RectD)>();

        foreach (var line in lines)
        {
            if (line.Count == 0) continue;
            if (sb.Length > 0)
            {
                // Synthetic line break: zero-width box at the end of the previous line.
                var prev = boxes[^1];
                sb.Append('\n');
                boxes.Add(new RectD(prev.Right, prev.Y, 0, prev.Height));
                lineOf.Add(lineSpans.Count - 1);
            }

            var start = sb.Length;
            var lineBox = default(RectD);
            foreach (var (ch, box) in line)
            {
                foreach (var unit in ch)
                {
                    sb.Append(unit);
                    boxes.Add(box);
                    lineOf.Add(lineSpans.Count);
                }
                lineBox = lineBox.Union(box);
            }
            lineSpans.Add((start, sb.Length, lineBox));
        }

        Text = sb.ToString();
        _boxes = [.. boxes];
        _lineOf = [.. lineOf];
        _lines = [.. lineSpans];
    }

    /// <summary>Nearest caret position to a point in page space (always succeeds when the page has text).</summary>
    public int HitTest(double x, double y)
    {
        if (_lines.Length == 0) return 0;

        // Pick the line whose band contains y, else the nearest line by vertical distance (then horizontal).
        var best = 0;
        var bestScore = double.MaxValue;
        for (var i = 0; i < _lines.Length; i++)
        {
            var b = _lines[i].Box;
            var dy = y < b.Y ? b.Y - y : y > b.Bottom ? y - b.Bottom : 0;
            var dx = x < b.X ? b.X - x : x > b.Right ? x - b.Right : 0;
            var score = dy * 1000 + dx;
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        var (start, end, _) = _lines[best];
        for (var i = start; i < end; i++)
            if (x < _boxes[i].CenterX) return i;
        return end;
    }

    /// <summary>True when the point is over a character box (used for the I-beam cursor).</summary>
    public bool IsOverText(double x, double y)
    {
        foreach (var (_, _, box) in _lines)
            if (box.Inflate(1).Contains(x, y))
                return true;
        return false;
    }

    public string GetString(int start, int end)
    {
        (start, end) = Order(start, end);
        return Text[start..end];
    }

    /// <summary>One rectangle per line covering the characters in [start, end).</summary>
    public List<RectD> GetRects(int start, int end)
    {
        (start, end) = Order(start, end);
        var rects = new List<RectD>();
        var i = start;
        while (i < end)
        {
            var line = _lineOf[i];
            var r = default(RectD);
            while (i < end && _lineOf[i] == line)
            {
                if (Text[i] != '\n') r = r.Union(_boxes[i]);
                i++;
            }
            if (!r.IsEmpty) rects.Add(r);
        }
        return rects;
    }

    /// <summary>Expands a caret to the surrounding word (for double-click).</summary>
    public (int Start, int End) WordAt(int caret)
    {
        if (Text.Length == 0) return (0, 0);
        var i = Math.Clamp(caret, 0, Text.Length - 1);
        if (!IsWordChar(Text[i]) && i > 0 && IsWordChar(Text[i - 1])) i--;
        if (!IsWordChar(Text[i])) return (i, i + 1);
        var s = i;
        var e = i;
        while (s > 0 && IsWordChar(Text[s - 1])) s--;
        while (e < Text.Length && IsWordChar(Text[e])) e++;
        return (s, e);
    }

    /// <summary>Expands a caret to its whole line (for triple-click).</summary>
    public (int Start, int End) LineAt(int caret)
    {
        if (_lines.Length == 0) return (0, 0);
        var line = _lineOf[Math.Clamp(caret, 0, _lineOf.Length - 1)];
        return (_lines[line].Start, _lines[line].End);
    }

    /// <summary>Finds every occurrence of <paramref name="query"/>; whitespace in the query matches any run of whitespace, including line breaks.</summary>
    public List<(int Start, int End)> Find(string query, bool matchCase, bool wholeWord)
    {
        var hits = new List<(int, int)>();
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0 || Text.Length == 0) return hits;

        var cmp = matchCase ? CompareOptions.None : CompareOptions.IgnoreCase;
        var ci = CultureInfo.InvariantCulture.CompareInfo;
        var pos = 0;
        while (pos < Text.Length)
        {
            var first = ci.IndexOf(Text, terms[0], pos, cmp);
            if (first < 0) break;

            var end = first + terms[0].Length;
            var ok = true;
            for (var t = 1; t < terms.Length && ok; t++)
            {
                var ws = end;
                while (ws < Text.Length && char.IsWhiteSpace(Text[ws])) ws++;
                if (ws == end || ws + terms[t].Length > Text.Length || ci.Compare(Text, ws, terms[t].Length, terms[t], 0, terms[t].Length, cmp) != 0)
                    ok = false;
                else
                    end = ws + terms[t].Length;
            }

            if (ok && wholeWord && ((first > 0 && IsWordChar(Text[first - 1])) || (end < Text.Length && IsWordChar(Text[end]))))
                ok = false;

            if (ok)
            {
                hits.Add((first, end));
                pos = end;
            }
            else
            {
                pos = first + 1;
            }
        }
        return hits;
    }

    /// <summary>Short excerpt around a match for the search results list.</summary>
    public string Excerpt(int start, int end, int context = 40)
    {
        var s = Math.Max(0, start - context);
        var e = Math.Min(Text.Length, end + context);
        var text = (s > 0 ? "…" : "") + Text[s..e] + (e < Text.Length ? "…" : "");
        return text.Replace('\n', ' ');
    }

    private (int, int) Order(int a, int b)
    {
        a = Math.Clamp(a, 0, Text.Length);
        b = Math.Clamp(b, 0, Text.Length);
        return a <= b ? (a, b) : (b, a);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'' or '’';
}
