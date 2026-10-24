using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MuPDFCore;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

/// <summary>Synthetic image files for the image and optimizer tests.</summary>
internal static class TestImages
{
    /// <summary>An RGBA PNG (optionally with a pHYs resolution).</summary>
    public static byte[] Png(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> pixel, double? dpi = null)
    {
        var raw = new byte[h * (1 + 4 * w)];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (r, g, b, a) = pixel(x, y);
            var o = y * (1 + 4 * w) + 1 + 4 * x;
            (raw[o], raw[o + 1], raw[o + 2], raw[o + 3]) = (r, g, b, a);
        }
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        (ihdr[8], ihdr[9]) = (8, 6);
        Chunk(ms, "IHDR", ihdr);
        if (dpi is { } d)
        {
            var phys = new byte[9];
            var ppm = (uint)Math.Round(d / 0.0254);
            BinaryPrimitives.WriteUInt32BigEndian(phys, ppm);
            BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), ppm);
            phys[8] = 1;
            Chunk(ms, "pHYs", phys);
        }
        Chunk(ms, "IDAT", PdfObjDeflate(raw));
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] PdfObjDeflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        Encoding.ASCII.GetBytes(type, head.AsSpan(4));
        s.Write(head);
        s.Write(data);
        var crc = Crc32([.. head.AsSpan(4).ToArray(), .. data]);
        var tail = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        s.Write(tail);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>A w x h pixel JPEG (rendered by MuPDF): white with a red top-left quarter.</summary>
    public static byte[] Jpeg(int w, int h)
    {
        var pdf = Core2Pdf.Build(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Contents 4 0 R >>",
            Core2Pdf.Stream($"1 0 0 rg 0 {h / 2} {w / 2} {h - h / 2} re f"),
        ]);
        using var ctx = new MuPDFContext();
        var doc = new MuPDFDocument(ctx, pdf, InputFileTypes.PDF);
        try
        {
            using var ms = new MemoryStream();
            doc.WriteImageAsJPEG(0, 1, ms, 90, false);
            return ms.ToArray();
        }
        finally
        {
            PdfEngine.DisposeDocument(doc);
        }
    }

    /// <summary>The JPEG with an EXIF APP1 segment (big-endian TIFF, one IFD entry: Orientation) right after SOI.</summary>
    public static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        var tiff = new byte[] { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0, 0 };
        var payload = (byte[])[.. "Exif\0\0"u8.ToArray(), .. tiff];
        var seg = new byte[4 + payload.Length];
        (seg[0], seg[1]) = (0xFF, 0xE1);
        BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(seg, 4);
        return [.. jpeg.AsSpan(0, 2), .. seg, .. jpeg.AsSpan(2)];
    }

    /// <summary>The color at a point of a page (page space) rendered at 1x, as RGB.</summary>
    public static (int R, int G, int B) PixelAt(byte[] pdf, int page, double x, double y)
    {
        using var engine = PdfEngine.Open(pdf, InputFileTypes.PDF, null);
        var img = engine.Render(page, 1, new RectD(x, y, 1, 1));
        return (img.Pixels[2], img.Pixels[1], img.Pixels[0]);
    }
}
