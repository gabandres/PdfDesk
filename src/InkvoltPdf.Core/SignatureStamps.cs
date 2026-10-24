using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>Decoded image pixels: 8-bit RGB (row-major, no padding) and optional 8-bit alpha (transparency).</summary>
public sealed record StampImage(int Width, int Height, byte[] Rgb, byte[]? Alpha)
{
    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || Width > 8192 || Height > 8192) throw new ArgumentException("The image size isn't usable.");
        if (Rgb.Length != (long)Width * Height * 3 || (Alpha is not null && Alpha.Length != (long)Width * Height))
            throw new ArgumentException("The image data doesn't match its size.");
    }
}

/// <summary>
/// "Fill &amp; Sign" handwritten signatures and initials, and pictures placed with the Image tool: an image placed on a
/// page as a standard Stamp annotation with an image appearance (transparency kept with a soft mask), so every viewer
/// shows it. Not a digital signature. Read back as <see cref="TextStamp"/> with <see cref="StampKind.Signature"/> or
/// <see cref="StampKind.Image"/> (and <see cref="StampKind.Stamp"/> for <see cref="RubberStamps"/>); moved and resized
/// with <see cref="Put"/>, removed with <see cref="TextStamps.Remove"/>.
/// </summary>
public static class SignatureStamps
{
    private const string IdPrefix = "InkvoltPdf-signature-";

    /// <summary>Id prefix of pictures placed with the Image tool.</summary>
    private const string ImagePrefix = "InkvoltPdf-image-";

    /// <summary>Id prefix of signature stamps made before the Inkvolt rename; still recognized.</summary>
    private const string LegacyIdPrefix = "PdfDesk-signature-";

