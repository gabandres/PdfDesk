using System.Text;
using PdfSharp.Pdf;

namespace InkvoltPdf.Core;

/// <summary>
/// Content Inkvolt PDF adds to pages (headers and footers, watermarks): real page content, so it prints in every viewer,
/// yet removable again. Each is a form XObject drawn by content streams of its own; the XObject and those streams carry
/// a private key (<see cref="Key"/>, e.g. /InkvoltPdfMark /HeaderFooter) by which they are found later. Drawn over the
/// page, the page's own content is wrapped in q/Q (a "q" stream first, "Q" and the drawing last) so its graphics state
/// can't leak into the drawing; drawn behind, the drawing is one self-contained stream before the page's own. Wrappers
/// of different kinds nest, so either kind can be removed without the other. Page content that leaves q/Q unbalanced
/// (an unclosed q, or a Q with nothing to restore) is first wrapped in a balancing pair of its own (/Balance), so a mark
/// drawn over it starts from the page's own coordinates. The drawing is a pagination artifact (marked content), so
/// screen readers and reflow skip it.
/// </summary>
internal static class PageMarks
{
    public const string Key = "/InkvoltPdfMark";

    /// <summary>
    /// The page as shown (rotation and crop applied): its size, and the /Matrix that maps a form drawn in displayed-page
    /// coordinates (origin at the bottom-left of the page as shown, y up) into the page's user space.
    /// </summary>
    /// <remarks>
    /// Sizes are in points: on a page with a /UserUnit (one unit is that many points) the form is drawn in points and
    /// scaled into the page's units, so margins, font sizes and tiles are the same on paper as on any other page.
    /// </remarks>
    public static (double W, double H, PdfArray Matrix) Frame(PdfDocument doc, PdfPage page)
    {
        var (w, h) = PdfObj.DisplaySize(page);
        var u = UserUnit(page);
        var m = DisplayMatrix(page);
        return (w * u, h * u, PdfObj.Reals(doc, m[0] / u, m[1] / u, m[2] / u, m[3] / u, m[4], m[5]));
    }

    /// <summary>The page's /UserUnit (points per unit; 1 when absent or unusable).</summary>
    internal static double UserUnit(PdfPage page)
    {
        var u = PdfObj.Num(page.Elements["/UserUnit"], 1);
        return double.IsFinite(u) && u is >= 0.001 and <= 75000 ? u : 1;
    }

    /// <summary>[a b c d e f]: displayed-page coordinates (bottom-left origin, y up) to user space.</summary>
    internal static double[] DisplayMatrix(PdfPage page)
    {
        var (_, h) = PdfObj.DisplaySize(page);
        // Page space has its origin top-left with y down: displayed (x, y) is page space (x, h - y). The map is affine,
        // so three points give it.
        var o = PdfObj.ToPdf(page, 0, h);
        var x = PdfObj.ToPdf(page, 1, h);
        var y = PdfObj.ToPdf(page, 0, h - 1);
        return [x.X - o.X, x.Y - o.Y, y.X - o.X, y.Y - o.Y, o.X, o.Y];
    }

    /// <summary>Draws <paramref name="form"/> on <paramref name="page"/> as a mark of <paramref name="kind"/> (over or behind the content).</summary>
    /// <param name="artifact">The artifact's property list, e.g. "&lt;&lt;/Type /Pagination /Subtype /Watermark&gt;&gt;".</param>
    public static void Add(PdfDocument doc, PdfPage page, PdfDictionary form, string kind, bool behind, string artifact)
    {
        form.Elements[Key] = new PdfName("/" + kind);
        var xobjects = OwnXObjects(doc, page);
        var i = 0;
        while (xobjects.Elements.ContainsKey($"/Iv{kind}{i}")) i++;
        var name = $"/Iv{kind}{i}";
        xobjects.Elements[name] = form.Reference!;

        var draw = $"/Artifact {artifact} BDC q {name} Do Q EMC\n";
        if (!behind) Balance(doc, page);
        var contents = new PdfArray(doc);
        if (behind) contents.Elements.Add(Stream(doc, kind, "q\n" + draw + "Q\n"));
        else contents.Elements.Add(Stream(doc, kind, "q\n"));
        var existing = page.Elements["/Contents"];
        if (PdfObj.Array(existing) is { } arr) foreach (var e in arr.Elements) contents.Elements.Add(e);
        else if (existing is PdfSharp.Pdf.Advanced.PdfReference) contents.Elements.Add(existing);
        if (!behind) contents.Elements.Add(Stream(doc, kind, "Q\n" + draw));
        page.Elements["/Contents"] = contents;
    }

