using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>
/// How a stamp looks: <see cref="Title"/> in bold capitals in a rounded frame, an optional <see cref="Detail"/> line
/// under it (who and when), all in <see cref="Color"/>.
/// </summary>
/// <param name="Name">The PDF stamp name (/Name: Approved, NotApproved, Draft, …), which other viewers may know.</param>
/// <param name="Label">What it is called in lists and the Comments pane ("Approved", "Rejected", …).</param>
public sealed record StampDesign(string Name, string Label, string Title, string? Detail, AnnotColor Color)
{
    public static readonly AnnotColor Green = new(0.12, 0.5, 0.2);
    public static readonly AnnotColor Red = new(0.8, 0.12, 0.12);
    public static readonly AnnotColor Blue = new(0.12, 0.3, 0.75);
}

/// <summary>
/// Stamps (Review › Stamp): built-in ones (Approved, Rejected, Draft, Final, Confidential, For Comment, Received, and
/// "Approved by &lt;user&gt; on &lt;date&gt;") drawn as vector appearances, and custom stamps from an image. Each is a
/// standard Stamp annotation with its own appearance stream, so every viewer shows and prints it the same; it lists in
/// the Comments pane, and Inkvolt PDF moves, resizes and deletes it like a placed signature (read back by
/// <see cref="SignatureStamps.Read"/> as <see cref="StampKind.Stamp"/>). A vector stamp keeps its design in a private
/// entry (/InkvoltPdfStamp), so a resize redraws it sharp.
/// </summary>
public static class RubberStamps
{
    internal const string IdPrefix = "InkvoltPdf-stamp-";
    private const string DesignKey = "/InkvoltPdfStamp";

    /// <summary>The title's size in the stamp's own layout (points); the stamp scales to its rectangle.</summary>
    private const double TitleSize = 24;

    public static readonly IReadOnlyList<StampDesign> BuiltIn =
    [
        new("Approved", "Approved", "APPROVED", null, StampDesign.Green),
        new("NotApproved", "Rejected", "REJECTED", null, StampDesign.Red),
        new("Draft", "Draft", "DRAFT", null, StampDesign.Blue),
        new("Final", "Final", "FINAL", null, StampDesign.Green),
        new("Confidential", "Confidential", "CONFIDENTIAL", null, StampDesign.Red),
        new("ForComment", "For Comment", "FOR COMMENT", null, StampDesign.Blue),
        new("Received", "Received", "RECEIVED", null, StampDesign.Blue),
    ];

    /// <summary>"APPROVED / By &lt;user&gt; on &lt;date&gt;", the date and time written the way <paramref name="culture"/> does (null: the current one).</summary>
    public static StampDesign ApprovedBy(string user, DateTime when, CultureInfo? culture = null) =>
        new("Approved", "Approved", "APPROVED", $"By {user} on {when.ToString("g", culture ?? CultureInfo.CurrentCulture)}", StampDesign.Green);

    /// <summary>The stamp's natural size in points (its layout at a 24 pt title); placing it at another size scales it.</summary>
    public static (double Width, double Height) NaturalSize(StampDesign design) => Layout(design).Size;

    private sealed record StampLayout((double Width, double Height) Size, double Pad, double Line, double DetailSize, TextPainter Title, TextPainter? Detail);

    private static StampLayout Layout(StampDesign d)
    {
        var bold = new StdFont(StdFamily.Helvetica, true, false);
        var title = new TextPainter(bold, [d.Title]);
        var detail = string.IsNullOrWhiteSpace(d.Detail) ? null : new TextPainter(StdFont.Helvetica, [d.Detail]);
        var detailSize = TitleSize * 0.42;
        var pad = TitleSize * 0.55;
        var line = TitleSize * 0.11;
        var width = Math.Max(title.Measure(d.Title) * TitleSize, detail is null ? 0 : detail.Measure(d.Detail!) * detailSize) + 2 * pad;
        var height = (title.Ascent - title.Descent) * TitleSize + (detail is null ? 0 : detailSize * 1.35) + pad * 0.9;
        return new StampLayout((width, height), pad, line, detailSize, title, detail);
    }

