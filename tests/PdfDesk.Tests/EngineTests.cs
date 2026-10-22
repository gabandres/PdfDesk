using MuPDFCore;
using PdfDesk.Core;

namespace PdfDesk.Tests;

public class EngineTests
{
    [Fact]
    public void RendersPageAtScale()
    {
        using var engine = PdfEngine.Open(TestPdf.Make(2), InputFileTypes.PDF, null);
        Assert.Equal(2, engine.PageCount);
        var g = engine.GetPage(0);
        Assert.Equal(612, g.Width, 0.5);
        Assert.Equal(792, g.Height, 0.5);

        var img = engine.Render(0, 0.5);
        Assert.Equal(306, img.Width, 1.0);
        Assert.Equal(396, img.Height, 1.0);
        Assert.Equal(img.Width * img.Height * 3, img.Pixels.Length);
        Assert.Equal(255, img.Pixels[0]); // white background at the corner
        Assert.Contains(img.Pixels, b => b < 64); // some dark text pixels
    }

    [Fact]
    public void RendersRegion()
    {
        using var engine = PdfEngine.Open(TestPdf.Make(1), InputFileTypes.PDF, null);
        var img = engine.Render(0, 2, new RectD(100, 100, 50, 25));
        Assert.Equal(100, img.Width, 1.0);
        Assert.Equal(50, img.Height, 1.0);
    }

    [Fact]
    public void ExtractsTextWithBoxesAndFinds()
    {
        using var engine = PdfEngine.Open(TestPdf.Make(3), InputFileTypes.PDF, null);
        var text = engine.GetText(1);
        Assert.Contains("Page 2 alpha", text.Text);

        var hits = text.Find("QUICK brown", matchCase: false, wholeWord: true);
        Assert.Single(hits);
        var rects = text.GetRects(hits[0].Start, hits[0].End);
        Assert.Single(rects);
        Assert.InRange(rects[0].X, 100, 125);  // "The " starts at x = 72pt, so "quick" sits near 112pt
        Assert.InRange(rects[0].Y, 110, 145);  // baseline at y = 140pt, top-down

        // Whitespace in the query spans line breaks.
        Assert.Single(text.Find("fox jumps", false, false));
        Assert.Empty(text.Find("QUICK", matchCase: true, false));
        Assert.Empty(text.Find("qui", false, wholeWord: true));

        var caret = text.HitTest(rects[0].X + 1, rects[0].CenterY);
        Assert.Equal(hits[0].Start, caret);
        var word = text.WordAt(caret + 1);
        Assert.Equal("quick", text.GetString(word.Start, word.End));
    }

    [Fact]
    public void DisposedEnginesLeaveNothingForTheFinalizer()
    {
        // MuPDFCore crashes the process from a finalizer if a page outlives its context.
        for (var i = 0; i < 3; i++)
        {
            using var engine = PdfEngine.Open(TestPdf.Make(4), InputFileTypes.PDF, null);
            engine.Render(1, 0.3);
            engine.GetText(2);
            engine.GetLinks(3);
            engine.GetOutline();
        }
        Assert.Throws<PasswordRequiredException>(() => PdfEngine.Open(TestPdf.Encrypted(2, "pw"), InputFileTypes.PDF, null));
        Assert.Throws<PasswordRequiredException>(() => PdfEngine.Open(TestPdf.Encrypted(2, "pw"), InputFileTypes.PDF, "wrong"));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public void UnencryptedDocumentIgnoresPassword()
    {
        using var engine = PdfEngine.Open(TestPdf.Make(1), InputFileTypes.PDF, "whatever");
        Assert.False(engine.IsEncrypted);
    }
}
