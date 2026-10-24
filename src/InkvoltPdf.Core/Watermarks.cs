using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>Where a watermark goes: once in the middle of the page, or repeated across it.</summary>
public enum WatermarkLayout { Center, Tiled }

/// <summary>What <see cref="Watermarks.Apply"/> writes: a text or an image (one of the two).</summary>
public sealed record WatermarkOptions
{
    public string? Text { get; init; }
    public StampImage? Image { get; init; }

    public double FontSize { get; init; } = 72;
    public AnnotColor Color { get; init; } = new(0.5, 0.5, 0.5);

    /// <summary>0 (invisible) to 1 (opaque).</summary>
    public double Opacity { get; init; } = 0.3;

    /// <summary>Degrees counterclockwise on the page as shown (45: rising from bottom-left to top-right).</summary>
    public double Rotation { get; init; } = 45;

    public WatermarkLayout Layout { get; init; }

    /// <summary>An image's width as a fraction of the page's width (as shown).</summary>
    public double ImageScale { get; init; } = 0.5;

    /// <summary>Under the page's content (opaque areas, such as a scanned page, hide it) instead of over it.</summary>
    public bool Behind { get; init; }
}

/// <summary>
/// Watermarks: a text (Helvetica, or an embedded subset font for other scripts) or an image, turned and see-through,
/// once in the middle of each page or tiled across it, over or behind the page's content. Written into the pages
/// (it prints everywhere), laid out on the page as shown, and tagged so <see cref="Remove"/> can take it off again
/// (see <see cref="PageMarks"/>). Applying again replaces the earlier watermark of those pages.
/// </summary>
public static class Watermarks
{
    internal const string Kind = "Watermark";

    /// <summary>Tiles never exceed this many per page (a tiny text would otherwise make a huge content stream).</summary>
    public const int MaxTiles = 400;

    /// <summary>
    /// Centers (displayed-page coordinates, origin bottom-left, y up) for <paramref name="tileWidth"/> x
    /// <paramref name="tileHeight"/> tiles turned by <paramref name="rotation"/> degrees: rows a little apart, every other
    /// row shifted by half a step, reaching past the page's edges so its corners are covered too. At most <see cref="MaxTiles"/>.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> TileCenters(double pageWidth, double pageHeight, double tileWidth, double tileHeight, double rotation)
    {
        var (ew, eh) = TurnedExtent(tileWidth, tileHeight, rotation);
        var gap = Math.Max(12, Math.Min(ew, eh) * 0.6);
        var stepX = ew + gap;
        var stepY = eh + gap;
        // Grow the steps until the grid fits the limit.
        while ((pageWidth / stepX + 3) * (pageHeight / stepY + 3) > MaxTiles)
        {
            stepX *= 1.25;
            stepY *= 1.25;
        }
        var list = new List<(double, double)>();
        var row = 0;
        for (var y = pageHeight / 2 - Math.Ceiling(pageHeight / 2 / stepY + 1) * stepY; y <= pageHeight + stepY; y += stepY, row++)
        {
            var shift = row % 2 == 0 ? 0 : stepX / 2;
            for (var x = pageWidth / 2 - Math.Ceiling(pageWidth / 2 / stepX + 1) * stepX + shift; x <= pageWidth + stepX; x += stepX)
                if (x + ew / 2 >= 0 && x - ew / 2 <= pageWidth && y + eh / 2 >= 0 && y - eh / 2 <= pageHeight && list.Count < MaxTiles)
                    list.Add((x, y));
        }
        return list;
    }

    /// <summary>The axis-aligned size of a w x h box turned by <paramref name="degrees"/>.</summary>
    internal static (double W, double H) TurnedExtent(double w, double h, double degrees)
    {
        var a = degrees * Math.PI / 180;
        double c = Math.Abs(Math.Cos(a)), s = Math.Abs(Math.Sin(a));
        return (w * c + h * s, w * s + h * c);
    }

