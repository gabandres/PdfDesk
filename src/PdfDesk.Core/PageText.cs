using System.Globalization;
using System.Text;

namespace PdfDesk.Core;

/// <summary>
/// The text layer of one page: a flat string (lines joined by '\n') with a box per UTF-16 unit, so selection,
/// copy and search work on plain string indices. A "caret" is a position between characters (0..Text.Length).
/// </summary>
public sealed class PageText
{
    /// <summary>
    /// A line: its characters [Start, End), its box, its baseline direction (unit vector; (1, 0) for normal text,
    /// vertical for text turned sideways) and the box of its column (the run of lines stacked with it in reading order:
    /// a paragraph or a stretch of a text column), grown by a line's thickness so the gaps between lines count as inside.
    /// </summary>
    private readonly record struct Line(int Start, int End, RectD Box, double Dx, double Dy, RectD Column);

    private const double Eps = 0.01;

    private readonly RectD[] _boxes;
    private readonly int[] _lineOf;
    private readonly Line[] _lines;

    public static PageText Empty { get; } = new(new List<(string, RectD)>[0]);

    public string Text { get; }
    public bool IsEmpty => Text.Length == 0;

    /// <param name="lines">Each line is a list of (character string, box) in reading order.</param>
    /// <param name="directions">
    /// Per line (same indices as <paramref name="lines"/>): its baseline direction, e.g. (1, 0) for normal text and
    /// (0, 1) for text turned clockwise. Missing or zero: taken from where the line's characters sit.
    /// </param>
    public PageText(IReadOnlyList<List<(string Ch, RectD Box)>> lines, IReadOnlyList<(double X, double Y)>? directions = null)
    {
        var sb = new StringBuilder();
        var boxes = new List<RectD>();
        var lineOf = new List<int>();
        var spans = new List<Line>();

        for (var li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            if (line.Count == 0) continue;
            if (sb.Length > 0)
            {
                // Synthetic line break: zero-width box at the end of the previous line.
                var prev = boxes[^1];
                sb.Append('\n');
                boxes.Add(new RectD(prev.Right, prev.Y, 0, prev.Height));
                lineOf.Add(spans.Count - 1);
            }

            var start = sb.Length;
            var lineBox = default(RectD);
            foreach (var (ch, box) in line)
            {
                foreach (var unit in ch)
                {
                    sb.Append(unit);
                    boxes.Add(box);
                    lineOf.Add(spans.Count);
                }
                lineBox = lineBox.Union(box);
            }
            var (dx, dy) = Direction(line, directions is not null && li < directions.Count ? directions[li] : default);
            spans.Add(new Line(start, sb.Length, lineBox, dx, dy, lineBox));
        }

        Text = sb.ToString();
        _boxes = [.. boxes];
        _lineOf = [.. lineOf];
        _lines = [.. spans];
        FindColumns(_lines);
    }

    /// <summary>A line's baseline direction as a unit vector: the given one, else the axis its characters run along.</summary>
    private static (double, double) Direction(List<(string Ch, RectD Box)> line, (double X, double Y) given)
    {
        var len = Math.Sqrt(given.X * given.X + given.Y * given.Y);
        if (len > 1e-6 && double.IsFinite(len)) return (given.X / len, given.Y / len);
        if (line.Count < 2) return (1, 0);
        var vx = line[^1].Box.CenterX - line[0].Box.CenterX;
        var vy = line[^1].Box.CenterY - line[0].Box.CenterY;
        return Math.Abs(vy) > Math.Abs(vx) ? (0, 1) : (1, 0);
    }

    /// <summary>
    /// Groups lines that follow each other in reading order, run the same way, overlap along it and sit at most
    /// 1.5 lines apart into columns, and gives each line its column's box.
    /// </summary>
    private static void FindColumns(Line[] lines)
    {
        var first = 0;
        for (var i = 1; i <= lines.Length; i++)
        {
            if (i < lines.Length && SameColumn(lines[i - 1], lines[i])) continue;
            var box = default(RectD);
            var thick = 0.0;
            for (var j = first; j < i; j++)
            {
                box = box.Union(lines[j].Box);
                var (a, b) = Span(lines[j].Box, -lines[j].Dy, lines[j].Dx);
                thick = Math.Max(thick, b - a);
            }
            box = box.Inflate(thick);
            for (var j = first; j < i; j++) lines[j] = lines[j] with { Column = box };
            first = i;
        }

        static bool SameColumn(Line a, Line b)
        {
            if (Math.Abs(a.Dx * b.Dx + a.Dy * b.Dy) < 0.9) return false;
            var (a0, a1) = Span(a.Box, a.Dx, a.Dy);
            var (b0, b1) = Span(b.Box, a.Dx, a.Dy);
            if (b0 > a1 || b1 < a0) return false;
            var (c0, c1) = Span(a.Box, -a.Dy, a.Dx);
            var (d0, d1) = Span(b.Box, -a.Dy, a.Dx);
            var gap = Math.Max(d0 - c1, c0 - d1);
            return gap <= 1.5 * Math.Max(c1 - c0, d1 - d0);
        }
    }