    /// <summary>
    /// The appearance's drawing for a stamp drawn into a <paramref name="width"/> x <paramref name="height"/> box: the
    /// natural layout scaled to fit, and the resources it uses (fonts).
    /// </summary>
    internal static (byte[] Ops, PdfDictionary Resources) Appearance(PdfDocument doc, StampDesign d, double width, double height)
    {
        var l = Layout(d);
        var (nw, nh) = l.Size;
        var color = new PdfColorValue([d.Color.R, d.Color.G, d.Color.B]);
        var ops = new StringBuilder();
        ops.Append(CultureInfo.InvariantCulture, $"q {PdfObj.F(width / nw)} 0 0 {PdfObj.F(height / nh)} 0 0 cm\n");
        // The frame: a rounded rectangle inside the box, its line fully inside.
        var half = l.Line / 2;
        ops.Append(CultureInfo.InvariantCulture, $"{color.Stroke} {PdfObj.F(l.Line)} w ");
        ops.Append(RoundedRect(half, half, nw - l.Line, nh - l.Line, Math.Min(nh * 0.22, TitleSize * 0.4))).Append(" S\n");

        var titleWidth = l.Title.Measure(d.Title) * TitleSize;
        var detailBand = l.Detail is null ? 0 : l.DetailSize * 1.35;
        // The title centered in what's above the detail line.
        var titleBase = detailBand + (nh - detailBand - (l.Title.Ascent - l.Title.Descent) * TitleSize) / 2 - l.Title.Descent * TitleSize;
        l.Title.Draw(d.Title, (nw - titleWidth) / 2, titleBase, TitleSize);
        var (titleOps, resources) = l.Title.Build(doc, nw, nh, "/HeBo", color);
        ops.Append(Encoding.Latin1.GetString(titleOps));
        if (l.Detail is { } detail)
        {
            var dw = detail.Measure(d.Detail!) * l.DetailSize;
            detail.Draw(d.Detail!, (nw - dw) / 2, l.Pad * 0.45 + detailBand * 0.15, l.DetailSize);
            var (detailOps, detailRes) = detail.Build(doc, nw, nh, "/Helv", color);
            ops.Append(Encoding.Latin1.GetString(detailOps));
            // Both painters' fonts in one resource dictionary (their names differ: /HeBo and /Helv, or each subset's own).
            if (PdfObj.Dict(detailRes.Elements["/Font"]) is { } more && PdfObj.Dict(resources.Elements["/Font"]) is { } fonts)
                foreach (var key in more.Elements.Keys.Where(k => !fonts.Elements.ContainsKey(k))) fonts.Elements[key] = more.Elements[key];
        }
        ops.Append("Q\n");
        return (Encoding.Latin1.GetBytes(ops.ToString()), resources);
    }

    private static string RoundedRect(double x, double y, double w, double h, double r)
    {
        const double k = 0.5523;
        string P(double px, double py) => $"{PdfObj.F(px)} {PdfObj.F(py)}";
        double x2 = x + w, y2 = y + h;
        return $"{P(x + r, y)} m {P(x2 - r, y)} l {P(x2 - r + r * k, y)} {P(x2, y + r - r * k)} {P(x2, y + r)} c " +
               $"{P(x2, y2 - r)} l {P(x2, y2 - r + r * k)} {P(x2 - r + r * k, y2)} {P(x2 - r, y2)} c " +
               $"{P(x + r, y2)} l {P(x + r - r * k, y2)} {P(x, y2 - r + r * k)} {P(x, y2 - r)} c " +
               $"{P(x, y + r)} l {P(x, y + r - r * k)} {P(x + r - r * k, y)} {P(x + r, y)} c h";
    }