    /// <summary>Takes the marks of <paramref name="kind"/> off the page (its streams and XObjects). False when it had none.</summary>
    public static bool Remove(PdfDocument doc, PdfPage page, string kind)
    {
        if (PdfObj.Array(page.Elements["/Contents"]) is not { } arr) return false;
        var keep = new PdfArray(doc);
        var removed = false;
        foreach (var e in arr.Elements)
        {
            if (Is(PdfObj.Dict(e), kind)) removed = true;
            else keep.Elements.Add(e);
        }
        if (!removed) return false;
        // The balancing pair goes with the last mark: the page is left as it was.
        if (!keep.Elements.Any(e => PdfObj.Dict(e) is { } d && d.Elements.ContainsKey(Key) && !Is(d, BalanceKind)))
            for (var i = keep.Elements.Count - 1; i >= 0; i--)
                if (Is(PdfObj.Dict(keep.Elements[i]), BalanceKind)) keep.Elements.RemoveAt(i);
        page.Elements["/Contents"] = keep;
        SignedEdits.NoteRemoval();
        // Add gave the page its own XObject dictionary, so these entries are this page's.
        if (PdfObj.Dict(PdfObj.Dict(page.Elements["/Resources"])?.Elements["/XObject"]) is { } xobjects)
            foreach (var key in xobjects.Elements.Keys.ToList())
                if (Is(PdfObj.Dict(xobjects.Elements[key]), kind)) xobjects.Elements.Remove(key);
        return true;
    }

    /// <summary>True when any page has a mark of <paramref name="kind"/>.</summary>
    public static bool Has(PdfDocument doc, string kind)
    {
        foreach (var page in doc.Pages)
            if (PdfObj.Array(page.Elements["/Contents"])?.Elements.Any(e => Is(PdfObj.Dict(e), kind)) == true) return true;
        return false;
    }

    private const string BalanceKind = "Balance";

    /// <summary>
    /// Wraps the page's own content (the streams that aren't marks) in a balancing pair when its q/Q don't match: a "q"
    /// stream before it for each Q that has nothing to restore, and a "Q" stream after it closing everything still open.
    /// Afterwards the content leaves the graphics state as it found it, so a mark drawn after it lands where it should.
    /// </summary>
    private static void Balance(PdfDocument doc, PdfPage page)
    {
        var existing = page.Elements["/Contents"];
        List<PdfItem> items = PdfObj.Array(existing) is { } arr ? [.. arr.Elements] : existing is PdfSharp.Pdf.Advanced.PdfReference ? [existing] : [];
        // The page's own streams (and an earlier balancing pair), in order: the q/Q of marks balance themselves.
        var own = items.Select((e, i) => (Dict: PdfObj.Dict(e), Index: i))
            .Where(x => x.Dict is not null && (!x.Dict.Elements.ContainsKey(Key) || Is(x.Dict, BalanceKind))).ToList();
        if (own.Count == 0) return;
        var (depth, lowest) = SaveDepth(own.Select(x => PdfObj.StreamBytes(x.Dict!)));
        if (depth == 0 && lowest == 0) return;
        var open = -lowest;
        var list = new PdfArray(doc);
        for (var i = 0; i < items.Count; i++)
        {
            if (i == own[0].Index && open > 0) list.Elements.Add(Stream(doc, BalanceKind, string.Concat(Enumerable.Repeat("q ", open)) + "\n"));
            list.Elements.Add(items[i]);
            if (i == own[^1].Index && open + depth > 0) list.Elements.Add(Stream(doc, BalanceKind, "\n" + string.Concat(Enumerable.Repeat("Q ", open + depth)) + "\n"));
        }
        page.Elements["/Contents"] = list;
    }

