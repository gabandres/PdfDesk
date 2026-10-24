using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>What <see cref="Flattener.Flatten"/> merges into the pages.</summary>
public sealed record FlattenOptions
{
    /// <summary>Comments and other annotations (everything but form fields and links). Their popups go too.</summary>
    public bool Annotations { get; init; } = true;

    /// <summary>Form fields: their widgets are drawn and the fields removed from the form. Signature fields stay.</summary>
    public bool FormFields { get; init; } = true;

    /// <summary>Links (removed: they have nothing to draw, usually).</summary>
    public bool Links { get; init; }

    /// <summary>Draw only annotations meant to print (/F Print flag); the others are removed without a trace.</summary>
    public bool PrintableOnly { get; init; }
}

/// <summary>
/// Flattening: each annotation's normal appearance (/AP /N, the /AS state for check boxes and radios) is drawn into
/// its page's content as a form XObject, mapped onto the annotation's /Rect the way viewers do (ISO 32000-2 12.5.5:
/// the /BBox transformed by the /Matrix, fitted to the /Rect), with its opacity (/CA) and layer (/OC); then the
/// annotation is removed. Hidden annotations (Hidden, NoView flags) are removed without being drawn. Text and choice
/// fields without an appearance (or with /NeedAppearances) get one generated first. Pages change, which no signature
/// permits, so a signed document needs the caller's consent (<see cref="SignedEdits.Run"/> with the rewrite allowed).
/// </summary>
public static class Flattener
{
    private const int FlagHidden = 2, FlagPrint = 4, FlagNoView = 32;

    /// <exception cref="SignaturesWouldBreakException">The document is signed and the rewrite wasn't allowed.</exception>
    public static byte[] Flatten(byte[] data, string? password, FlattenOptions? options = null)
    {
        options ??= new FlattenOptions();
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        var catalog = doc.Internals.Catalog;
        var acro = PdfObj.Dict(catalog.Elements["/AcroForm"]);
        var removed = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var removedKeys = new List<int>();
        var widgets = new List<PdfDictionary>();
        var pagesTouched = false;

        foreach (var page in doc.Pages)
        {
            if (PdfObj.Array(page.Elements["/Annots"]) is null) continue;
            var annots = PdfObj.Annots(doc, page); // the page's own array, if pages share one
            var take = new List<PdfDictionary>();
            foreach (var a in annots.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>())
            {
                var flatten = PdfObj.Name(a.Elements["/Subtype"]) switch
                {
                    "Widget" => options.FormFields && PdfObj.Name(FormEditor.Inherited(a, "/FT")) != "Sig",
                    "Link" => options.Links,
                    _ => options.Annotations,
                };
                if (flatten) take.Add(a);
            }
            if (take.Count == 0) continue;
            if (!pagesTouched)
            {
                SignedEdits.RequireRewrite(data, doc, "flattening changes page content, which no signature permits");
                if (options.FormFields && acro is not null) FormEditor.EnsureAppearances(doc, acro);
                pagesTouched = true;
            }

            var ops = new StringBuilder();
            foreach (var a in take)
            {
                Draw(doc, page, a, options, ops);
                removed.Add(a);
                if (PdfObj.Int(a.Elements["/StructParent"]) is { } key) removedKeys.Add(key);
                if (PdfObj.NameOf(a, "/Subtype") == "/Widget") widgets.Add(a);
            }
            for (var i = annots.Elements.Count - 1; i >= 0; i--)
                if (PdfObj.Dict(annots.Elements[i]) is { } a && removed.Contains(a)) annots.Elements.RemoveAt(i);
            if (annots.Elements.Count == 0) page.Elements.Remove("/Annots");
            if (ops.Length > 0) AppendContent(doc, page, ops.ToString());
        }
        if (!pagesTouched) return data;

        // Annotations that stay must not point at removed ones (a popup's /Parent, a reply's /IRT, a markup's /Popup).
        foreach (var page in doc.Pages)
            foreach (var a in PdfObj.Array(page.Elements["/Annots"])?.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>() ?? [])
                foreach (var key in new[] { "/Parent", "/IRT", "/Popup" })
                    if (PdfObj.Dict(a.Elements[key]) is { } target && removed.Contains(target)) a.Elements.Remove(key);

        if (widgets.Count > 0 && acro is not null) RemoveFields(catalog, acro, widgets, removed);
        if (removed.Count > 0) StructTree.Prune(doc, [], removedKeys, removed.Contains);
        doc.Info.ModificationDate = DateTime.Now;
        return PdfObj.Save(doc, rewrite: true);
    }