    /// <summary>
    /// Places a stamp of <paramref name="design"/> in <paramref name="rect"/> (page space), or with
    /// <paramref name="replaceId"/> moves/resizes an existing one (redrawn at its new size; its own design and author kept).
    /// </summary>
    public static byte[] Put(byte[] data, string? password, int pageIndex, RectD rect, StampDesign design, string? author, string? replaceId = null)
    {
        if (rect.IsEmpty) throw new ArgumentException("The stamp needs a size.");
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        if (pageIndex < 0 || pageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        (int Page, PdfDictionary Annot)? old = null;
        if (replaceId is not null)
        {
            old = Annotations.Find(doc, replaceId) ?? throw new InvalidOperationException("The stamp is no longer in the document.");
            design = DesignOf(old.Value.Annot) ?? design;
        }

        var page = doc.Pages[pageIndex];
        var (x1, y1, x2, y2) = TextStamps.FromPageSpace(page, rect);
        var (ops, resources) = Appearance(doc, design, rect.Width, rect.Height);
        var ap = PdfObj.FormXObject(doc, ops, 0, 0, rect.Width, rect.Height, resources, PdfObj.CounterRotation(doc, page));
        if (old is { } o)
        {
            // Redrawn at its new size in place: the same annotation, everything else about it kept.
            SignatureStamps.MoveAnnotation(doc, o.Annot, o.Page, pageIndex, x1, y1, x2, y2, ap);
            return PdfObj.Save(doc);
        }

        var stored = new PdfDictionary(doc);
        stored.Elements["/Title"] = PdfObj.MakeString(design.Title);
        if (design.Detail is { } detail) stored.Elements["/Detail"] = PdfObj.MakeString(detail);
        stored.Elements["/Label"] = PdfObj.MakeString(design.Label);

        var annot = new PdfDictionary(doc);
        annot.Elements["/Type"] = new PdfName("/Annot");
        annot.Elements["/Subtype"] = new PdfName("/Stamp");
        annot.Elements["/Name"] = new PdfName("/" + PdfName(design.Name));
        annot.Elements["/Rect"] = PdfObj.Reals(doc, x1, y1, x2, y2);
        annot.Elements["/Contents"] = PdfObj.MakeString(design.Detail is null ? design.Title : $"{design.Title}\n{design.Detail}");
        annot.Elements["/Subj"] = PdfObj.MakeString(design.Label);
        annot.Elements["/NM"] = new PdfString($"{IdPrefix}{Guid.NewGuid():N}");
        annot.Elements["/C"] = PdfObj.Reals(doc, design.Color.R, design.Color.G, design.Color.B);
        annot.Elements["/F"] = new PdfInteger(4); // print
        if (author is not null) annot.Elements["/T"] = PdfObj.MakeString(author);
        annot.Elements["/M"] = PdfObj.DateString(DateTimeOffset.Now);
        annot.Elements["/CreationDate"] = PdfObj.DateString(DateTimeOffset.Now);
        annot.Elements[DesignKey] = stored;
        annot.Elements["/AP"] = new PdfDictionary(doc) { Elements = { ["/N"] = ap.Reference! } };
        annot.Elements["/P"] = page.Reference!;
        doc.Internals.AddObject(annot);
        PdfObj.Annots(doc, page).Elements.Add(annot.Reference!);
        return PdfObj.Save(doc);
    }

    /// <summary>Places a custom stamp made from an image (moved later with <see cref="Move"/>).</summary>
    public static byte[] PutImage(byte[] data, string? password, int pageIndex, RectD rect, StampImage image, string label, string? author) =>
        SignatureStamps.Place(data, password, pageIndex, rect, image, null, label, IdPrefix, "/" + PdfName(label), author, label);

    /// <summary>Moves or resizes a stamp: a vector one is redrawn at its new size, an image one keeps its picture.</summary>
    public static byte[] Move(byte[] data, string? password, string id, int pageIndex, RectD rect)
    {
        StampDesign? design;
        using (var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import, "This PDF can't be edited"))
        {
            var (_, annot) = Annotations.Find(doc, id) ?? throw new InvalidOperationException("The stamp is no longer in the document.");
            design = DesignOf(annot);
        }
        return design is null
            ? SignatureStamps.Put(data, password, pageIndex, rect, null, id)
            : Put(data, password, pageIndex, rect, design, null, id);
    }

    /// <summary>The design a vector stamp was made with (null for an image stamp or another app's).</summary>
    internal static StampDesign? DesignOf(PdfDictionary annot)
    {
        if (PdfObj.Dict(annot.Elements[DesignKey]) is not { } d || PdfObj.Str(d.Elements["/Title"]) is not { } title) return null;
        var c = PdfObj.Array(annot.Elements["/C"]);
        var color = c is { Elements.Count: 3 } ? new AnnotColor(PdfObj.Num(c, 0), PdfObj.Num(c, 1), PdfObj.Num(c, 2)) : StampDesign.Blue;
        return new StampDesign(PdfObj.Name(annot.Elements["/Name"]) ?? "Stamp", PdfObj.Str(d.Elements["/Label"]) ?? title, title, PdfObj.Str(d.Elements["/Detail"]), color);
    }

    /// <summary>A stamp name usable as a PDF name: letters and digits only.</summary>
    private static string PdfName(string name)
    {
        var clean = new string([.. name.Where(char.IsAsciiLetterOrDigit)]);
        return clean.Length == 0 ? "Stamp" : clean;
    }
}
