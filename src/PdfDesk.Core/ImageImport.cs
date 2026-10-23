using System.Buffers.Binary;
using System.Text;
using MuPDFCore;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfDesk.Core;

/// <summary>Page size for images turned into PDF pages.</summary>
public enum PageSizeOption
{
    /// <summary>Each page the size of its image at the image's resolution (96 dpi when the file doesn't say).</summary>
    FitImage,
    /// <summary>US Letter, turned to landscape for wide images, the image centered within half-inch margins.</summary>
    Letter,
    /// <summary>A4, turned to landscape for wide images, the image centered within half-inch margins.</summary>
    A4,
}

/// <summary>
/// Turns image files into a PDF, one page per image (per page of a multi-page TIFF). JPEG files are embedded as they
/// are (DCTDecode, no recompression), turned upright as their EXIF orientation says. PNG, BMP, GIF and TIFF are decoded
/// by MuPDF and stored losslessly (Flate), with an /SMask when they have transparency. On Letter/A4 pages an image is
/// shrunk to fit the margins but never enlarged.
/// </summary>
public static class ImageImport
{
    /// <summary>Resolution assumed when an image file doesn't state one.</summary>
    public const double DefaultDpi = 96;

    private const double Margin = 36;

    private sealed record Picture(PdfDictionary Image, int Width, int Height, double DpiX, double DpiY, int Orientation);

    /// <exception cref="ArgumentException">No images were given.</exception>
    /// <exception cref="InvalidOperationException">A file isn't an image that can be read (the message names it).</exception>
    public static byte[] ImagesToPdf(IReadOnlyList<(byte[] Bytes, string Name)> images, PageSizeOption pageSize = PageSizeOption.FitImage)
    {
        if (images.Count == 0) throw new ArgumentException("There are no images to convert.", nameof(images));
        using var doc = new PdfDocument();
        foreach (var (bytes, name) in images)
        {
            var pictures = Sniff(bytes, name) switch
            {
                InputFileTypes.JPEG when Jpeg(doc, bytes) is { } jpeg => [jpeg],

                { } type => Decoded(doc, bytes, type, name),
                null => throw new InvalidOperationException($"\"{name}\" isn't an image PdfDesk can read (JPEG, PNG, BMP, GIF, TIFF)."),
            };
            foreach (var p in pictures) AddPage(doc, p, pageSize);
        }
        doc.Info.CreationDate = DateTime.Now;
        return PdfObj.Save(doc);
    }

    private static void AddPage(PdfDocument doc, Picture p, PageSizeOption size)
    {
        var w = p.Width * 72 / p.DpiX;
        var h = p.Height * 72 / p.DpiY;
        var sideways = p.Orientation is >= 5 and <= 8;
        var (dw, dh) = sideways ? (h, w) : (w, h);

        double pw, ph, x, y;
        if (size == PageSizeOption.FitImage)
        {
            (pw, ph, x, y) = (dw, dh, 0, 0);
        }
        else
        {
            (pw, ph) = size == PageSizeOption.A4 ? (595.28, 841.89) : (612, 792);
            if (dw > dh) (pw, ph) = (ph, pw);
            var scale = Math.Min(1, Math.Min((pw - 2 * Margin) / dw, (ph - 2 * Margin) / dh));
            (dw, dh) = (dw * scale, dh * scale);
            (x, y) = ((pw - dw) / 2, (ph - dh) / 2);
        }

        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(pw);
        page.Height = XUnit.FromPoint(ph);
        var m = Placement(p.Orientation, x, y, dw, dh);
        var ops = $"q {string.Join(' ', m.Select(PdfObj.F))} cm /Im0 Do Q\n";
        page.Elements["/Contents"] = PdfObj.AddStream(doc, Encoding.ASCII.GetBytes(ops)).Reference!;
        var xobjects = new PdfDictionary(doc);
        xobjects.Elements["/Im0"] = p.Image.Reference!;
        var resources = new PdfDictionary(doc);
        resources.Elements["/XObject"] = xobjects;
        page.Elements["/Resources"] = resources;
    }