    /// <summary>Takes the flattened widgets out of the form; a form left without fields goes entirely.</summary>
    private static void RemoveFields(PdfDictionary catalog, PdfDictionary acro, List<PdfDictionary> widgets, HashSet<PdfObject> removed)
    {
        foreach (var w in widgets) Redaction.RemoveWidget(acro, w, removed);
        if (PdfObj.Array(acro.Elements["/CO"]) is { } co)
            for (var i = co.Elements.Count - 1; i >= 0; i--)
                if (PdfObj.Dict(co.Elements[i]) is { } f && removed.Contains(f)) co.Elements.RemoveAt(i);
        // An XFA form keeps its own copy of the values; once AcroForm fields went, the AcroForm is what remains.
        acro.Elements.Remove("/XFA");
        if (PdfObj.Array(acro.Elements["/Fields"]) is not { Elements.Count: > 0 }) catalog.Elements.Remove("/AcroForm");
    }

    /// <summary>Appends the drawing of <paramref name="annot"/>'s normal appearance to <paramref name="ops"/>, if it shows.</summary>
    private static void Draw(PdfDocument doc, PdfPage page, PdfDictionary annot, FlattenOptions options, StringBuilder ops)
    {
        var flags = PdfObj.Int(annot.Elements["/F"]) ?? 0;
        if ((flags & (FlagHidden | FlagNoView)) != 0 || (options.PrintableOnly && (flags & FlagPrint) == 0)) return;
        if (Appearance(annot) is not { } form || PdfObj.Array(annot.Elements["/Rect"]) is not { Elements.Count: >= 4 } r) return;
        if (PdfObj.Array(form.Elements["/BBox"]) is not { Elements.Count: >= 4 } bb || form.Reference is null) return;

        double rx1 = Math.Min(PdfObj.Num(r, 0), PdfObj.Num(r, 2)), ry1 = Math.Min(PdfObj.Num(r, 1), PdfObj.Num(r, 3));
        double rx2 = Math.Max(PdfObj.Num(r, 0), PdfObj.Num(r, 2)), ry2 = Math.Max(PdfObj.Num(r, 1), PdfObj.Num(r, 3));
        var (bx1, by1, bx2, by2) = TransformedBox(form, bb);
        if (bx2 - bx1 <= 1e-6 || by2 - by1 <= 1e-6 || rx2 - rx1 <= 1e-6 || ry2 - ry1 <= 1e-6) return;
        // Matrix A of 12.5.5: maps the transformed bounding box onto the rectangle.
        var sx = (rx2 - rx1) / (bx2 - bx1);
        var sy = (ry2 - ry1) / (by2 - by1);

        form.Elements["/Type"] = new PdfName("/XObject");
        form.Elements["/Subtype"] = new PdfName("/Form");
        var resources = Resources(doc, page);
        var name = Add(doc, resources, "/XObject", "Flat", form.Reference!);

        // An annotation in a layer stays in it.
        var oc = annot.Elements["/OC"] is { } ocItem && PdfObj.Dict(ocItem) is not null ? Add(doc, resources, "/Properties", "FlatOC", ocItem) : null;
        if (oc is not null) ops.Append($"/OC {oc} BDC\n");
        ops.Append("q\n");
        if (PdfObj.Resolve(annot.Elements["/CA"]) is PdfInteger or PdfReal && PdfObj.Num(annot.Elements["/CA"], 1) is var ca && ca < 1)
        {
            var gs = new PdfDictionary(doc);
            gs.Elements["/Type"] = new PdfName("/ExtGState");
            gs.Elements["/CA"] = new PdfReal(Math.Clamp(ca, 0, 1));
            gs.Elements["/ca"] = new PdfReal(Math.Clamp(ca, 0, 1));
            ops.Append($"{Add(doc, resources, "/ExtGState", "FlatGS", gs)} gs\n");
        }
        ops.Append($"{PdfObj.F(sx)} 0 0 {PdfObj.F(sy)} {PdfObj.F(rx1 - bx1 * sx)} {PdfObj.F(ry1 - by1 * sy)} cm {name} Do\nQ\n");
        if (oc is not null) ops.Append("EMC\n");
    }

