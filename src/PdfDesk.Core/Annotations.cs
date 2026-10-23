using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfDesk.Core;

public enum AnnotationKind
{
    Text, FreeText, Highlight, Underline, StrikeOut, Squiggly, Line, Square, Circle, Polygon, PolyLine, Ink, Stamp, Caret,
    FileAttachment, Sound, Redact, Other,
}

public enum TextMarkupKind { Highlight, Underline, StrikeOut, Squiggly }

/// <summary>An RGB color with components 0..1.</summary>
public readonly record struct AnnotColor(double R, double G, double B)
{
    public static readonly AnnotColor Yellow = new(1, 0.85, 0.1);
    public static readonly AnnotColor Red = new(0.9, 0.1, 0.1);
}

/// <summary>A markup annotation (comment) on a page. Rects are in page space.</summary>
/// <param name="Id">Stable id: the annotation's /NM, else "p&lt;page&gt;:&lt;index&gt;" (valid for the bytes it was read from).</param>
/// <param name="Subtype">The PDF subtype without the slash ("Highlight", "Text", ...).</param>
/// <param name="QuadRects">Text markup: the marked line boxes (from /QuadPoints); empty for other kinds.</param>
/// <param name="IsPdfDeskStamp">A Fill &amp; Sign stamp (managed by <see cref="TextStamps"/>).</param>
/// <param name="ReplyTo">Id of the annotation this one replies to (/IRT), if any.</param>
/// <param name="Hidden">Hidden or NoView flag set: viewers don't draw it.</param>
/// <param name="Geometry">Drawing annotations (ink, shapes, lines, polygons, FreeText other than Fill &amp; Sign stamps): where
/// they are, for selecting, moving and resizing (see <see cref="Annotations.UpdateGeometry"/>); null for other kinds.</param>
/// <param name="Style">Drawing annotations: how they look (see <see cref="Annotations.UpdateStyle"/>); null for other kinds.</param>
public sealed record AnnotationInfo(
    string Id,
    AnnotationKind Kind,
    string Subtype,
    int PageIndex,
    RectD Rect,
    string Contents,
    string? Author,
    DateTimeOffset? Modified,
    string? Subject,
    AnnotColor? Color,
    IReadOnlyList<RectD> QuadRects,
    bool IsPdfDeskStamp,
    string? ReplyTo,
    bool Hidden,
    double Opacity = 1,
    AnnotGeometry? Geometry = null,
    AnnotStyle? Style = null);

/// <summary>
/// Reads and edits comments (markup annotations): text highlights/underlines/strike-outs/squiggles and sticky notes.
/// Widgets, links and popups are not listed. Every call takes the document bytes and returns new bytes.
/// New annotations get their own appearance stream, so every viewer draws them the same way.
/// </summary>
public static partial class Annotations
{
    internal const string IdPrefix = "pdfdesk-annot-";
    private const int FlagHidden = 2, FlagPrint = 4, FlagNoView = 32;

    /// <summary>A fresh annotation id (pass it to an Add call to know the id of what you created).</summary>
    public static string NewId() => $"{IdPrefix}{Guid.NewGuid():N}";

