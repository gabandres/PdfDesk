using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfDesk.Core;

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
/// "Fill &amp; Sign" handwritten signatures and initials: an image placed on a page as a standard Stamp annotation with
/// an image appearance (transparency kept with a soft mask), so every viewer shows it. Not a digital signature.
/// Read back as <see cref="TextStamp"/> with <see cref="StampKind.Signature"/>; removed with <see cref="TextStamps.Remove"/>.
/// </summary>
public static class SignatureStamps
{
    private const string IdPrefix = "PdfDesk-signature-";

    public static IReadOnlyList<TextStamp> Read(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import, "This PDF can't be read");
        var list = new List<TextStamp>();
        for (var p = 0; p < doc.PageCount; p++)
        {
            if (PdfObj.Array(doc.Pages[p].Elements["/Annots"]) is not { } annots) continue;
            foreach (var a in annots.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>())
            {
                if (PdfObj.NameOf(a, "/Subtype") != "/Stamp" || PdfObj.Str(a.Elements["/NM"]) is not { } nm || !nm.StartsWith(IdPrefix, StringComparison.Ordinal)) continue;
                if (PdfObj.Array(a.Elements["/Rect"]) is not { Elements.Count: >= 4 } r) continue;
                list.Add(new TextStamp(nm, p, PdfObj.RectToPage(doc.Pages[p], r), StampKind.Signature, PdfObj.Text(a.Elements["/Contents"]) ?? "", 0));
            }
        }
        return list;
    }

    /// <summary>
    /// Places <paramref name="image"/> in <paramref name="rect"/> (page space). With <paramref name="replaceId"/> the
    /// existing stamp is moved/resized instead (its image kept when <paramref name="image"/> is null).
    /// </summary>
    public static byte[] Put(byte[] data, string? password, int pageIndex, RectD rect, StampImage? image, string? replaceId = null, string label = "Signature")
    {
        if (rect.IsEmpty) throw new ArgumentException("The signature needs a size.");
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        if (pageIndex < 0 || pageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfItem? imageRef = null;
        if (replaceId is not null)
        {
            var old = Find(doc, replaceId) ?? throw new InvalidOperationException("The signature is no longer in the document.");
            if (image is null)
                imageRef = PdfObj.Dict(PdfObj.Dict(PdfObj.Dict(PdfObj.Dict(old.Annot.Elements["/AP"])?.Elements["/N"])?.Elements["/Resources"])?.Elements["/XObject"])?.Elements["/Im1"];
            label = PdfObj.Text(old.Annot.Elements["/Contents"]) ?? label;
            PdfObj.Array(doc.Pages[old.Page].Elements["/Annots"])!.Elements.RemoveAt(old.Index);
        }
        if (image is not null) imageRef = ImageXObject(doc, image).Reference;
        if (imageRef is null) throw new InvalidOperationException("The signature image is missing.");

        var page = doc.Pages[pageIndex];
        var (x1, y1, x2, y2) = TextStamps.FromPageSpace(page, rect);
        var res = new PdfDictionary(doc);
        res.Elements["/XObject"] = new PdfDictionary(doc) { Elements = { ["/Im1"] = imageRef } };
        var content = System.Text.Encoding.ASCII.GetBytes($"q {PdfObj.F(rect.Width)} 0 0 {PdfObj.F(rect.Height)} 0 0 cm /Im1 Do Q");
        var ap = PdfObj.FormXObject(doc, content, 0, 0, rect.Width, rect.Height, res, PdfObj.CounterRotation(doc, page));

        var annot = new PdfDictionary(doc);
        annot.Elements["/Type"] = new PdfName("/Annot");
        annot.Elements["/Subtype"] = new PdfName("/Stamp");
        annot.Elements["/Name"] = new PdfName("/PdfDeskSignature");
        annot.Elements["/Rect"] = PdfObj.Reals(doc, x1, y1, x2, y2);
        annot.Elements["/Contents"] = PdfObj.MakeString(label);
        annot.Elements["/NM"] = new PdfString(replaceId ?? $"{IdPrefix}{Guid.NewGuid():N}");
        annot.Elements["/F"] = new PdfInteger(4); // print
        annot.Elements["/M"] = PdfObj.DateString(DateTimeOffset.Now);
        annot.Elements["/AP"] = new PdfDictionary(doc) { Elements = { ["/N"] = ap.Reference! } };
        annot.Elements["/P"] = page.Reference!;
        doc.Internals.AddObject(annot);
        PdfObj.Annots(doc, page).Elements.Add(annot.Reference!);
        return PdfObj.Save(doc);
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
        if (img.Alpha is { } alpha && alpha.Any(a => a != 255)) image.Elements["/SMask"] = Image(alpha, "/DeviceGray").Reference!;
        return image;
    }
}