    /// <summary>Writes the watermark on <paramref name="pages"/> (0-based).</summary>
    public static byte[] Apply(byte[] data, string? password, IReadOnlyCollection<int> pages, WatermarkOptions options)
    {
        var text = options.Text?.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', ' ');
        if (string.IsNullOrWhiteSpace(text) == (options.Image is null))
            throw new ArgumentException(options.Image is null ? "Type the watermark's text or choose an image." : "A watermark is a text or an image, not both.");
        if (options.Image is null && (!double.IsFinite(options.FontSize) || options.FontSize is < 4 or > 500)) throw new ArgumentException("The font size must be from 4 to 500 points.");
        if (options.Image is not null && (!double.IsFinite(options.ImageScale) || options.ImageScale is <= 0 or > 4)) throw new ArgumentException("The image size must be from 1% to 400% of the page width.");
        if (!double.IsFinite(options.Rotation) || !double.IsFinite(options.Opacity)) throw new ArgumentException("The angle and opacity must be numbers.");
        options.Image?.Validate();

        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        var opacity = Math.Clamp(options.Opacity, 0.01, 1);
        var gs = new PdfDictionary(doc);
        gs.Elements["/Type"] = new PdfName("/ExtGState");
        gs.Elements["/ca"] = new PdfReal(opacity);
        gs.Elements["/CA"] = new PdfReal(opacity);
        doc.Internals.AddObject(gs);

        // One tile (the text or the image, centered on its origin), shared by every page.
        PdfDictionary tile;
        double tileW, tileH;
        if (options.Image is { } img)
        {
            tile = SignatureStamps.ImageXObject(doc, img);
            tileW = tileH = 0; // sized per page
        }
        else
        {
            var lines = text!.Split('\n');
            var size = options.FontSize;
            var painter = new TextPainter(StdFont.Helvetica, lines);
            var leading = size * 1.15;
            tileW = lines.Max(l => painter.Measure(l)) * size;
            tileH = (painter.Ascent - painter.Descent) * size + leading * (lines.Length - 1);
            var top = tileH / 2 - painter.Ascent * size;
            for (var i = 0; i < lines.Length; i++)
                painter.Draw(lines[i], -painter.Measure(lines[i]) * size / 2, top - i * leading, size);
            var (ops, res) = painter.Build(doc, tileW, tileH, "/Helv", new PdfColorValue([options.Color.R, options.Color.G, options.Color.B]));
            tile = PdfObj.FormXObject(doc, ops, -tileW / 2 - 2, -tileH / 2 - 2, tileW / 2 + 2, tileH / 2 + 2, res);
        }

        var a = options.Rotation * Math.PI / 180;
        var (cos, sin) = (Math.Cos(a), Math.Sin(a));
        foreach (var p in pages.Where(p => p >= 0 && p < doc.PageCount).Distinct())
        {
            var page = doc.Pages[p];
            PageMarks.Remove(doc, page, Kind);
            var (w, h, matrix) = PageMarks.Frame(doc, page);
            var (tw, th) = (tileW, tileH);
            if (options.Image is { } im)
            {
                tw = w * options.ImageScale;
                th = tw * im.Height / im.Width;
            }
            IReadOnlyList<(double X, double Y)> centers = options.Layout == WatermarkLayout.Tiled ? TileCenters(w, h, tw, th, options.Rotation) : [(w / 2, h / 2)];
            var ops = new StringBuilder("/WmGS gs\n");
            foreach (var (cx, cy) in centers)
            {
                ops.Append($"q 1 0 0 1 {PdfObj.F(cx)} {PdfObj.F(cy)} cm {PdfObj.F(cos)} {PdfObj.F(sin)} {PdfObj.F(-sin)} {PdfObj.F(cos)} 0 0 cm ");
                // An image is a unit square: scale it to its size, centered on the origin.
                if (options.Image is not null) ops.Append($"{PdfObj.F(tw)} 0 0 {PdfObj.F(th)} {PdfObj.F(-tw / 2)} {PdfObj.F(-th / 2)} cm ");
                ops.Append("/WmTile Do Q\n");
            }
            var resources = new PdfDictionary(doc);
            resources.Elements["/ExtGState"] = new PdfDictionary(doc) { Elements = { ["/WmGS"] = gs.Reference! } };
            resources.Elements["/XObject"] = new PdfDictionary(doc) { Elements = { ["/WmTile"] = tile.Reference! } };
            var form = PdfObj.FormXObject(doc, Encoding.ASCII.GetBytes(ops.ToString()), 0, 0, w, h, resources, matrix);
            PageMarks.Add(doc, page, form, Kind, options.Behind, "<</Type /Pagination /Subtype /Watermark>>");
        }
        doc.Info.ModificationDate = DateTime.Now;
        return PdfObj.Save(doc);
    }

    /// <summary>Removes every watermark Inkvolt PDF added. Returns <paramref name="data"/> itself when there are none.</summary>
    public static byte[] Remove(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        var any = false;
        foreach (var page in doc.Pages) any |= PageMarks.Remove(doc, page, Kind);
        if (!any) return data;
        doc.Info.ModificationDate = DateTime.Now;
        return PdfObj.Save(doc);
    }

    /// <summary>True when the document has a watermark Inkvolt PDF added.</summary>
    public static bool Has(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import);
        return PageMarks.Has(doc, Kind);
    }
}