    /// <summary>The normal appearance stream: /AP /N itself, or its /AS state's entry.</summary>
    private static PdfDictionary? Appearance(PdfDictionary annot)
    {
        var n = PdfObj.Dict(PdfObj.Dict(annot.Elements["/AP"])?.Elements["/N"]);
        if (n is null) return null;
        if (n.Stream is not null) return n;
        var state = PdfObj.Name(annot.Elements["/AS"]);
        return state is null ? null : PdfObj.Dict(n.Elements["/" + state]) is { Stream: not null } s ? s : null;
    }

    /// <summary>The form's /BBox transformed by its /Matrix, as an axis-aligned box.</summary>
    private static (double X1, double Y1, double X2, double Y2) TransformedBox(PdfDictionary form, PdfArray bb)
    {
        var m = PdfObj.Array(form.Elements["/Matrix"]) is { Elements.Count: >= 6 } ma
            ? Enumerable.Range(0, 6).Select(i => PdfObj.Num(ma, i)).ToArray()
            : [1, 0, 0, 1, 0, 0];
        double x1 = PdfObj.Num(bb, 0), y1 = PdfObj.Num(bb, 1), x2 = PdfObj.Num(bb, 2), y2 = PdfObj.Num(bb, 3);
        var xs = new double[4];
        var ys = new double[4];
        var k = 0;
        foreach (var (x, y) in new[] { (x1, y1), (x2, y1), (x1, y2), (x2, y2) })
        {
            xs[k] = m[0] * x + m[2] * y + m[4];
            ys[k++] = m[1] * x + m[3] * y + m[5];
        }
        return (xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }

    /// <summary>The page's own resources dictionary (created when it has none).</summary>
    private static PdfDictionary Resources(PdfDocument doc, PdfPage page)
    {
        if (PdfObj.Dict(page.Elements["/Resources"]) is { } res) return res;
        res = new PdfDictionary(doc);
        page.Elements["/Resources"] = res;
        return res;
    }

    /// <summary>Adds <paramref name="value"/> to a resource category under a fresh name ("/Flat0", ...), which it returns.</summary>
    private static string Add(PdfDocument doc, PdfDictionary resources, string category, string prefix, PdfItem value)
    {
        var dict = PdfObj.Dict(resources.Elements[category]);
        if (dict is null)
        {
            dict = new PdfDictionary(doc);
            resources.Elements[category] = dict;
        }
        var i = 0;
        while (dict.Elements.ContainsKey($"/{prefix}{i}")) i++;
        var name = $"/{prefix}{i}";
        dict.Elements[name] = value;
        return name;
    }

    /// <summary>Appends <paramref name="ops"/> after the page's content, which is wrapped in q/Q so its graphics state can't leak.</summary>
    private static void AppendContent(PdfDocument doc, PdfPage page, string ops)
    {
        var contents = new PdfArray(doc);
        contents.Elements.Add(PdfObj.AddStream(doc, "q\n"u8.ToArray()).Reference!);
        var existing = page.Elements["/Contents"];
        if (PdfObj.Array(existing) is { } arr) foreach (var e in arr.Elements) contents.Elements.Add(e);
        else if (existing is PdfReference) contents.Elements.Add(existing);
        contents.Elements.Add(PdfObj.AddStream(doc, Encoding.ASCII.GetBytes("Q\n" + ops)).Reference!);
        page.Elements["/Contents"] = contents;
    }
}
