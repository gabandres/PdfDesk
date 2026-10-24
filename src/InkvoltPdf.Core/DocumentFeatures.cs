using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>Which panel the author wants open (/PageMode). Null in <see cref="ViewerPrefs"/> = not specified.</summary>
public enum PageModeKind { None, Outlines, Thumbs, FullScreen, Attachments, Layers }

/// <summary>/PageLayout: SinglePage = one page at a time; OneColumn = continuous; TwoColumn* = continuous two-up; TwoPage* = two-up page at a time. *Right = odd pages on the right (cover).</summary>
public enum PageLayoutKind { SinglePage, OneColumn, TwoColumnLeft, TwoColumnRight, TwoPageLeft, TwoPageRight }

/// <summary>How a destination positions the page: XYZ = point + zoom; Fit = whole page; FitH = page width at Y; FitV = page height at X; FitR = rectangle; FitB* = bounding box of the content.</summary>
public enum ZoomKind { XYZ, Fit, FitH, FitV, FitR, FitB, FitBH, FitBV }

/// <summary>A destination inside the document (page space: points from the displayed page's top-left).</summary>
/// <param name="Zoom">XYZ zoom factor (1 = 100%); null = keep the current zoom.</param>
public sealed record PageDestination(int PageIndex, ZoomKind Kind, double? X, double? Y, double? Zoom);

/// <summary>How the author asked the document to open.</summary>
/// <param name="OpenAction">The /OpenAction destination (GoTo or explicit destination), if any.</param>
/// <param name="DisplayDocTitle">Show the document title (not the file name) in the window/tab caption.</param>
public sealed record ViewerPrefs(PageModeKind? PageMode, PageLayoutKind? PageLayout, PageDestination? OpenAction, bool DisplayDocTitle);

