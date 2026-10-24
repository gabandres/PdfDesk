using MuPDFCore;
using InkvoltPdf.Core;
using ScanPage = InkvoltPrint.Core.Pdf.PdfPage;
using ScanPdfWriter = InkvoltPrint.Core.Pdf.PdfWriter;

namespace InkvoltPdf.Tests;

/// <summary>
/// Inkvolt suite: a scan becomes a PDF through Inkvolt Print's writer (each JPEG page embedded as it came from the scanner), then
/// gets Inkvolt PDF's OCR text layer and is inserted into a document, as File › Scan and Organize › Insert from Scanner do.
/// </summary>
public class ScanPdfTests
{
    /// <summary>A 40 × 56 px grayscale JPEG.</summary>
    private static readonly byte[] Jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAZABkAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxlZ2P/wAALCAA4ACgBAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEBAAA/AKtn4b6fJ+lay6NbWcayXcsUCE7Q0rhQT6ZP0qu+t6LbRg2wlvHIOAkZUA9slsYz7A1Xm8Q6jcLtsbSKzBA+Y/vHBz2JAHTjkGq81nqOqNm+uZZgSG2E4QEDGQo4HHoKkm1LXL9dglW1QgArbLt6HOdxyw/A02PQprmYzXDSTStjc8jFmPbkmtSPQobaEzXDRwxLjc8jBVHbkmmzalodg2wStdOCAVtl3dRnO44U/gapv4kvZowtjp8VsSCC7sZCPQjgAY9wasTeIdOt222NpLeEEfMf3aEY7EgnrxyBVN9X1y8jEaGK1BBDGCPBbPuSSMeoxUa6Nc3kiyXcss7gbQ0rliB6ZP1rUt/DyRRtJIFREBZmbgKB1JNE17oenrzcrcPgEJbDzM846j5fwJqR9MstNjEl9PFACCQHYAtjrgdT9BVebXtJtlxZQy3r4BBC+WnXkEnnpz0NV5te1a5bFlDFZJkEEL5j9OQSeOvPQVVGk3l7s+2Tz3GzO3zZC+3PXGenQVqWfhvp8n6Vn2fhvp8n6VrLo1tZxrJdyxQITtDSuFBPpk/Sq763ottGDbCW8cg4CRlQD2yWxjPsDVebxDqNwu2xtIrMED5j+8cHPYkAdOOQarzWeo6o2b65lmBIbYThAQMZCjgcegqSbUtcv12CVbVCACtsu3oc53HLD8DTY9CmuZjNcNJNK2NzyMWY9uSa1I9ChtoTNcNHDEuNzyMFUduSabNqWh2DbBK104IBW2Xd1Gc7jhT+Bqm/iS9mjC2OnxWxIILuxkI9COABj3BqxN4h063bbY2kt4QR8x/doRjsSCevHIFU31fXLyMRoYrUEEMYI8Fs+5JIx6jFRro1zeSLJdyyzuBtDSuWIHpk/WtS38PJFG0kgVEQFmZuAoHUk0TXuh6evNytw+AQlsPMzzjqPl/Amv/Z");

    private static byte[] ScanPdf(int pages, int dpi = 100)
    {
        using var ms = new MemoryStream();
        ScanPdfWriter.Write(ms, [.. Enumerable.Range(0, pages).Select(_ => new ScanPage(Jpeg, dpi))], "Scan 2026-10-23 0900");
        return ms.ToArray();
    }

    [Fact]
    public void ScannedPagesOpenAtTheirScannedSize()
    {
        using var engine = PdfEngine.Open(ScanPdf(2), InputFileTypes.PDF, null);
        Assert.Equal(2, engine.PageCount);
        var g = engine.GetPage(1);
        Assert.Equal(40 * 72.0 / 100, g.Width, 1);
        Assert.Equal(56 * 72.0 / 100, g.Height, 1);
        Assert.False(TextLayer.HasText(engine, 0)); // nothing to search until OCR adds a layer
    }

    [Fact]
    public void TextLayerAndInsertKeepTheScannedJpegAsIs()
    {
        var scan = ScanPdf(2);
        var words = new Dictionary<int, IReadOnlyList<OcrWord>> { [0] = [new OcrWord("Invoice", new RectD(2, 2, 20, 8))] };
        var withText = TextLayer.AddInvisibleText(scan, null, words);
        using (var e = PdfEngine.Open(withText, InputFileTypes.PDF, null))
            Assert.Contains("Invoice", e.GetText(0).Text);

        var result = PageEditor.Insert(TestPdf.Make(3), null, new PdfSource(withText, null, "Scan.pdf"), null, 1);
        using var r = PdfEngine.Open(result, InputFileTypes.PDF, null);
        Assert.Equal(5, r.PageCount);
        Assert.Contains("Page 1", r.GetText(0).Text);
        Assert.Contains("Invoice", r.GetText(1).Text);
        Assert.Contains("Page 2", r.GetText(3).Text);
        Assert.True(result.AsSpan().IndexOf(Jpeg) >= 0, "the JPEG should be copied byte for byte, not re-encoded");
    }
}
