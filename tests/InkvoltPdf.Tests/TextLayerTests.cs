using MuPDFCore;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class TextLayerTests
{
    /// <summary>A page that is only an image (no text): a synthetic page rasterized by redacting a blank corner.</summary>
    private static byte[] Scanned(int rotate)
    {
        var data = TestPdf.Make(1);
        if (rotate != 0) data = PageEditor.Rotate(data, null, [0], rotate);
        return Redaction.Apply(data, null, new Dictionary<int, IReadOnlyList<RectD>> { [0] = [new RectD(0, 0, 2, 2)] }, dpi: 72);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(270)]
    public void InvisibleWordsAreFoundWhereTheyWereRecognized(int rotate)
    {
        var scan = Scanned(rotate);
        using (var before = PdfEngine.Open(scan, InputFileTypes.PDF, null))
            Assert.False(TextLayer.HasText(before, 0));

        OcrWord[] words =
        [
            new("Invoice", new RectD(72, 300, 90, 24)),
            new("number", new RectD(170, 300, 80, 24)),
            new("Total:", new RectD(72, 340, 50, 12)),
            new("€1,234", new RectD(130, 340, 55, 12)),
            new("مرحبا", new RectD(300, 340, 60, 14)),
        ];
        var data = TextLayer.AddInvisibleText(scan, null, new Dictionary<int, IReadOnlyList<OcrWord>> { [0] = words });

        using var engine = PdfEngine.Open(data, InputFileTypes.PDF, null);
        Assert.True(TextLayer.HasText(engine, 0));
        var text = engine.GetText(0);
        Assert.Contains("Invoice number", text.Text);
        Assert.Contains("Total: €1,234", text.Text);
        Assert.Contains("مرحبا", text.Text);
        Assert.True(text.Text.IndexOf("Invoice", StringComparison.Ordinal) < text.Text.IndexOf("Total", StringComparison.Ordinal), "reading order");
        foreach (var w in words)
        {
            var (s, e) = text.Find(w.Text, true, false).Single();
            var box = text.GetRects(s, e).Aggregate(default(RectD), (a, r) => a.Union(r));
            Assert.InRange(box.X, w.Rect.X - 1.5, w.Rect.X + 1.5);
            Assert.InRange(box.Right, w.Rect.Right - 1.5, w.Rect.Right + 1.5);
            Assert.InRange(box.Y, w.Rect.Y - 1.5, w.Rect.Y + 1.5);
            Assert.InRange(box.Bottom, w.Rect.Bottom - 1.5, w.Rect.Bottom + 1.5);
        }
        // Invisible: the page looks exactly as before.
        Assert.Equal(Core2Pdf.Ink(scan, 0, new RectD(60, 290, 320, 70)), Core2Pdf.Ink(data, 0, new RectD(60, 290, 320, 70)), 3);
    }

    [Fact]
    public void ExistingContentStateDoesNotLeakIntoTheTextLayer()
    {
        // The page's content ends inside an unbalanced q with a scaling cm: the layer must still land where asked.
        var data = Core2Pdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            Core2Pdf.Stream("q 2 0 0 2 0 0 cm 0 0 10 10 re f"),
        ]);
        var withText = TextLayer.AddInvisibleText(data, null, new Dictionary<int, IReadOnlyList<OcrWord>> { [0] = [new("Hello", new RectD(100, 100, 50, 20))] });
        using var engine = PdfEngine.Open(withText, InputFileTypes.PDF, null);
        var t = engine.GetText(0);
        var (s, e) = t.Find("Hello", true, false).Single();
        var box = t.GetRects(s, e).Aggregate(default(RectD), (a, r) => a.Union(r));
        Assert.InRange(box.X, 98.5, 101.5);
        Assert.InRange(box.Y, 98.5, 101.5);
    }

    [Theory]
    [InlineData("q q 2 0 0 2 0 0 cm 0 0 10 10 re f")] // two unbalanced saves
    [InlineData("Q 2 0 0 2 0 0 cm q 3 0 0 3 0 0 cm 0 0 10 10 re f")] // a restore with nothing saved, then a save
    public void UnbalancedContentDoesNotMoveTheTextLayer(string content)
    {
        var data = Core2Pdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            Core2Pdf.Stream(content),
        ]);
        var withText = TextLayer.AddInvisibleText(data, null, new Dictionary<int, IReadOnlyList<OcrWord>> { [0] = [new("Hello", new RectD(100, 100, 50, 20))] });
        using var engine = PdfEngine.Open(withText, InputFileTypes.PDF, null);
        var t = engine.GetText(0);
        var (s, e) = t.Find("Hello", true, false).Single();
        var box = t.GetRects(s, e).Aggregate(default(RectD), (a, r) => a.Union(r));
        Assert.InRange(box.X, 98.5, 101.5);
        Assert.InRange(box.Y, 98.5, 101.5);
        Assert.InRange(box.Width, 48, 52);
    }

    [Fact]
    public void NoWordsReturnsTheSameBytes()
    {
        var scan = Scanned(0);
        Assert.Same(scan, TextLayer.AddInvisibleText(scan, null, new Dictionary<int, IReadOnlyList<OcrWord>> { [0] = [new(" ", new RectD(1, 1, 5, 5))] }));
    }
}
