using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>A link annotation: where it is (page space) and where it leads (a URI, or a page of this document).</summary>
/// <param name="Id">The annotation's /NM, else its position ("p&lt;page&gt;:&lt;index&gt;", valid for the bytes it was read from).</param>
/// <param name="TargetPage">The page a link inside the document goes to (0-based); -1 for a URI or an action Inkvolt PDF doesn't edit.</param>
/// <param name="Uri">The address of a URI link; null otherwise.</param>
public sealed record LinkInfo(string Id, int PageIndex, RectD Rect, string? Uri, int TargetPage)
{
    /// <summary>What it leads to, for lists and tooltips.</summary>
    public string Describe() => Uri ?? (TargetPage >= 0 ? $"Page {TargetPage + 1}" : "Another action");
}

/// <summary>
/// Link annotations made and edited with the Link tool: a rectangle on a page leading to a web or email address (a URI
/// action) or to a page of this document (a destination). Links are invisible (no border); viewers show a hand over
/// them. Opening a link is the viewer's business: Inkvolt PDF asks before opening anything but http, https and mailto.
/// </summary>
public static class Links
{
    /// <summary>Schemes opened without asking (the same as the page view's link safety prompt).</summary>
    private static readonly string[] SafeSchemes = ["http", "https", "mailto"];

    /// <summary>Schemes never written into a link.</summary>
    private static readonly string[] BlockedSchemes = ["javascript", "vbscript", "data"];

