using System.Text;
using MuPDFCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>
/// True redaction: every page with areas to redact is rendered (with its annotations and form fields) to a bitmap,
/// the areas are painted solid black in the pixels, and the page's content is replaced by that image. Nothing from the
/// original page (text, vector art, images, annotations, form widgets, thumbnails, alternate text in the structure
/// tree) survives, so the redacted content can't be recovered or copied. Redacted pages become images: their text is
/// no longer selectable or searchable. A form field an area touches is emptied on every page (its value would otherwise
/// survive in its other widgets). Tagged PDFs lose the redacted page's part of the structure tree. Other pages and the
/// document metadata are left untouched.
/// </summary>
public static class Redaction
{
    /// <param name="areasByPage">Areas to black out per page index, in page space (points, displayed page's top-left).</param>
    /// <param name="dpi">Resolution of the replacement page images.</param>
    public static byte[] Apply(byte[] data, string? password, IReadOnlyDictionary<int, IReadOnlyList<RectD>> areasByPage, double dpi = 300)
    {
        var pages = areasByPage.Where(kv => kv.Value.Any(r => !r.IsEmpty)).ToList();
        if (pages.Count == 0) return data;
        dpi = Math.Clamp(dpi, 36, 1200);
        data = ClearRedactedFields(data, password, pages);

        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        using var engine = PdfEngine.Open(data, InputFileTypes.PDF, password);
        foreach (var (index, areas) in pages)
        {
            if (index < 0 || index >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(areasByPage), $"There is no page {index + 1}.");
            var scale = dpi / 72;
            var img = engine.Render(index, scale);
            var rgb = new byte[img.Width * img.Height * 3];
            for (var i = 0; i < rgb.Length; i += 3)
            {
                rgb[i] = img.Pixels[i + 2];
                rgb[i + 1] = img.Pixels[i + 1];
                rgb[i + 2] = img.Pixels[i];
            }
            foreach (var a in areas.Where(r => !r.IsEmpty))
            {
                var x0 = Math.Clamp((int)Math.Floor(a.X * scale), 0, img.Width);
                var x1 = Math.Clamp((int)Math.Ceiling(a.Right * scale), 0, img.Width);
                var y0 = Math.Clamp((int)Math.Floor(a.Y * scale), 0, img.Height);
                var y1 = Math.Clamp((int)Math.Ceiling(a.Bottom * scale), 0, img.Height);
                for (var y = y0; y < y1; y++) Array.Clear(rgb, (y * img.Width + x0) * 3, (x1 - x0) * 3);
            }
            ReplacePage(doc, doc.Pages[index], img.Width, img.Height, rgb);
        }
        return PdfObj.Save(doc, rewrite: true);
    }