    /// <summary>A box's extent along the direction (ux, uy): the smallest and largest projection of its corners.</summary>
    private static (double Min, double Max) Span(RectD b, double ux, double uy)
    {
        var p = b.X * ux + b.Y * uy;
        var w = b.Width * ux;
        var h = b.Height * uy;
        return (p + Math.Min(0, w) + Math.Min(0, h), p + Math.Max(0, w) + Math.Max(0, h));
    }

    private static double Distance(double v, (double Min, double Max) span) =>
        v < span.Min ? span.Min - v : v > span.Max ? v - span.Max : 0;

    /// <summary>Nearest caret position to a point in page space (always succeeds when the page has text).</summary>
    public int HitTest(double x, double y) => _lines.Length == 0 ? 0 : CaretInLine(PickLine(x, y), x, y);

    /// <summary>
    /// The line a point belongs to: the nearest across the text direction (then along it), preferring lines whose
    /// column contains the point. So between two lines of the left column the point stays there, even where a line of
    /// the right column is level with it; beside a heading or a paragraph's short last line it takes that line.
    /// </summary>
    private int PickLine(double x, double y)
    {
        int best = -1, bestAny = 0;
        double bestScore = double.MaxValue, bestAnyScore = double.MaxValue;
        for (var i = 0; i < _lines.Length; i++)
        {
            var l = _lines[i];
            var along = Distance(x * l.Dx + y * l.Dy, Span(l.Box, l.Dx, l.Dy));
            var across = Distance(-x * l.Dy + y * l.Dx, Span(l.Box, -l.Dy, l.Dx));
            var score = across * 1000 + along;
            if (score < bestAnyScore)
            {
                bestAnyScore = score;
                bestAny = i;
            }
            if (score < bestScore && l.Column.Contains(x, y))
            {
                bestScore = score;
                best = i;
            }
        }
        return best >= 0 ? best : bestAny;
    }

    /// <summary>
    /// The caret in the line holding <paramref name="caretOnLine"/> nearest to a point (for moving the caret between
    /// lines): measured along the line's own direction, so it works for sideways and right-to-left text too.
    /// </summary>
    public int NearestCaretInLine(int caretOnLine, double x, double y) =>
        _lines.Length == 0 ? 0 : CaretInLine(_lineOf.Length == 0 ? 0 : _lineOf[Math.Clamp(caretOnLine, 0, _lineOf.Length - 1)], x, y);

    /// <summary>
    /// The caret in a line nearest to a point: the point is projected on the line's baseline direction and lands
    /// before or after the nearest character, whichever half it is in. Characters running against the direction
    /// (right-to-left text stored in reading order) count from their far side.
    /// </summary>
    private int CaretInLine(int line, double x, double y)
    {
        var l = _lines[line];
        var p = x * l.Dx + y * l.Dy;
        var best = -1;
        double bestDist = double.MaxValue, bestOff = double.MaxValue;
        for (var i = l.Start; i < l.End; i = Next(i, l.End))
        {
            var span = Span(_boxes[i], l.Dx, l.Dy);
            var dist = Distance(p, span);
            var off = Math.Abs(p - (span.Min + span.Max) / 2);
            if (dist < bestDist - Eps || (Math.Abs(dist - bestDist) <= Eps && off < bestOff))
            {
                bestDist = dist;
                bestOff = off;
                best = i;
            }
        }
        if (best < 0) return l.Start;
        var before = p < Center(best, l);
        return before != IsReversed(best, l) ? best : Next(best, l.End);
    }

    private double Center(int i, Line l)
    {
        var (a, b) = Span(_boxes[i], l.Dx, l.Dy);
        return (a + b) / 2;
    }

    /// <summary>The index after the character at <paramref name="i"/> (a surrogate pair is one character).</summary>
    private int Next(int i, int end) => i + 1 < end && char.IsHighSurrogate(Text[i]) && char.IsLowSurrogate(Text[i + 1]) ? i + 2 : i + 1;

    private int Previous(int i, int start) => i - 2 >= start && char.IsLowSurrogate(Text[i - 1]) && char.IsHighSurrogate(Text[i - 2]) ? i - 2 : i - 1;