    /// <summary>
    /// The q/Q nesting of content streams read in a row: where it ends (q minus Q) and the lowest it gets (0, or less when
    /// a Q comes with nothing to restore). Strings, comments and inline image data are skipped.
    /// </summary>
    internal static (int End, int Lowest) SaveDepth(IEnumerable<byte[]> streams)
    {
        int depth = 0, lowest = 0;
        foreach (var s in streams)
        {
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (IsSpace(c)) i++;
                else if (c == '%')
                    while (i < s.Length && s[i] is not ((byte)'\r' or (byte)'\n')) i++;
                else if (c == '(')
                {
                    // A literal string: balanced parentheses, backslash escapes.
                    var nest = 0;
                    for (; i < s.Length; i++)
                    {
                        if (s[i] == '\\') i++;
                        else if (s[i] == '(') nest++;
                        else if (s[i] == ')' && --nest == 0) break;
                    }
                    i++;
                }
                else if (c == '<' && i + 1 < s.Length && s[i + 1] != '<')
                {
                    // A hex string.
                    while (i < s.Length && s[i] != '>') i++;
                    i++;
                }
                else if (c == '/')
                {
                    // A name (/Q is a resource name, not an operator).
                    i++;
                    while (i < s.Length && !IsSpace(s[i]) && !IsDelimiter(s[i])) i++;
                }
                else if (IsDelimiter(c)) i++;
                else
                {
                    var start = i++;
                    while (i < s.Length && !IsSpace(s[i]) && !IsDelimiter(s[i])) i++;
                    var len = i - start;
                    if (len == 1 && c == 'q') depth++;
                    else if (len == 1 && c == 'Q') lowest = Math.Min(lowest, --depth);
                    else if (len == 2 && c == 'B' && s[start + 1] == 'I') i = AfterInlineImage(s, i);
                }
            }
        }
        return (depth, lowest);
    }

    private static bool IsSpace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>Past an inline image's data: the "EI" (between white space) after its "ID".</summary>
    private static int AfterInlineImage(byte[] s, int i)
    {
        for (; i + 2 < s.Length; i++)
            if (s[i] == 'I' && s[i + 1] == 'D' && IsSpace(s[i - 1]) && IsSpace(s[i + 2]))
            {
                i += 3;
                break;
            }
        for (; i + 1 < s.Length; i++)
            if (s[i] == 'E' && s[i + 1] == 'I' && IsSpace(s[i - 1]) && (i + 2 == s.Length || IsSpace(s[i + 2]) || IsDelimiter(s[i + 2])))
                return i + 2;
        return s.Length;
    }

    private static bool Is(PdfDictionary? d, string kind) => d is not null && PdfObj.NameOf(d, Key) == "/" + kind;

    private static PdfSharp.Pdf.Advanced.PdfReference Stream(PdfDocument doc, string kind, string ops)
    {
        var s = PdfObj.AddStream(doc, Encoding.ASCII.GetBytes(ops));
        s.Elements[Key] = new PdfName("/" + kind);
        return s.Reference!;
    }

    /// <summary>
    /// The page's XObject dictionary, made its own first (resources, and their XObject dictionary, may be shared between
    /// pages: an entry added for one page must not show up in, or be removed from, another).
    /// </summary>
    private static PdfDictionary OwnXObjects(PdfDocument doc, PdfPage page)
    {
        var resources = Copy(doc, PdfObj.Dict(page.Elements["/Resources"]));
        page.Elements["/Resources"] = resources;
        var xobjects = Copy(doc, PdfObj.Dict(resources.Elements["/XObject"]));
        resources.Elements["/XObject"] = xobjects;
        return xobjects;
    }

    private static PdfDictionary Copy(PdfDocument doc, PdfDictionary? from)
    {
        var d = new PdfDictionary(doc);
        if (from is not null)
            foreach (var key in from.Elements.Keys) d.Elements[key] = from.Elements[key];
        return d;
    }
}