    public static IReadOnlyList<AnnotationInfo> Read(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import, "This PDF's comments can't be read");
        var list = new List<AnnotationInfo>();
        for (var p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            if (PdfObj.Array(page.Elements["/Annots"]) is not { } annots) continue;
            for (var i = 0; i < annots.Elements.Count; i++)
            {
                if (PdfObj.Dict(annots.Elements[i]) is not { } a) continue;
                var subtype = PdfObj.Name(a.Elements["/Subtype"]) ?? "";
                if (subtype is "Widget" or "Link" or "Popup" or "") continue;
                var rect = PdfObj.Array(a.Elements["/Rect"]) is { } r ? PdfObj.RectToPage(page, r) : default;
                var flags = PdfObj.Int(a.Elements["/F"]) ?? 0;
                var quads = new List<RectD>();
                if (PdfObj.Array(a.Elements["/QuadPoints"]) is { } qp)
                    for (var k = 0; k + 7 < qp.Elements.Count; k += 8)
                    {
                        var xs = new[] { PdfObj.Num(qp, k), PdfObj.Num(qp, k + 2), PdfObj.Num(qp, k + 4), PdfObj.Num(qp, k + 6) };
                        var ys = new[] { PdfObj.Num(qp, k + 1), PdfObj.Num(qp, k + 3), PdfObj.Num(qp, k + 5), PdfObj.Num(qp, k + 7) };
                        quads.Add(FormEditor.ToPageSpace(page, xs.Min(), ys.Min(), xs.Max(), ys.Max()));
                    }
                var nm = PdfObj.Str(a.Elements["/NM"]);
                var drawing = IsDrawing(a, subtype) ? Describe(page, a, subtype) : null;
                list.Add(new AnnotationInfo(
                    IdOf(a, p, i), KindOf(subtype), subtype, p, rect,
                    PdfObj.Str(a.Elements["/Contents"]) ?? "",
                    PdfObj.Str(a.Elements["/T"]),
                    PdfObj.ParseDate(PdfObj.Str(a.Elements["/M"])),
                    PdfObj.Str(a.Elements["/Subj"]),
                    ColorOf(PdfObj.Array(a.Elements["/C"])),
                    quads,
                    subtype == "FreeText" && nm?.StartsWith("PdfDesk-", StringComparison.Ordinal) == true,
                    PdfObj.Dict(a.Elements["/IRT"]) is { } irt ? IdOf(irt, p, -1) : null,
                    (flags & (FlagHidden | FlagNoView)) != 0,
                    PdfObj.Num(a.Elements["/CA"], 1),
                    drawing?.Geometry,
                    drawing?.Style));
            }
        }
        return list;
    }

    /// <summary>
    /// Adds a highlight / underline / strike-out / squiggly over the given text line boxes (page space, one per line,
    /// e.g. from <see cref="PageText.GetRects"/>). <paramref name="note"/> becomes the comment text.
    /// </summary>
    public static byte[] AddTextMarkup(byte[] data, string? password, int pageIndex, TextMarkupKind kind, IReadOnlyList<RectD> lineRects,
        AnnotColor color, string? note, string author, string? id = null)
    {
        if (lineRects.Count == 0 || lineRects.All(r => r.IsEmpty)) throw new ArgumentException("No text to mark.", nameof(lineRects));
        using var doc = OpenForEdit(data, password);
        var page = doc.Pages[pageIndex];

        // Quad corners in PDF space, in the reading orientation of the displayed page: upper-left, upper-right, lower-left, lower-right.
        var quads = new List<((double X, double Y) UL, (double X, double Y) UR, (double X, double Y) LL, (double X, double Y) LR)>();
        foreach (var r in lineRects.Where(r => !r.IsEmpty))
            quads.Add((PdfObj.ToPdf(page, r.X, r.Y), PdfObj.ToPdf(page, r.Right, r.Y), PdfObj.ToPdf(page, r.X, r.Bottom), PdfObj.ToPdf(page, r.Right, r.Bottom)));
        var allX = quads.SelectMany(q => new[] { q.UL.X, q.UR.X, q.LL.X, q.LR.X }).ToList();
        var allY = quads.SelectMany(q => new[] { q.UL.Y, q.UR.Y, q.LL.Y, q.LR.Y }).ToList();
        var pad = kind == TextMarkupKind.Squiggly ? 2 : 1;
        double x1 = allX.Min() - pad, y1 = allY.Min() - pad, x2 = allX.Max() + pad, y2 = allY.Max() + pad;

        var c = $"{PdfObj.F(color.R)} {PdfObj.F(color.G)} {PdfObj.F(color.B)}";
        var ops = new StringBuilder();
        foreach (var (ul, ur, ll, lr) in quads)
        {
            // Unit vectors along the line (d) and up from the baseline (u); h = line height.
            double dx = lr.X - ll.X, dy = lr.Y - ll.Y, ux = ul.X - ll.X, uy = ul.Y - ll.Y;
            var h = Math.Sqrt(ux * ux + uy * uy);
            var len = Math.Sqrt(dx * dx + dy * dy);
            (double X, double Y) At(double along, double up) => (ll.X + dx * along / Math.Max(len, 1e-6) + ux * up / Math.Max(h, 1e-6), ll.Y + dy * along / Math.Max(len, 1e-6) + uy * up / Math.Max(h, 1e-6));
            switch (kind)
            {
                case TextMarkupKind.Highlight:
                    ops.Append($"{c} rg {P(ul)} m {P(ur)} l {P(lr)} l {P(ll)} l h f\n");
                    break;
                case TextMarkupKind.Underline or TextMarkupKind.StrikeOut:
                {
                    var at = kind == TextMarkupKind.Underline ? h * 0.12 : h * 0.45;
                    ops.Append($"{c} RG {PdfObj.F(Math.Max(0.5, h / 14))} w {P(At(0, at))} m {P(At(len, at))} l S\n");
                    break;
                }
                case TextMarkupKind.Squiggly:
                {
                    var step = Math.Max(1.5, h / 6);
                    var amp = Math.Max(1, h / 12);
                    ops.Append($"{c} RG {PdfObj.F(Math.Max(0.5, h / 18))} w 1 j {P(At(0, 0))} m");
                    var up = true;
                    for (var t = step; t < len + step; t += step, up = !up) ops.Append($" {P(At(Math.Min(t, len), up ? amp : 0))} l");
                    ops.Append(" S\n");
                    break;
                }
            }
        }

        var resources = new PdfDictionary(doc);
        if (kind == TextMarkupKind.Highlight)
        {
            var gs = new PdfDictionary(doc);
            gs.Elements["/Type"] = new PdfName("/ExtGState");
            gs.Elements["/BM"] = new PdfName("/Multiply");
            var gsMap = new PdfDictionary(doc);
            gsMap.Elements["/GS0"] = gs;
            resources.Elements["/ExtGState"] = gsMap;
            ops.Insert(0, "/GS0 gs\n");
        }
        var ap = PdfObj.FormXObject(doc, Encoding.Latin1.GetBytes(ops.ToString()), x1, y1, x2, y2, resources);

        var annot = NewAnnot(doc, page, kind.ToString(), x1, y1, x2, y2, id, note ?? "", author, color);
        var qp = new List<double>();
        foreach (var (ul, ur, ll, lr) in quads) qp.AddRange([ul.X, ul.Y, ur.X, ur.Y, ll.X, ll.Y, lr.X, lr.Y]);
        annot.Elements["/QuadPoints"] = PdfObj.Reals(doc, [.. qp]);
        SetAppearance(doc, annot, ap);
        return PdfObj.Save(doc);

        static string P((double X, double Y) p) => $"{PdfObj.F(p.X)} {PdfObj.F(p.Y)}";
    }

    /// <summary>Adds a sticky note (a /Text annotation drawn as a 20 pt note icon whose top-left is at (x, y), page space).</summary>
    public static byte[] AddNote(byte[] data, string? password, int pageIndex, double x, double y, string text, string author,
        AnnotColor? color = null, string? id = null)
    {
        using var doc = OpenForEdit(data, password);
        var page = doc.Pages[pageIndex];
        const double size = 20;
        var (x1, y1, x2, y2) = TextStamps.FromPageSpace(page, new RectD(x, y, size, size));
        var col = color ?? AnnotColor.Yellow;
        var c = $"{PdfObj.F(col.R)} {PdfObj.F(col.G)} {PdfObj.F(col.B)}";
        // A speech bubble with three text lines, drawn upright on rotated pages.
        var ops = $"q 0.35 G 0.8 w {c} rg 1.5 6 m 1.5 18.5 l 18.5 18.5 l 18.5 6 l 9 6 l 5 1.5 l 6 6 l h B\n" +
                  "0.3 G 1 w 5 15 m 15 15 l 5 12 m 15 12 l 5 9 m 12 9 l S Q\n";
        var ap = PdfObj.FormXObject(doc, Encoding.Latin1.GetBytes(ops), 0, 0, size, size, new PdfDictionary(doc), PdfObj.CounterRotation(doc, page));
        var annot = NewAnnot(doc, page, "Text", x1, y1, x2, y2, id, text, author, col);
        annot.Elements["/Name"] = new PdfName("/Comment");
        annot.Elements["/Open"] = new PdfBoolean(false);
        SetAppearance(doc, annot, ap);
        return PdfObj.Save(doc);
    }

    /// <summary>Changes a comment's text. A FreeText box (other than a Fill &amp; Sign stamp) shows the new text: its appearance is rebuilt.</summary>
    public static byte[] SetContents(byte[] data, string? password, string id, string text)
    {
        using var doc = OpenForEdit(data, password);
        var (p, annot) = Find(doc, id) ?? throw new InvalidOperationException("That comment no longer exists.");
        annot.Elements["/Contents"] = PdfObj.MakeString(text);
        annot.Elements["/M"] = PdfObj.DateString(DateTimeOffset.Now);
        if (PdfObj.Name(annot.Elements["/Subtype"]) == "FreeText" && IsDrawing(annot, "FreeText") && Describe(doc.Pages[p], annot, "FreeText") is { } ft)
            Apply(doc, doc.Pages[p], annot, "FreeText", ft.Geometry, ft.Style);
        return PdfObj.Save(doc);
    }

    /// <summary>Deletes an annotation with its popup and its replies (and theirs).</summary>
    public static byte[] Delete(byte[] data, string? password, string id)
    {
        using var doc = OpenForEdit(data, password);
        if (!DeleteIn(doc, id)) throw new InvalidOperationException("That comment no longer exists.");
        return PdfObj.Save(doc);
    }

    internal static bool DeleteIn(PdfDocument doc, string id)
    {
        if (Find(doc, id) is not { } found) return false;
        SignedEdits.NoteRemoval();
        var all = new List<PdfDictionary>();
        for (var p = 0; p < doc.PageCount; p++)
            if (PdfObj.Array(doc.Pages[p].Elements["/Annots"]) is { } annots)
                all.AddRange(annots.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>());

        var doomed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { found.Annot };
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var a in all)
            {
                if (doomed.Contains(a)) continue;
                var irt = PdfObj.Dict(a.Elements["/IRT"]);
                var parent = PdfObj.Dict(a.Elements["/Parent"]);
                if (irt is not null && doomed.Contains(irt) || parent is not null && doomed.Contains(parent) && PdfObj.NameOf(a, "/Subtype") == "/Popup")
                    grew |= doomed.Add(a);
            }
            foreach (var a in doomed.ToList())
                if (PdfObj.Dict(a.Elements["/Popup"]) is { } popup) grew |= doomed.Add(popup);
        }

        for (var p = 0; p < doc.PageCount; p++)
        {
            if (PdfObj.Array(doc.Pages[p].Elements["/Annots"]) is null) continue;
            var annots = PdfObj.Annots(doc, doc.Pages[p]);
            for (var i = annots.Elements.Count - 1; i >= 0; i--)
                if (PdfObj.Dict(annots.Elements[i]) is { } a && doomed.Contains(a)) annots.Elements.RemoveAt(i);
        }
        return true;
    }

    internal static (int Page, PdfDictionary Annot)? Find(PdfDocument doc, string id)
    {
        for (var p = 0; p < doc.PageCount; p++)
        {
            if (PdfObj.Array(doc.Pages[p].Elements["/Annots"]) is not { } annots) continue;
            for (var i = 0; i < annots.Elements.Count; i++)
                if (PdfObj.Dict(annots.Elements[i]) is { } a && IdOf(a, p, i) == id) return (p, a);
        }
        return null;
    }

    /// <summary>
    /// The annotation's /NM, else its position ("p&lt;page&gt;:&lt;index in /Annots&gt;"; <paramref name="index"/> -1 looks
    /// it up, page <paramref name="page"/> first). Never the object number: PDFsharp renumbers objects when it opens a
    /// file for editing but not when it opens one for reading, and an incrementally updated file has gaps.
    /// </summary>
    internal static string IdOf(PdfDictionary a, int page, int index)
    {
        if (PdfObj.Str(a.Elements["/NM"]) is { Length: > 0 } nm) return nm;
        if (index < 0 && a.Owner is { } doc)
        {
            foreach (var p in (IEnumerable<int>)[page, .. Enumerable.Range(0, doc.PageCount)])
            {
                if (p < 0 || p >= doc.PageCount || PdfObj.Array(doc.Pages[p].Elements["/Annots"]) is not { } annots) continue;
                for (var i = 0; i < annots.Elements.Count; i++)
                    if (ReferenceEquals(PdfObj.Dict(annots.Elements[i]), a)) return $"p{p}:{i}";
            }
        }
        return $"p{page}:{index}";
    }

    private static PdfDictionary NewAnnot(PdfDocument doc, PdfPage page, string subtype, double x1, double y1, double x2, double y2,
        string? id, string contents, string author, AnnotColor color)
    {
        var now = PdfObj.DateString(DateTimeOffset.Now);
        var annot = new PdfDictionary(doc);
        annot.Elements["/Type"] = new PdfName("/Annot");
        annot.Elements["/Subtype"] = new PdfName("/" + subtype);
        annot.Elements["/Rect"] = PdfObj.Reals(doc, x1, y1, x2, y2);
        annot.Elements["/NM"] = new PdfString(id ?? NewId());
        annot.Elements["/F"] = new PdfInteger(FlagPrint);
        annot.Elements["/C"] = PdfObj.Reals(doc, color.R, color.G, color.B);
        annot.Elements["/Contents"] = PdfObj.MakeString(contents);
        annot.Elements["/T"] = PdfObj.MakeString(author);
        annot.Elements["/M"] = now;
        annot.Elements["/CreationDate"] = PdfObj.DateString(DateTimeOffset.Now);
        annot.Elements["/P"] = page.Reference!;
        doc.Internals.AddObject(annot);
        PdfObj.Annots(doc, page).Elements.Add(annot.Reference!);
        return annot;
    }

    private static void SetAppearance(PdfDocument doc, PdfDictionary annot, PdfDictionary form)
    {
        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = form.Reference!;
        annot.Elements["/AP"] = ap;
    }

    private static AnnotationKind KindOf(string subtype) => Enum.TryParse<AnnotationKind>(subtype, out var k) && k != AnnotationKind.Other ? k : AnnotationKind.Other;

    private static AnnotColor? ColorOf(PdfArray? c) => c?.Elements.Count switch
    {
        1 => new AnnotColor(PdfObj.Num(c, 0), PdfObj.Num(c, 0), PdfObj.Num(c, 0)),
        3 => new AnnotColor(PdfObj.Num(c, 0), PdfObj.Num(c, 1), PdfObj.Num(c, 2)),
        4 => new AnnotColor((1 - PdfObj.Num(c, 0)) * (1 - PdfObj.Num(c, 3)), (1 - PdfObj.Num(c, 1)) * (1 - PdfObj.Num(c, 3)), (1 - PdfObj.Num(c, 2)) * (1 - PdfObj.Num(c, 3))),
        _ => null,
    };

    private static PdfDocument OpenForEdit(byte[] data, string? password) =>
        PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
}