    /// <summary>
    /// The image matrix drawing a stored image upright in the box (x, y, w, h) for EXIF orientation 1-8 (TIFF 6.0
    /// §Orientation: where the stored first row and first column go).
    /// </summary>
    internal static double[] Placement(int orientation, double x, double y, double w, double h)
    {
        // Displayed fractions (X right, T down) from stored fractions (s right, t down): X = ax s + bx t + cx, T = at s + bt t + ct.
        var (ax, bx, cx, at, bt, ct) = orientation switch
        {
            2 => (-1, 0, 1, 0, 1, 0),
            3 => (-1, 0, 1, 0, -1, 1),
            4 => (1, 0, 0, 0, -1, 1),
            5 => (0, 1, 0, 1, 0, 0),
            6 => (0, -1, 1, 1, 0, 0),
            7 => (0, -1, 1, -1, 0, 1),
            8 => (0, 1, 0, -1, 0, 1),
            _ => (1, 0, 0, 0, 1, 0),
        };
        // Image space: u = s, v = 1 - t (the first row is at the top); page: px = x + w X, py = y + h (1 - T).
        return [w * ax, -h * at, -w * bx, h * bt, x + w * (bx + cx), y + h - h * (bt + ct)];
    }

    // ---- JPEG: embedded as is ----

    private static Picture? Jpeg(PdfDocument doc, byte[] b)
    {
        if (ReadJpeg(b) is not { } info) return null;
        var image = PdfObj.AddStream(doc, b, compress: false);
        image.Elements["/Type"] = new PdfName("/XObject");
        image.Elements["/Subtype"] = new PdfName("/Image");
        image.Elements["/Width"] = new PdfInteger(info.Width);
        image.Elements["/Height"] = new PdfInteger(info.Height);
        image.Elements["/BitsPerComponent"] = new PdfInteger(8);
        image.Elements["/ColorSpace"] = new PdfName(info.Components switch { 1 => "/DeviceGray", 4 => "/DeviceCMYK", _ => "/DeviceRGB" });
        // Adobe (Photoshop) CMYK JPEGs store inverted values.
        if (info.Components == 4 && info.Adobe) image.Elements["/Decode"] = PdfObj.Reals(doc, 1, 0, 1, 0, 1, 0, 1, 0);
        image.Elements["/Filter"] = new PdfName("/DCTDecode");
        return new Picture(image, info.Width, info.Height, info.DpiX ?? DefaultDpi, info.DpiY ?? info.DpiX ?? DefaultDpi, info.Orientation);
    }

    internal sealed record JpegInfo(int Width, int Height, int Components, double? DpiX, double? DpiY, int Orientation, bool Adobe);

