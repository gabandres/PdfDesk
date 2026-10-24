using System.Text;
using PdfSharp.Pdf;

namespace InkvoltPdf.Core;

/// <summary>
/// Content Inkvolt PDF adds to pages (headers and footers, watermarks): real page content, so it prints in every viewer,
/// yet removable again. Each is a form XObject drawn by content streams of its own; the XObject and those streams carry
/// a private key (<see cref="Key"/>, e.g. /InkvoltPdfMark /HeaderFooter) by which they are found later. Drawn over the
/// page, the page's own content is wrapped in q/Q (a "q" stream first, "Q" and the drawing last) so its graphics state
/// can't leak into the drawing; drawn behind, the drawing is one self-contained stream before the page's own. Wrappers
/// of different kinds nest, so either kind can be removed without the other. The drawing is a pagination artifact
/// (marked content), so screen readers and reflow skip it.
/// </summary>
internal static class PageMarks
{
    public const string Key = "/InkvoltPdfMark";

    /// <summary>
    /// The page as shown (rotation and crop applied): its size, and the /Matrix that maps a form drawn in displayed-page
    /// coordinates (origin at the bottom-left of the page as shown, y up) into the page's user space.
    /// </summary>
    public static (double W, double H, PdfArray Matrix) Frame(PdfDocument doc, PdfPage page)
    {
        var (w, h) = PdfObj.DisplaySize(page);
        return (w, h, PdfObj.Reals(doc, DisplayMatrix(page)));
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
        page.Elements["/Contents"] = keep;
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