    /// <summary>
    /// True when the character runs against the line's direction: the next character in reading order sits before it
    /// (or the previous one after it), as in a right-to-left run; a lone character goes by its script.
    /// </summary>
    private bool IsReversed(int i, Line l)
    {
        var c = Center(i, l);
        var next = Next(i, l.End);
        if (next < l.End)
        {
            var n = Center(next, l);
            if (n < c - Eps) return true;
            if (n > c + Eps) return false;
        }
        if (i > l.Start)
        {
            var p = Center(Previous(i, l.Start), l);
            if (p > c + Eps) return true;
            if (p < c - Eps) return false;
        }
        return IsRightToLeft(char.IsSurrogatePair(Text, i) ? char.ConvertToUtf32(Text, i) : Text[i]);
    }

    /// <summary>Letters of right-to-left scripts (Hebrew, Arabic, Syriac, Thaana, N'Ko… and their presentation forms).</summary>
    internal static bool IsRightToLeft(int cp) =>
        cp is >= 0x0590 and <= 0x08FF or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF or >= 0x10800 and <= 0x10FFF or >= 0x1E800 and <= 0x1EFFF;

    /// <summary>True when the point is over a character box (used for the I-beam cursor).</summary>
    public bool IsOverText(double x, double y)
    {
        foreach (var line in _lines)
            if (line.Box.Inflate(1).Contains(x, y))
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

    /// <summary>
    /// Finds every occurrence of <paramref name="query"/>; whitespace in the query matches any run of whitespace,
    /// including line breaks, and words hyphenated across a line break ("exam-" / "ple") match unhyphenated.
    /// Match lengths come from the comparer, so ligatures and ignorable characters highlight exactly.
    /// </summary>
    /// <param name="ignoreAccents">Treat "é" and "e" alike (diacritics are ignored).</param>
    public List<(int Start, int End)> Find(string query, bool matchCase, bool wholeWord, bool ignoreAccents = false)
    {
        var hits = new List<(int, int)>();
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0 || Text.Length == 0) return hits;

        var view = _searchView ??= SearchView.For(Text);
        var text = view.Text;
        var cmp = (matchCase ? CompareOptions.None : CompareOptions.IgnoreCase) | (ignoreAccents ? CompareOptions.IgnoreNonSpace : CompareOptions.None);
        var ci = CultureInfo.InvariantCulture.CompareInfo;
        var pos = 0;
        while (pos < text.Length)
        {
            var rel = ci.IndexOf(text.AsSpan(pos), terms[0], cmp, out var length);
            if (rel < 0 || length == 0) break; // nothing, or a query made only of ignorable characters

            var first = pos + rel;
            var end = first + length;
            var ok = true;
            for (var t = 1; t < terms.Length && ok; t++)
            {
                var ws = end;
                while (ws < text.Length && char.IsWhiteSpace(text[ws])) ws++;
                if (ws == end || !ci.IsPrefix(text.AsSpan(ws), terms[t], cmp, out var termLength) || termLength == 0)
                    ok = false;
                else
                    end = ws + termLength;
            }

            if (ok && wholeWord && ((first > 0 && IsWordChar(text[first - 1])) || (end < text.Length && IsWordChar(text[end]))))
                ok = false;

            if (ok)
            {
                hits.Add(view.ToText(first, end));
                pos = end;
            }
            else
            {
                pos = first + 1;
            }
        }
        return hits;
    }

    private SearchView? _searchView;

    /// <summary>
    /// The text as search sees it: a hyphen at a line break between two letters ("exam-\nple") and soft hyphens
    /// (U+00AD, with a line break after them) are dropped, so the word reads whole. Map holds each character's index in
    /// <see cref="Text"/> (null when nothing was dropped).
    /// </summary>
    private sealed class SearchView(string text, int[]? map)
    {
        public string Text { get; } = text;

        public static SearchView For(string text)
        {
            if (text.IndexOf('­') < 0 && text.IndexOf("-\n", StringComparison.Ordinal) < 0) return new(text, null);
            var sb = new StringBuilder(text.Length);
            var map = new List<int>(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '­' || (c == '-' && i > 0 && char.IsLetter(text[i - 1]) && i + 2 < text.Length && text[i + 1] == '\n' && char.IsLetter(text[i + 2])))
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    continue;
                }
                sb.Append(c);
                map.Add(i);
            }
            return new(sb.ToString(), [.. map]);
        }

        /// <summary>A [start, end) range of this view as a range of the page text.</summary>
        public (int, int) ToText(int start, int end) =>
            map is null ? (start, end) : (map[start], end > start ? map[end - 1] + 1 : map[start]);
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