    public static IReadOnlyList<TextStamp> Read(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import, "This PDF can't be read");
        var list = new List<TextStamp>();
        for (var p = 0; p < doc.PageCount; p++)
        {
            if (PdfObj.Array(doc.Pages[p].Elements["/Annots"]) is not { } annots) continue;
            foreach (var a in annots.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>())
            {
                if (PdfObj.NameOf(a, "/Subtype") != "/Stamp" || PdfObj.Str(a.Elements["/NM"]) is not { } nm || KindOf(nm) is not { } kind) continue;
                if (PdfObj.Array(a.Elements["/Rect"]) is not { Elements.Count: >= 4 } r) continue;
                list.Add(new TextStamp(nm, p, PdfObj.RectToPage(doc.Pages[p], r), kind, PdfObj.Text(a.Elements["/Contents"]) ?? "", 0));
            }
        }
        return list;
    }

    /// <summary>The kind of stamp an /NM this class gives belongs to; null for any other.</summary>
    internal static StampKind? KindOf(string nm) =>
        nm.StartsWith(IdPrefix, StringComparison.Ordinal) || nm.StartsWith(LegacyIdPrefix, StringComparison.Ordinal) ? StampKind.Signature
        : nm.StartsWith(ImagePrefix, StringComparison.Ordinal) ? StampKind.Image
        : nm.StartsWith(RubberStamps.IdPrefix, StringComparison.Ordinal) ? StampKind.Stamp
        : null;

    /// <summary>
    /// Places <paramref name="image"/> in <paramref name="rect"/> (page space). With <paramref name="replaceId"/> the
    /// existing stamp (a signature or a picture) is moved/resized instead (its image kept when <paramref name="image"/> is null).
    /// </summary>
    public static byte[] Put(byte[] data, string? password, int pageIndex, RectD rect, StampImage? image, string? replaceId = null, string label = "Signature") =>
        Place(data, password, pageIndex, rect, image, replaceId, label, IdPrefix, "/InkvoltPdfSignature", null);

    /// <summary>Places a picture (the Image tool) in <paramref name="rect"/> (page space); <see cref="Put"/> moves it later.</summary>
    public static byte[] PutImage(byte[] data, string? password, int pageIndex, RectD rect, StampImage image, string label, string? author = null) =>
        Place(data, password, pageIndex, rect, image, null, label, ImagePrefix, "/InkvoltPdfImage", author, "Image");

    /// <summary>
    /// Places an image stamp. <paramref name="idPrefix"/>, <paramref name="name"/> (/Name), <paramref name="author"/> (/T)
    /// and <paramref name="subject"/> (/Subj) are for a new stamp; a replaced one keeps its own, and its creation date.
    /// </summary>
    internal static byte[] Place(byte[] data, string? password, int pageIndex, RectD rect, StampImage? image, string? replaceId, string label,
        string idPrefix, string name, string? author, string? subject = null)
    {
        if (rect.IsEmpty) throw new ArgumentException("The stamp needs a size.");
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        if (pageIndex < 0 || pageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfItem? imageRef = null;
        (int Page, int Index, PdfDictionary Annot)? old = null;
        if (replaceId is not null)
        {
            old = Find(doc, replaceId) ?? throw new InvalidOperationException("The stamp is no longer in the document.");
            if (image is null)
                imageRef = PdfObj.Dict(PdfObj.Dict(PdfObj.Dict(PdfObj.Dict(old.Value.Annot.Elements["/AP"])?.Elements["/N"])?.Elements["/Resources"])?.Elements["/XObject"])?.Elements["/Im1"];
        }
        if (image is not null) imageRef = ImageXObject(doc, image).Reference;
        if (imageRef is null) throw new InvalidOperationException("The stamp's image is missing.");

        var page = doc.Pages[pageIndex];
        var (x1, y1, x2, y2) = TextStamps.FromPageSpace(page, rect);
        var res = new PdfDictionary(doc);
        res.Elements["/XObject"] = new PdfDictionary(doc) { Elements = { ["/Im1"] = imageRef } };
        var content = System.Text.Encoding.ASCII.GetBytes($"q {PdfObj.F(rect.Width)} 0 0 {PdfObj.F(rect.Height)} 0 0 cm /Im1 Do Q");
        var ap = PdfObj.FormXObject(doc, content, 0, 0, rect.Width, rect.Height, res, PdfObj.CounterRotation(doc, page));

        if (old is { } o)
        {
            // Moved or resized in place: the same annotation (replies, its popup, structure, layer, opacity and its
            // place among the page's annotations stay), with a new rectangle and appearance.
            MoveAnnotation(doc, o.Annot, o.Page, pageIndex, x1, y1, x2, y2, ap);
            return PdfObj.Save(doc);
        }

        var annot = new PdfDictionary(doc);
        annot.Elements["/Type"] = new PdfName("/Annot");
        annot.Elements["/Subtype"] = new PdfName("/Stamp");
        annot.Elements["/Name"] = new PdfName(name);
        annot.Elements["/Rect"] = PdfObj.Reals(doc, x1, y1, x2, y2);
        annot.Elements["/Contents"] = PdfObj.MakeString(label);
        annot.Elements["/NM"] = new PdfString($"{idPrefix}{Guid.NewGuid():N}");
        if (author is not null) annot.Elements["/T"] = PdfObj.MakeString(author);
        if (subject is not null) annot.Elements["/Subj"] = PdfObj.MakeString(subject);
        annot.Elements["/F"] = new PdfInteger(4); // print
        annot.Elements["/M"] = PdfObj.DateString(DateTimeOffset.Now);
        annot.Elements["/AP"] = new PdfDictionary(doc) { Elements = { ["/N"] = ap.Reference! } };
        annot.Elements["/P"] = page.Reference!;
        doc.Internals.AddObject(annot);
        PdfObj.Annots(doc, page).Elements.Add(annot.Reference!);
        return PdfObj.Save(doc);
    }

    /// <summary>
    /// Gives a placed annotation a new rectangle (PDF coordinates) and normal appearance, and the time of the change. On
    /// another page it moves into that page's annotations (its popup with it), at the end, as one placed there would be.
    /// </summary>
    internal static void MoveAnnotation(PdfDocument doc, PdfDictionary annot, int fromPage, int toPage, double x1, double y1, double x2, double y2, PdfDictionary ap)
    {
        annot.Elements["/Rect"] = PdfObj.Reals(doc, x1, y1, x2, y2);
        var appearance = PdfObj.Dict(annot.Elements["/AP"]) ?? new PdfDictionary(doc);
        appearance.Elements["/N"] = ap.Reference!;
        // /D (down) and /R (rollover) would show the old size; /AS names a state of the old /N.
        appearance.Elements.Remove("/D");
        appearance.Elements.Remove("/R");
        annot.Elements["/AP"] = appearance;
        annot.Elements.Remove("/AS");
        annot.Elements["/M"] = PdfObj.DateString(DateTimeOffset.Now);
        if (fromPage == toPage) return;

        var popup = PdfObj.Dict(annot.Elements["/Popup"]);
        var from = PdfObj.Annots(doc, doc.Pages[fromPage]);
        var to = PdfObj.Annots(doc, doc.Pages[toPage]);
        foreach (var a in popup is null ? [annot] : new[] { annot, popup })
        {
            for (var i = from.Elements.Count - 1; i >= 0; i--)
                if (ReferenceEquals(PdfObj.Dict(from.Elements[i]), a)) from.Elements.RemoveAt(i);
            to.Elements.Add(a.Reference is { } r ? r : a);
            a.Elements["/P"] = doc.Pages[toPage].Reference!;
        }
    }

    private static (int Page, int Index, PdfDictionary Annot)? Find(PdfDocument doc, string id)
    {
        for (var p = 0; p < doc.PageCount; p++)
        {
            if (PdfObj.Array(doc.Pages[p].Elements["/Annots"]) is not { } annots) continue;
            for (var i = 0; i < annots.Elements.Count; i++)
                if (PdfObj.Dict(annots.Elements[i]) is { } a && PdfObj.Str(a.Elements["/NM"]) == id) return (p, i, a);
        }
        return null;
    }

    /// <summary>An image XObject (Flate RGB, alpha as a soft mask).</summary>
    internal static PdfDictionary ImageXObject(PdfDocument doc, StampImage img)
    {
        img.Validate();
        PdfDictionary Image(byte[] samples, string colorSpace)
        {
            var d = new PdfDictionary(doc);
            d.CreateStream(PdfObj.Deflate(samples));
            d.Elements["/Filter"] = new PdfName("/FlateDecode");
            d.Elements["/Type"] = new PdfName("/XObject");
            d.Elements["/Subtype"] = new PdfName("/Image");
            d.Elements["/Width"] = new PdfInteger(img.Width);
            d.Elements["/Height"] = new PdfInteger(img.Height);
            d.Elements["/ColorSpace"] = new PdfName(colorSpace);
            d.Elements["/BitsPerComponent"] = new PdfInteger(8);
            doc.Internals.AddObject(d);
            return d;
        }
        var image = Image(img.Rgb, "/DeviceRGB");
        // Alpha that hides every pixel isn't transparency but a missing alpha channel (32-bit clipboard bitmaps): opaque.
        if (img.Alpha is { } alpha && alpha.Any(a => a != 255) && alpha.Any(a => a != 0)) image.Elements["/SMask"] = Image(alpha, "/DeviceGray").Reference!;
        return image;
    }
}