/// <summary>Document-level navigation features: page labels and opening preferences.</summary>
public static class DocumentFeatures
{
    /// <summary>
    /// Each page's label from the /PageLabels number tree (e.g. "i", "ii", "1", "A-1"), or null when the file has none.
    /// </summary>
    public static string[]? PageLabels(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import);
        var tree = PdfObj.Dict(doc.Internals.Catalog.Elements["/PageLabels"]);
        if (tree is null) return null;
        var ranges = PdfObj.NumberTree(tree).Where(e => e.Key >= 0).OrderBy(e => e.Key).ToList();
        if (ranges.Count == 0) return null;
        var labels = new string[doc.PageCount];
        for (var i = 0; i < labels.Length; i++)
        {
            var idx = ranges.FindLastIndex(r => r.Key <= i);
            if (idx < 0)
            {
                labels[i] = (i + 1).ToString(PdfObj.Inv);
                continue;
            }
            var (start, value) = ranges[idx];
            var d = PdfObj.Dict(value);
            var style = PdfObj.Name(d?.Elements["/S"]);
            var prefix = PdfObj.Str(d?.Elements["/P"]) ?? "";
            // A hostile /St (or start key) must not overflow, nor make Roman/letter labels millions of characters long.
            var first = Math.Clamp(PdfObj.Int(d?.Elements["/St"]) ?? 1, 1, 1_000_000_000);
            var n = (int)Math.Clamp((long)first + i - start, int.MinValue, int.MaxValue);
            labels[i] = prefix + FormatNumber(n, style);
        }
        return labels;
    }

    /// <summary>Formats a page number in a /PageLabels style (D, R, r, A, a; null = no number).</summary>
    public static string FormatNumber(int n, string? style) => style switch
    {
        "D" => n.ToString(PdfObj.Inv),
        "R" => Roman(n),
        "r" => Roman(n).ToLowerInvariant(),
        "A" => Letters(n),
        "a" => Letters(n).ToLowerInvariant(),
        _ => "",
    };

    /// <summary>The page whose label is <paramref name="text"/> (exact, then case-insensitive), or null.</summary>
    public static int? FindPageByLabel(IReadOnlyList<string>? labels, string text)
    {
        if (labels is null) return null;
        text = text.Trim();
        if (text.Length == 0) return null;
        for (var i = 0; i < labels.Count; i++)
            if (labels[i] == text) return i;
        for (var i = 0; i < labels.Count; i++)
            if (string.Equals(labels[i].Trim(), text, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    private static string Roman(int n)
    {
        if (n <= 0 || n > 39_999) return n.ToString(PdfObj.Inv); // beyond that a Roman numeral is just a wall of M's
        var sb = new StringBuilder();
        foreach (var (v, s) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
            while (n >= v)
            {
                sb.Append(s);
                n -= v;
            }
        return sb.ToString();
    }

    /// <summary>A..Z, then AA..ZZ, AAA.. (the PDF convention: the letter repeated).</summary>
    private static string Letters(int n)
    {
        if (n <= 0 || n > 26 * 100) return n.ToString(PdfObj.Inv);
        var letter = (char)('A' + (n - 1) % 26);
        return new string(letter, (n - 1) / 26 + 1);
    }

    /// <summary>The document's opening preferences (page mode, layout, open action, title display).</summary>
    public static ViewerPrefs ReadViewerPrefs(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import);
        var catalog = doc.Internals.Catalog;
        PageModeKind? mode = PdfObj.Name(catalog.Elements["/PageMode"]) switch
        {
            "UseNone" => PageModeKind.None,
            "UseOutlines" => PageModeKind.Outlines,
            "UseThumbs" => PageModeKind.Thumbs,
            "FullScreen" => PageModeKind.FullScreen,
            "UseOC" => PageModeKind.Layers,
            "UseAttachments" => PageModeKind.Attachments,
            _ => null,
        };
        PageLayoutKind? layout = Enum.TryParse<PageLayoutKind>(PdfObj.Name(catalog.Elements["/PageLayout"]), out var l) ? l : null;
        var prefs = PdfObj.Dict(catalog.Elements["/ViewerPreferences"]);
        var displayTitle = PdfObj.Resolve(prefs?.Elements["/DisplayDocTitle"]) is PdfBoolean { Value: true };

        PageDestination? open = null;
        var action = PdfObj.Resolve(catalog.Elements["/OpenAction"]);
        if (action is PdfArray) open = ResolveDestination(doc, action);
        else if (action is PdfDictionary a && PdfObj.Name(a.Elements["/S"]) == "GoTo") open = ResolveDestination(doc, a.Elements["/D"]);
        return new ViewerPrefs(mode, layout, open, displayTitle);
    }

    /// <summary>Resolves an explicit or named destination to a page and view.</summary>
    internal static PageDestination? ResolveDestination(PdfDocument doc, PdfItem? dest, int depth = 0)
    {
        if (depth > 8) return null;
        switch (PdfObj.Resolve(dest))
        {
            case PdfArray arr when arr.Elements.Count > 0:
            {
                var page = PdfObj.PageIndexOf(doc, arr.Elements[0]);
                if (page < 0) return null;
                var p = doc.Pages[page];
                var kind = Enum.TryParse<ZoomKind>(arr.Elements.Count > 1 ? PdfObj.Name(arr.Elements[1]) : null, out var k) ? k : ZoomKind.Fit;
                double? Arg(int i) => i < arr.Elements.Count && PdfObj.Resolve(arr.Elements[i]) is PdfInteger or PdfReal ? PdfObj.Num(arr.Elements[i]) : null;
                double? x = null, y = null, zoom = null;
                switch (kind)
                {
                    case ZoomKind.XYZ:
                        (x, y) = Point(p, Arg(2), Arg(3));
                        zoom = Arg(4) is { } z && z > 0 ? z : null;
                        break;
                    case ZoomKind.FitH or ZoomKind.FitBH:
                        (x, y) = Point(p, null, Arg(2));
                        break;
                    case ZoomKind.FitV or ZoomKind.FitBV:
                        (x, y) = Point(p, Arg(2), null);
                        break;
                    case ZoomKind.FitR:
                        (x, y) = Point(p, Arg(2), Arg(5));
                        break;
                }
                return new PageDestination(page, kind, x, y, zoom);
            }
            case PdfDictionary d when d.Elements.ContainsKey("/D"):
                return ResolveDestination(doc, d.Elements["/D"], depth + 1);
            case PdfName n:
                return ResolveDestination(doc, PdfObj.Dict(doc.Internals.Catalog.Elements["/Dests"])?.Elements[n.Value], depth + 1);
            case PdfString s:
            {
                var tree = PdfObj.Dict(PdfObj.Dict(doc.Internals.Catalog.Elements["/Names"])?.Elements["/Dests"]);
                var hit = PdfObj.NameTree(tree).FirstOrDefault(e => e.Key == s.Value);
                return hit.Key is null ? null : ResolveDestination(doc, hit.Value, depth + 1);
            }
            default:
                return null;
        }
    }

    /// <summary>A destination point (PDF user space, either coordinate optional) in page space.</summary>
    private static (double? X, double? Y) Point(PdfPage page, double? left, double? top)
    {
        if (left is null && top is null) return (null, null);
        var (bx1, _, _, by2) = PdfObj.VisibleBox(page);
        var (px, py) = PdfObj.ToPage(page, left ?? bx1, top ?? by2);
        var rot = ((page.Rotate % 360) + 360) % 360;
        // On a page shown sideways the PDF's "top" becomes a horizontal position; report what's meaningful for scrolling.
        return rot is 90 or 270 ? (top is null ? null : px, left is null ? null : py) : (left is null ? null : px, top is null ? null : py);
    }
}