    /// <summary>Size, components, resolution and EXIF orientation from a JPEG's markers (null if it isn't a usable JPEG).</summary>
    internal static JpegInfo? ReadJpeg(byte[] b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;
        int width = 0, height = 0, comps = 0, orientation = 1;
        double? dpiX = null, dpiY = null, exifX = null, exifY = null;
        var adobe = false;
        var i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF)
            {
                i++;
                continue;
            }
            var marker = b[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                i += 2;
                continue;
            }
            if (marker == 0xD9) break;
            var len = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 2));
            if (len < 2 || i + 2 + len > b.Length) break;
            var seg = b.AsSpan(i + 4, len - 2);
            switch (marker)
            {
                case 0xE0 when seg.Length >= 12 && seg[..5].SequenceEqual("JFIF\0"u8):
                {
                    var unit = seg[7];
                    double xd = BinaryPrimitives.ReadUInt16BigEndian(seg[8..]), yd = BinaryPrimitives.ReadUInt16BigEndian(seg[10..]);
                    var f = unit switch { 1 => 1.0, 2 => 2.54, _ => 0 };
                    if (f > 0 && xd > 0 && yd > 0) (dpiX, dpiY) = (xd * f, yd * f);
                    break;
                }
                case 0xE1 when seg.Length >= 14 && seg[..6].SequenceEqual("Exif\0\0"u8):
                {
                    var tiff = Tiff.Read(seg[6..].ToArray());
                    if (tiff.Orientation is >= 1 and <= 8) orientation = tiff.Orientation;
                    (exifX, exifY) = (tiff.DpiX, tiff.DpiY);
                    break;
                }
                case 0xEE when seg.Length >= 12 && seg[..5].SequenceEqual("Adobe"u8):
                    adobe = true;
                    break;
                case >= 0xC0 and <= 0xCF when marker is not (0xC4 or 0xC8 or 0xCC) && seg.Length >= 6:
                    if (seg[0] != 8) return null; // 12-bit and lossless JPEGs: PDF's DCTDecode wants 8 bits
                    height = BinaryPrimitives.ReadUInt16BigEndian(seg[1..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(seg[3..]);
                    comps = seg[5];
                    break;
            }
            if (marker == 0xDA) break;
            i += 2 + len;
        }
        if (width <= 0 || height <= 0 || comps is not (1 or 3 or 4)) return null;
        return new JpegInfo(width, height, comps, dpiX ?? exifX, dpiY ?? exifY, orientation, adobe);
    }

    // ---- Other formats: decoded by MuPDF ----

    private static List<Picture> Decoded(PdfDocument doc, byte[] bytes, InputFileTypes type, string name)
    {
        var (pxW, pxH, dpiX, dpiY) = Header(bytes, type);
        using var ctx = new MuPDFContext(64 * 1024 * 1024);
        MuPDFDocument mu;
        try
        {
            mu = new MuPDFDocument(ctx, bytes, type);
        }
        catch (Exception ex) when (ex is MuPDFException or ArgumentException)
        {
            throw new InvalidOperationException($"\"{name}\" can't be read as an image: {ex.Message}", ex);
        }
        try
        {
            var list = new List<Picture>();
            for (var i = 0; i < mu.Pages.Count; i++)
            {
                var bounds = mu.Pages[i].Bounds;
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                // Native pixels: MuPDF sizes an image page from the image's resolution, which the header tells.
                var scale = pxW > 0 ? pxW / bounds.Width : 1;
                var px = bounds.Round(scale);
                if ((double)px.Width * px.Height > PdfEngine.MaxPixels)
                    throw new InvalidOperationException($"\"{name}\" is too large ({px.Width} x {px.Height} pixels).");
                var rgba = mu.Render(i, scale, PixelFormats.RGBA, includeAnnotations: false);
                if (rgba.Length != px.Width * px.Height * 4) continue;
                var dx = dpiX ?? (pxW > 0 ? DefaultDpi : 72);
                var dy = dpiY ?? dpiX ?? (pxW > 0 ? DefaultDpi : 72);
                list.Add(new Picture(Store(doc, px.Width, px.Height, rgba), px.Width, px.Height, dx, dy, 1));
            }
            if (list.Count == 0) throw new InvalidOperationException($"\"{name}\" has no image MuPDF can draw.");
            return list;
        }
        finally
        {
            PdfEngine.DisposeDocument(mu);
        }
    }

    /// <summary>An image XObject (Flate, gray when every pixel is) from RGBA, with an /SMask for any transparency.</summary>
    internal static PdfDictionary Store(PdfDocument doc, int width, int height, byte[] rgba)
    {
        var n = width * height;
        var alpha = new byte[n];
        var opaque = true;
        var gray = true;
        for (var k = 0; k < n; k++)
        {
            // MuPDFCore hands out straight (not premultiplied) color.
            alpha[k] = rgba[4 * k + 3];
            if (alpha[k] != 255) opaque = false;
        }
        for (var k = 0; k < n && gray; k++) gray = rgba[4 * k] == rgba[4 * k + 1] && rgba[4 * k] == rgba[4 * k + 2];
        var samples = new byte[gray ? n : n * 3];
        for (var k = 0; k < n; k++)
        {
            if (gray) samples[k] = rgba[4 * k];
            else
            {
                samples[3 * k] = rgba[4 * k];
                samples[3 * k + 1] = rgba[4 * k + 1];
                samples[3 * k + 2] = rgba[4 * k + 2];
            }
        }
        var image = ImageStream(doc, width, height, samples, gray ? "/DeviceGray" : "/DeviceRGB");
        if (!opaque) image.Elements["/SMask"] = ImageStream(doc, width, height, alpha, "/DeviceGray").Reference!;
        return image;
    }

    internal static PdfDictionary ImageStream(PdfDocument doc, int width, int height, byte[] samples, string colorSpace)
    {
        var image = PdfObj.AddStream(doc, samples);
        image.Elements["/Type"] = new PdfName("/XObject");
        image.Elements["/Subtype"] = new PdfName("/Image");
        image.Elements["/Width"] = new PdfInteger(width);
        image.Elements["/Height"] = new PdfInteger(height);
        image.Elements["/ColorSpace"] = new PdfName(colorSpace);
        image.Elements["/BitsPerComponent"] = new PdfInteger(8);
        return image;
    }

    /// <summary>The image type from the file's magic bytes, else its extension.</summary>
    internal static InputFileTypes? Sniff(byte[] b, string name)
    {
        var s = b.AsSpan();
        if (s.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return InputFileTypes.JPEG;
        if (s.StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' })) return InputFileTypes.PNG;
        if (s.StartsWith("GIF8"u8)) return InputFileTypes.GIF;
        if (s.StartsWith("BM"u8)) return InputFileTypes.BMP;
        if (s.StartsWith("II*\0"u8) || s.StartsWith("MM\0*"u8)) return InputFileTypes.TIFF;
        var type = PdfEngine.TypeFromPath(name);
        return type is InputFileTypes.PNG or InputFileTypes.JPEG or InputFileTypes.BMP or InputFileTypes.GIF or InputFileTypes.TIFF ? type : null;
    }

    /// <summary>Pixel size and resolution from the file header (0 / null where it doesn't say or can't be read).</summary>
    private static (int Width, int Height, double? DpiX, double? DpiY) Header(byte[] b, InputFileTypes type)
    {
        try
        {
            switch (type)
            {
                case InputFileTypes.PNG when b.Length >= 24:
                {
                    var w = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16));
                    var h = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20));
                    double? dx = null, dy = null;
                    for (var i = 8; i + 8 <= b.Length;)
                    {
                        var len = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(i));
                        if (len < 0 || i + 12L + len > b.Length) break;
                        var kind = b.AsSpan(i + 4, 4);
                        if (kind.SequenceEqual("pHYs"u8) && len >= 9 && b[i + 16] == 1)
                        {
                            dx = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i + 8)) * 0.0254;
                            dy = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i + 12)) * 0.0254;
                        }
                        if (kind.SequenceEqual("IDAT"u8)) break;
                        i += 12 + len;
                    }
                    return (w, h, Sane(dx), Sane(dy));
                }
                case InputFileTypes.GIF when b.Length >= 10:
                    return (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)), null, null);
                case InputFileTypes.BMP when b.Length >= 26:
                {
                    var size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(14));
                    if (size == 12) return (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(18)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(20)), null, null);
                    if (size < 40 || b.Length < 46) return (0, 0, null, null);
                    var w = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(18));
                    var h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(22)));
                    var dx = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(38)) * 0.0254;
                    var dy = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(42)) * 0.0254;
                    return (w, h, Sane(dx), Sane(dy));
                }
                case InputFileTypes.TIFF:
                {
                    var t = Tiff.Read(b);
                    return (t.Width, t.Height, Sane(t.DpiX), Sane(t.DpiY));
                }
            }
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        return (0, 0, null, null);
    }

    private static double? Sane(double? dpi) => dpi is >= 10 and <= 10_000 ? dpi : null;

    /// <summary>The first IFD of a TIFF structure (a TIFF file, or the EXIF block of a JPEG).</summary>
    private static class Tiff
    {
        public readonly record struct Info(int Width, int Height, double? DpiX, double? DpiY, int Orientation);

        public static Info Read(byte[] b)
        {
            if (b.Length < 8) return default;
            var little = b[0] == 'I';
            if (!little && b[0] != 'M') return default;
            uint U32(long at) => at < 0 || at + 4 > b.Length ? 0 : little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)at)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)at));
            ushort U16(long at) => at < 0 || at + 2 > b.Length ? (ushort)0 : little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)at)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan((int)at));
            long ifd = U32(4);
            int count = U16(ifd);
            int width = 0, height = 0, orientation = 1, unit = 2;
            double? dx = null, dy = null;
            for (var k = 0; k < count && k < 512; k++)
            {
                var e = ifd + 2 + 12L * k;
                if (e + 12 > b.Length) break;
                var tag = U16(e);
                var fieldType = U16(e + 2);
                long Value() => fieldType == 3 ? U16(e + 8) : U32(e + 8);
                double? Rational()
                {
                    long at = U32(e + 8);
                    var den = U32(at + 4);
                    return den == 0 ? null : U32(at) / (double)den;
                }
                switch (tag)
                {
                    case 256: width = (int)Value(); break;
                    case 257: height = (int)Value(); break;
                    case 274: orientation = (int)Value(); break;
                    case 282: dx = Rational(); break;
                    case 283: dy = Rational(); break;
                    case 296: unit = (int)Value(); break;
                }
            }
            var f = unit switch { 2 => 1.0, 3 => 2.54, _ => 0 };
            return new Info(width, height, f > 0 ? dx * f : null, f > 0 ? dy * f : null, orientation);
        }
    }
}
