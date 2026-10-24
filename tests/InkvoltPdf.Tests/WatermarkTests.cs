using InkvoltPdf.Core;
using PdfSharp.Pdf;

namespace InkvoltPdf.Tests;

public class WatermarkTests
{
    /// <summary>A page whose content paints it opaque white, as a scanned page does.</summary>
    private static byte[] OpaquePage() => Core2Pdf.Build(
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        Core2Pdf.Stream("1 1 1 rg 0 0 612 792 re f"),
    ]);

    private static StampImage Red(int w = 4, int h = 4) =>
        new(w, h, [.. Enumerable.Range(0, w * h).SelectMany(_ => new byte[] { 220, 0, 0 })], null);

    private static int ContentCount(byte[] pdf)
    {
        using var doc = Core2Pdf.Open(pdf);
        return PdfObj.Array(doc.Pages[0].Elements["/Contents"])?.Elements.Count ?? 1;
    }

    [Fact]
    public void TilesCoverThePageWithinTheLimit()
    {
        var centers = Watermarks.TileCenters(612, 792, 200, 40, 45);
        Assert.InRange(centers.Count, 6, Watermarks.MaxTiles);
        // Every corner region of the page has a tile near it.
        foreach (var (x, y) in new[] { (0.0, 0.0), (612.0, 0.0), (0.0, 792.0), (612.0, 792.0), (306.0, 396.0) })
            Assert.Contains(centers, c => Math.Abs(c.X - x) < 260 && Math.Abs(c.Y - y) < 260);
        // A tiny tile can't explode the content.
        Assert.InRange(Watermarks.TileCenters(612, 792, 2, 2, 0).Count, 1, Watermarks.MaxTiles);
        Assert.Equal((40.0, 200.0), Watermarks.TurnedExtent(200, 40, 90) is var (w, h) ? (Math.Round(w, 6), Math.Round(h, 6)) : default);
    }

    [Fact]
    public void TextIsTurnedAndCentered()
    {
        var pdf = TestPdf.Make(2);
        var result = Watermarks.Apply(pdf, null, [1], new WatermarkOptions { Text = "CONFIDENTIAL", Rotation = 90, Opacity = 1, Color = new AnnotColor(0, 0, 0) });
        Assert.DoesNotContain("CONFIDENTIAL", Core2Pdf.AllText(result, 0));
        var line = Core2Pdf.Lines(result, 1, annotations: false).Single(l => l.Text == "CONFIDENTIAL");
        Assert.True(line.Box.Height > line.Box.Width * 3); // upright: turned a quarter
        Assert.InRange(line.Box.CenterX, 286, 326);
        Assert.InRange(line.Box.CenterY, 376, 416);
        Assert.Equal([1, 2], TestPdf.PageNumbers(result));
        Assert.True(Watermarks.Has(result, null));
    }

    [Fact]
    public void OpacityLetsThePageShowThrough()
    {
        var pdf = OpaquePage();
        var faint = Watermarks.Apply(pdf, null, [0], new WatermarkOptions { Image = Red(), ImageScale = 0.5, Rotation = 0, Opacity = 0.3 });
        var (r, g, _) = TestImages.PixelAt(faint, 0, 306, 396);
        Assert.InRange(r, 230, 255);
        Assert.InRange(g, 150, 200); // 30% red over white
        var solid = Watermarks.Apply(pdf, null, [0], new WatermarkOptions { Image = Red(), ImageScale = 0.5, Rotation = 0, Opacity = 1 });
        Assert.True(TestImages.PixelAt(solid, 0, 306, 396).G < 30);
        // Half the page's width, centered: the edges stay white.
        Assert.True(TestImages.PixelAt(solid, 0, 100, 396).G > 240);
    }

    [Fact]
    public void BehindIsHiddenByOpaqueContent()
    {
        var pdf = OpaquePage();
        var behind = Watermarks.Apply(pdf, null, [0], new WatermarkOptions { Image = Red(), ImageScale = 1, Opacity = 1, Behind = true, Rotation = 0 });
        Assert.True(TestImages.PixelAt(behind, 0, 306, 396).G > 240);
        Assert.True(Watermarks.Has(behind, null));
        var over = Watermarks.Apply(pdf, null, [0], new WatermarkOptions { Image = Red(), ImageScale = 1, Opacity = 1, Rotation = 0 });
        Assert.True(TestImages.PixelAt(over, 0, 306, 396).G < 30);
    }

    [Fact]
    public void TiledRepeatsTheText()
    {
        var result = Watermarks.Apply(TestPdf.Make(1), null, [0], new WatermarkOptions { Text = "DRAFT", FontSize = 36, Layout = WatermarkLayout.Tiled, Rotation = 0 });
        Assert.True(Core2Pdf.Lines(result, 0, annotations: false).Count(l => l.Text.Contains("DRAFT")) >= 6);
    }

    [Fact]
    public void RemovesIndependentlyOfTheHeaderAndFooter()
    {
        var pdf = TestPdf.Make(1);
        var before = ContentCount(pdf);
        var both = Watermarks.Apply(HeaderFooter.Apply(pdf, null, [0], new HeaderFooterOptions { FooterCenter = "footer text" }), null, [0],
            new WatermarkOptions { Text = "SECRET", Behind = false });
        var noHeader = HeaderFooter.Remove(both, null);
        var text = Core2Pdf.AllText(noHeader, 0, annotations: false);
        Assert.DoesNotContain("footer text", text);
        Assert.Contains("SECRET", text);
        var plain = Watermarks.Remove(noHeader, null);
        Assert.DoesNotContain("SECRET", Core2Pdf.AllText(plain));
        Assert.Equal(before, ContentCount(plain));
        Assert.False(Watermarks.Has(plain, null));
        Assert.Same(plain, Watermarks.Remove(plain, null));
        Assert.Equal([1], TestPdf.PageNumbers(plain));
    }

    [Fact]
    public void ApplyingAgainReplaces()
    {
        var once = Watermarks.Apply(TestPdf.Make(1), null, [0], new WatermarkOptions { Text = "FIRST" });
        var twice = Watermarks.Apply(once, null, [0], new WatermarkOptions { Text = "SECOND" });
        var text = Core2Pdf.AllText(twice);
        Assert.DoesNotContain("FIRST", text);
        Assert.Contains("SECOND", text);
    }

    [Fact]
    public void NeedsATextOrAnImage()
    {
        var pdf = TestPdf.Make(1);
        Assert.Throws<ArgumentException>(() => Watermarks.Apply(pdf, null, [0], new WatermarkOptions { Text = " " }));
        Assert.Throws<ArgumentException>(() => Watermarks.Apply(pdf, null, [0], new WatermarkOptions { Text = "A", Image = Red() }));
    }
}