    public static IReadOnlyList<LinkInfo> Read(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import, "This PDF's links can't be read");
        var list = new List<LinkInfo>();
        for (var p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            if (PdfObj.Array(page.Elements["/Annots"]) is not { } annots) continue;
            for (var i = 0; i < annots.Elements.Count; i++)
            {
                if (PdfObj.Dict(annots.Elements[i]) is not { } a || PdfObj.NameOf(a, "/Subtype") != "/Link") continue;
                if (PdfObj.Array(a.Elements["/Rect"]) is not { Elements.Count: >= 4 } r) continue;
                var (uri, target) = Target(doc, a);
                list.Add(new LinkInfo(Annotations.IdOf(a, p, i), p, PdfObj.RectToPage(page, r), uri, target));
            }
        }
        return list;
    }

    private static (string? Uri, int Page) Target(PdfDocument doc, PdfDictionary link)
    {
        var dest = link.Elements["/Dest"];
        if (PdfObj.Dict(link.Elements["/A"]) is { } a)
        {
            switch (PdfObj.Name(a.Elements["/S"]))
            {
                case "URI":
                    return (PdfObj.Str(a.Elements["/URI"]), -1);
                case "GoTo":
                    dest = a.Elements["/D"];
                    break;
                default:
                    return (null, -1);
            }
        }
        return (null, dest is not null && DocumentFeatures.ResolveDestination(doc, dest) is { } d ? d.PageIndex : -1);
    }

    /// <summary>
    /// Adds a link (or, with <paramref name="replaceId"/>, moves it and changes where it leads) over
    /// <paramref name="rect"/> (page space): to <paramref name="uri"/> when given, else to page <paramref name="targetPage"/>.
    /// </summary>
    public static byte[] Put(byte[] data, string? password, int pageIndex, RectD rect, string? uri, int targetPage, string? replaceId = null) =>
        Put(data, password, pageIndex, rect, uri, targetPage, replaceId, null);

    /// <summary>
    /// Makes the text in <paramref name="lineRects"/> (one box per line, page space) a link: one link over all of them
    /// whose /QuadPoints are the lines, so viewers that read them react to the text only.
    /// </summary>
    public static byte[] PutOnText(byte[] data, string? password, int pageIndex, IReadOnlyList<RectD> lineRects, string? uri, int targetPage)
    {
        var lines = lineRects.Where(r => !r.IsEmpty).ToList();
        if (lines.Count == 0) throw new ArgumentException("No text to link.", nameof(lineRects));
        var all = RectD.FromEdges(lines.Min(r => r.X), lines.Min(r => r.Y), lines.Max(r => r.Right), lines.Max(r => r.Bottom));
        return Put(data, password, pageIndex, all, uri, targetPage, null, lines);
    }

    private static byte[] Put(byte[] data, string? password, int pageIndex, RectD rect, string? uri, int targetPage, string? replaceId, List<RectD>? lines)
    {
        if (rect.Width < 1 || rect.Height < 1) throw new ArgumentException("The link needs a size.");
        if (uri is not null) uri = Normalize(uri) ?? throw new ArgumentException("That isn't a web or email address.");
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        if (pageIndex < 0 || pageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (uri is null && (targetPage < 0 || targetPage >= doc.PageCount)) throw new ArgumentException($"Choose a page from 1 to {doc.PageCount}.");

        PdfDictionary annot;
        var page = doc.Pages[pageIndex];
        if (replaceId is not null)
        {
            annot = Relocate(doc, replaceId, pageIndex);
            annot.Elements.Remove("/A");
            annot.Elements.Remove("/Dest");
            annot.Elements.Remove("/QuadPoints");
        }
        else
        {
            annot = new PdfDictionary(doc);
            annot.Elements["/Type"] = new PdfName("/Annot");
            annot.Elements["/Subtype"] = new PdfName("/Link");
            annot.Elements["/NM"] = new PdfString(Annotations.NewId());
            annot.Elements["/F"] = new PdfInteger(4); // print
            annot.Elements["/Border"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
            annot.Elements["/H"] = new PdfName("/I");
            annot.Elements["/P"] = page.Reference!;
            doc.Internals.AddObject(annot);
            PdfObj.Annots(doc, page).Elements.Add(annot.Reference!);
        }
        var (x1, y1, x2, y2) = TextStamps.FromPageSpace(page, rect);
        annot.Elements["/Rect"] = PdfObj.Reals(doc, x1, y1, x2, y2);
        annot.Elements["/M"] = PdfObj.DateString(DateTimeOffset.Now);
        if (lines is not null)
        {
            // Corners in the reading orientation of the page as shown, as for text markup: upper-left, upper-right,
            // lower-left, lower-right.
            var qp = new List<double>();
            foreach (var r in lines)
                foreach (var (px, py) in new[] { (r.X, r.Y), (r.Right, r.Y), (r.X, r.Bottom), (r.Right, r.Bottom) })
                {
                    var (qx, qy) = PdfObj.ToPdf(page, px, py);
                    qp.Add(qx);
                    qp.Add(qy);
                }
            annot.Elements["/QuadPoints"] = PdfObj.Reals(doc, [.. qp]);
        }
        if (uri is not null)
        {
            var action = new PdfDictionary(doc);
            action.Elements["/Type"] = new PdfName("/Action");
            action.Elements["/S"] = new PdfName("/URI");
            action.Elements["/URI"] = new PdfString(uri);
            annot.Elements["/A"] = action;
        }
        else
        {
            // The top of the target page as shown, keeping the zoom.
            var target = doc.Pages[targetPage];
            var (ux, uy) = PdfObj.ToPdf(target, 0, 0);
            var sideways = ((target.Rotate % 360) + 360) % 360 is 90 or 270;
            annot.Elements["/Dest"] = new PdfArray(doc, target.Reference!, new PdfName("/XYZ"),
                sideways ? new PdfReal(Math.Round(ux, 2)) : PdfNull.Value, sideways ? PdfNull.Value : new PdfReal(Math.Round(uy, 2)), PdfNull.Value);
        }
        return PdfObj.Save(doc);
    }

    /// <summary>Moves or resizes a link (onto another page too), keeping where it leads, whatever its action.</summary>
    public static byte[] Move(byte[] data, string? password, string id, int pageIndex, RectD rect)
    {
        if (rect.Width < 1 || rect.Height < 1) throw new ArgumentException("The link needs a size.");
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        if (pageIndex < 0 || pageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var annot = Relocate(doc, id, pageIndex);
        var (x1, y1, x2, y2) = TextStamps.FromPageSpace(doc.Pages[pageIndex], rect);
        annot.Elements["/Rect"] = PdfObj.Reals(doc, x1, y1, x2, y2);
        annot.Elements["/M"] = PdfObj.DateString(DateTimeOffset.Now);
        // Highlighting quads (/QuadPoints) would still mark the old place.
        annot.Elements.Remove("/QuadPoints");
        return PdfObj.Save(doc);
    }

    /// <summary>The link <paramref name="id"/>, moved into page <paramref name="pageIndex"/>'s annotations if it was on another page.</summary>
    private static PdfDictionary Relocate(PdfDocument doc, string id, int pageIndex)
    {
        var (oldPage, link) = Annotations.Find(doc, id) ?? throw new InvalidOperationException("That link is no longer in the document.");
        if (PdfObj.NameOf(link, "/Subtype") != "/Link") throw new InvalidOperationException("That isn't a link.");
        if (oldPage == pageIndex) return link;
        var from = PdfObj.Annots(doc, doc.Pages[oldPage]);
        for (var i = from.Elements.Count - 1; i >= 0; i--)
            if (ReferenceEquals(PdfObj.Dict(from.Elements[i]), link)) from.Elements.RemoveAt(i);
        var page = doc.Pages[pageIndex];
        PdfObj.Annots(doc, page).Elements.Add(link.Reference is { } r ? r : link);
        link.Elements["/P"] = page.Reference!;
        return link;
    }

    /// <summary>Deletes a link.</summary>
    public static byte[] Delete(byte[] data, string? password, string id)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        if (Annotations.Find(doc, id) is not { } found || PdfObj.NameOf(found.Annot, "/Subtype") != "/Link")
            throw new InvalidOperationException("That link is no longer in the document.");
        Annotations.DeleteIn(doc, id);
        return PdfObj.Save(doc);
    }

    /// <summary>
    /// The address as it goes into a link, or null when it can't be one: "example.com/x" and "www.example.com" become
    /// https, "name@example.com" becomes mailto; absolute addresses stay as typed (international domains as punycode,
    /// other non-ASCII characters percent-encoded), except script and data addresses, which are refused.
    /// </summary>
    public static string? Normalize(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Any(char.IsWhiteSpace) && !t.Contains(':')) return null;
        if (!t.Contains(':') && t.Contains('@') && !t.Contains('/') && t.IndexOf('@') > 0 && t.IndexOf('.', t.IndexOf('@')) > t.IndexOf('@') + 1) t = "mailto:" + t;
        else if (!t.Contains("://", StringComparison.Ordinal) && !t.Contains(':') && t.Contains('.')) t = "https://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || u.IsFile && !t.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        if (BlockedSchemes.Contains(u.Scheme, StringComparer.OrdinalIgnoreCase)) return null;
        // A link's /URI is 7-bit ASCII: international domain names go in as punycode, other characters percent-encoded
        // (UTF-8); what still isn't plain ASCII is refused.
        string ascii;
        try
        {
            ascii = u.Scheme is "mailto" ? AsciiMailto(t) : AsciiUri(u);
        }
        catch (ArgumentException)
        {
            return null;
        }
        return ascii.All(c => c is > ' ' and < (char)0x7F) ? ascii : null;
    }

    private static string AsciiUri(Uri u)
    {
        if (u.IsAbsoluteUri && u.HostNameType == UriHostNameType.Dns && !u.Host.All(char.IsAscii))
            u = new UriBuilder(u) { Host = u.IdnHost }.Uri;
        return PercentEncode(u.AbsoluteUri);
    }

    /// <summary>mailto:local@domain?query: the domain as punycode, everything else that isn't ASCII percent-encoded.</summary>
    private static string AsciiMailto(string t)
    {
        var body = t[(t.IndexOf(':') + 1)..];
        var q = body.IndexOf('?');
        var (to, query) = q < 0 ? (body, "") : (body[..q], body[q..]);
        var addresses = to.Split(',').Select(a =>
        {
            var at = a.LastIndexOf('@');
            if (at < 0 || a[(at + 1)..].All(char.IsAscii)) return PercentEncode(a);
            return PercentEncode(a[..at]) + "@" + new System.Globalization.IdnMapping().GetAscii(Uri.UnescapeDataString(a[(at + 1)..]));
        });
        return "mailto:" + string.Join(',', addresses) + PercentEncode(query);
    }

    /// <summary>Percent-encodes (as UTF-8) every character that isn't printable ASCII, leaving the rest as it is.</summary>
    private static string PercentEncode(string s)
    {
        if (s.All(c => c is > ' ' and < (char)0x7F)) return s;
        var sb = new System.Text.StringBuilder();
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in s.EnumerateRunes())
        {
            if (rune.Value is > ' ' and < 0x7F)
            {
                sb.Append((char)rune.Value);
                continue;
            }
            var n = rune.EncodeToUtf8(utf8);
            foreach (var b in utf8[..n]) sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>True for addresses viewers open without asking (http, https, mailto).</summary>
    public static bool IsSafe(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && SafeSchemes.Contains(u.Scheme, StringComparer.OrdinalIgnoreCase);
}