    /// <summary>
    /// A form field whose widget an area covers loses its value everywhere: its widgets on other pages would otherwise
    /// keep showing it (and /V would keep it in the file). Done first, so the rendered pages already show the field empty.
    /// </summary>
    private static byte[] ClearRedactedFields(byte[] data, string? password, List<KeyValuePair<int, IReadOnlyList<RectD>>> pages)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        var hit = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var (index, areas) in pages)
        {
            if (index < 0 || index >= doc.PageCount || PdfObj.Array(doc.Pages[index].Elements["/Annots"]) is not { } annots) continue;
            var page = doc.Pages[index];
            foreach (var w in annots.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>())
            {
                if (PdfObj.NameOf(w, "/Subtype") != "/Widget" || PdfObj.Array(w.Elements["/Rect"]) is not { } r) continue;
                var rect = PdfObj.RectToPage(page, r);
                if (areas.Any(a => !a.IsEmpty && !a.Intersect(rect).IsEmpty)) hit.Add(w);
            }
        }
        return hit.Count > 0 && FormEditor.ClearFields(doc, hit) ? PdfObj.Save(doc, rewrite: true) : data;
    }

    private static void ReplacePage(PdfDocument doc, PdfPage page, int width, int height, byte[] rgb)
    {
        var image = PdfObj.AddStream(doc, rgb);
        image.Elements["/Type"] = new PdfName("/XObject");
        image.Elements["/Subtype"] = new PdfName("/Image");
        image.Elements["/Width"] = new PdfInteger(width);
        image.Elements["/Height"] = new PdfInteger(height);
        image.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
        image.Elements["/BitsPerComponent"] = new PdfInteger(8);

        // The image is upright as displayed; map its unit square onto the (rotated) crop box.
        var (dw, dh) = PdfObj.DisplaySize(page);
        var o = PdfObj.ToPdf(page, 0, dh);
        var u = PdfObj.ToPdf(page, dw, dh);
        var v = PdfObj.ToPdf(page, 0, 0);
        var ops = $"q {PdfObj.F(u.X - o.X)} {PdfObj.F(u.Y - o.Y)} {PdfObj.F(v.X - o.X)} {PdfObj.F(v.Y - o.Y)} {PdfObj.F(o.X)} {PdfObj.F(o.Y)} cm /PdRedacted Do Q\n";
        var content = PdfObj.AddStream(doc, Encoding.ASCII.GetBytes(ops));
        page.Elements["/Contents"] = content.Reference!;
        var xobjects = new PdfDictionary(doc);
        xobjects.Elements["/PdRedacted"] = image.Reference!;
        var resources = new PdfDictionary(doc);
        resources.Elements["/XObject"] = xobjects;
        page.Elements["/Resources"] = resources;

        var removed = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var parentKeys = StructTree.ParentKeys(page).ToList();
        if (PdfObj.Array(page.Elements["/Annots"]) is { } annots)
        {
            var acro = PdfObj.Dict(doc.Internals.Catalog.Elements["/AcroForm"]);
            var widgets = false;
            foreach (var a in annots.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>().ToList())
            {
                removed.Add(a);
                if (PdfObj.NameOf(a, "/Subtype") != "/Widget") continue;
                RemoveWidget(acro, a, removed);
                widgets = true;
            }
            if (acro is not null)
            {
                // Fields dropped above must not stay reachable through the calculation order (with their /V)...
                if (PdfObj.Array(acro.Elements["/CO"]) is { } co)
                    for (var i = co.Elements.Count - 1; i >= 0; i--)
                        if (PdfObj.Dict(co.Elements[i]) is { } f && removed.Contains(f)) co.Elements.RemoveAt(i);
                // ...and an XFA form keeps its own copy of every value (datasets): once a field went, AcroForm fields are
                // what remains. Comments and links alone leave the form (and its XFA) as it was.
                if (widgets) acro.Elements.Remove("/XFA");
            }
            // Annotations elsewhere pointing at removed ones (a popup's /Parent, a reply's /IRT) would keep them reachable.
            foreach (var other in doc.Pages)
                if (!ReferenceEquals(other, page) && PdfObj.Array(other.Elements["/Annots"]) is { } list)
                    foreach (var a in list.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>())
                        foreach (var key in new[] { "/Parent", "/IRT" })
                            if (PdfObj.Dict(a.Elements[key]) is { } target && removed.Contains(target)) a.Elements.Remove(key);
        }
        // What else a page can carry of its old content: thumbnail image, application data (PieceInfo), separations,
        // article beads, actions, its XMP, and PDF 2.0 associated files (/AF: e.g. the source the page was made from).
        foreach (var key in new[] { "/Annots", "/Thumb", "/PieceInfo", "/StructParents", "/Group", "/B", "/Metadata", "/SeparationInfo", "/AA", "/AF" })
            page.Elements.Remove(key);
        ScrubStructure(doc, page, removed);
        StructTree.Prune(doc, [page], parentKeys, removed.Contains);
    }

    /// <summary>Takes a widget out of the field tree, dropping fields left without widgets.</summary>
    internal static void RemoveWidget(PdfDictionary? acro, PdfDictionary node, HashSet<PdfObject> removed)
    {
        for (var guard = 0; guard < 64; guard++)
        {
            var parent = PdfObj.Dict(node.Elements["/Parent"]);
            var list = parent is null ? PdfObj.Array(acro?.Elements["/Fields"]) : PdfObj.Array(parent.Elements["/Kids"]);
            if (list is null) return;
            for (var i = list.Elements.Count - 1; i >= 0; i--)
                if (ReferenceEquals(PdfObj.Dict(list.Elements[i]), node)) list.Elements.RemoveAt(i);
            if (parent is null || list.Elements.Count > 0) return;
            node = parent;
            removed.Add(node);
        }
    }

    /// <summary>
    /// Drops what the structure tree keeps of the page: alternate/actual text (it can repeat the redacted words), and
    /// references to its annotations (OBJR, which would keep a widget and its value reachable) and to marked-content
    /// streams (MCR /Stm).
    /// </summary>
    private static void ScrubStructure(PdfDocument doc, PdfPage page, HashSet<PdfObject> removed)
    {
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        void Visit(PdfItem? item, bool onPage, int depth)
        {
            if (depth > 128) return;
            switch (PdfObj.Resolve(item))
            {
                case PdfArray arr:
                    foreach (var e in arr.Elements) Visit(e, onPage, depth + 1);
                    break;
                case PdfDictionary d when seen.Add(d):
                    if (d.Elements.ContainsKey("/Pg")) onPage = ReferenceEquals(PdfObj.Resolve(d.Elements["/Pg"]), page);
                    if (onPage)
                        foreach (var key in new[] { "/Alt", "/ActualText", "/E", "/Stm", "/StmOwn" }) d.Elements.Remove(key);
                    if (PdfObj.Dict(d.Elements["/Obj"]) is { } obj && (onPage || removed.Contains(obj)))
                        d.Elements.Remove("/Obj");
                    Visit(d.Elements["/K"], onPage, depth + 1);
                    break;
            }
        }
        Visit(PdfObj.Dict(doc.Internals.Catalog.Elements["/StructTreeRoot"])?.Elements["/K"], false, 0);
    }
}
