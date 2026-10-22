using System.IO.Compression;
using System.Text;
using MuPDFCore;
using PdfDesk.Core;

namespace PdfDesk.Tests;

public class Core2RedactionTests
{
    /// <summary>Two pages; page 1 says TOPSECRET (uncompressed content) and has a comment and a form field.</summary>
    private static byte[] Secret(int rotate = 0) => Core2Pdf.Build([
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [8 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate {rotate} /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> /Annots [9 0 R 8 0 R] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
        Core2Pdf.Stream("BT /F1 24 Tf 72 700 Td (Public heading) Tj ET BT /F1 24 Tf 72 600 Td (Code TOPSECRET here) Tj ET"),
        Core2Pdf.Stream("BT /F1 24 Tf 72 700 Td (Second page stays) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (secretfield) /V (HIDDENVALUE) /Rect [72 400 300 420] /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Text /Rect [500 700 520 720] /Contents (NOTESECRET) >>",
    ]);

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void RemovesEverythingFromRedactedPagesOnly(int rotate)
    {
        var data = Secret(rotate);
        RectD area;
        using (var engine = PdfEngine.Open(data, InputFileTypes.PDF, null))
        {
            var t = engine.GetText(0);
            var (s, e) = t.Find("TOPSECRET", true, false).Single();
            area = t.GetRects(s, e).Aggregate(default(RectD), (a, r) => a.Union(r)).Inflate(1);
        }

        var redacted = Redaction.Apply(data, null, new Dictionary<int, IReadOnlyList<RectD>> { [0] = [area] }, dpi: 150);

        using (var engine = PdfEngine.Open(redacted, InputFileTypes.PDF, null))
        {
            Assert.Equal(2, engine.PageCount);
            Assert.DoesNotContain("TOPSECRET", engine.GetText(0).Text);
            Assert.True(engine.GetText(0).IsEmpty, "a redacted page is an image");
            Assert.Contains("Second page stays", engine.GetText(1).Text);
            Assert.Equal(engine.GetPage(0).Width, rotate == 90 ? 792 : 612, 0.5);
        }
        Assert.True(Core2Pdf.Ink(redacted, 0, area.Inflate(-2)) > 0.95, "area is black");
        // The rest of the page is still visible as pixels ("Public heading" draws ink where it was).
        Assert.True(Core2Pdf.Ink(data, 0, HeadingArea(data)) > 0.05);
        Assert.True(Core2Pdf.Ink(redacted, 0, HeadingArea(data)) > 0.05);

        Assert.Empty(Annotations.Read(redacted, null));
        Assert.Empty(FormEditor.Read(redacted, null));
        var everything = AllBytesInflated(redacted);
        foreach (var secret in new[] { "TOPSECRET", "HIDDENVALUE", "NOTESECRET" })
            Assert.DoesNotContain(secret, everything);
    }

    /// <summary>
    /// A tagged form: the widget on page 1 is also reachable through the structure tree (OBJR), the calculation order
    /// (/CO), the XFA datasets and a popup on page 2; a marked-content stream (MCR /Stm) holds more page-1 text.
    /// </summary>
    [Fact]
    public void NoSideChannelKeepsRedactedPageContentReachable()
    {
        var data = Core2Pdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [8 0 R] /CO [8 0 R] /XFA [(datasets) 10 0 R] >> /StructTreeRoot 11 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> /Annots [8 0 R 9 0 R] /StructParents 0 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> /Annots [13 0 R] >>",
            Core2Pdf.Stream("BT /F1 24 Tf 72 600 Td (Code TOPSECRET here) Tj ET"),
            Core2Pdf.Stream("BT /F1 24 Tf 72 700 Td (Second page stays) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (secretfield) /V (HIDDENVALUE) /Rect [72 400 300 420] /P 3 0 R /StructParent 1 >>",
            "<< /Type /Annot /Subtype /Text /Rect [500 700 520 720] /Contents (NOTESECRET) >>",
            Core2Pdf.Stream("<xfa:datasets><data><secretfield>XFASECRET</secretfield></data></xfa:datasets>"),
            "<< /Type /StructTreeRoot /K 12 0 R >>",
            "<< /Type /StructElem /S /Form /Pg 3 0 R /K [<< /Type /OBJR /Obj 8 0 R >> << /Type /MCR /MCID 0 /Stm 14 0 R >>] >>",
            "<< /Type /Annot /Subtype /Popup /Rect [500 600 600 700] /Parent 9 0 R >>",
            Core2Pdf.Stream("BT /F1 12 Tf 0 0 Td (STMSECRET) Tj ET", "/Type /XObject /Subtype /Form /BBox [0 0 100 100]"),
        ]);
        var everythingBefore = AllBytesInflated(data);
        foreach (var secret in new[] { "HIDDENVALUE", "XFASECRET", "STMSECRET", "NOTESECRET" })
            Assert.Contains(secret, everythingBefore);

        var redacted = Redaction.Apply(data, null, new Dictionary<int, IReadOnlyList<RectD>> { [0] = [new RectD(0, 0, 612, 792)] }, dpi: 72);

        var everything = AllBytesInflated(redacted);
        foreach (var secret in new[] { "TOPSECRET", "HIDDENVALUE", "XFASECRET", "STMSECRET", "NOTESECRET" })
            Assert.DoesNotContain(secret, everything);
        using var engine = PdfEngine.Open(redacted, InputFileTypes.PDF, null);
        Assert.Contains("Second page stays", engine.GetText(1).Text);
    }

    [Fact]
    public void NothingToRedactReturnsTheSameBytes()
    {
        var data = Secret();
        Assert.Same(data, Redaction.Apply(data, null, new Dictionary<int, IReadOnlyList<RectD>>()));
    }

    private static RectD HeadingArea(byte[] data)
    {
        using var engine = PdfEngine.Open(data, InputFileTypes.PDF, null);
        var t = engine.GetText(0);
        var (s, e) = t.Find("Public heading", true, false).Single();
        return t.GetRects(s, e).Aggregate(default(RectD), (a, r) => a.Union(r));
    }

    /// <summary>The file as text with every Flate stream inflated, to look for leftovers anywhere.</summary>
    private static string AllBytesInflated(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var sb = new StringBuilder(text);
        var at = 0;
        while ((at = text.IndexOf("stream", at, StringComparison.Ordinal)) >= 0)
        {
            var start = at + 6;
            if (start < text.Length && text[start] == '\r') start++;
            if (start < text.Length && text[start] == '\n') start++;
            var end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) break;
            try
            {
                using var z = new ZLibStream(new MemoryStream(pdf, start, end - start), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                z.CopyTo(ms);
                sb.Append(Encoding.Latin1.GetString(ms.ToArray()));
            }
            catch (InvalidDataException)
            {
            }
            at = end + 9;
        }
        return sb.ToString();
    }
}
