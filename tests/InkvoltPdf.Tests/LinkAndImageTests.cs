using InkvoltPdf.Core;
using MuPDFCore;

namespace InkvoltPdf.Tests;

public class LinkAndImageTests
{
    private static StampImage Blue(int w = 30, int h = 20) =>
        new(w, h, [.. Enumerable.Range(0, w * h).SelectMany(_ => new byte[] { 20, 40, 200 })], null);

    [Theory]
    [InlineData("example.com/docs", "https://example.com/docs")]
    [InlineData("www.example.com", "https://www.example.com/")]
    [InlineData("https://example.com/a b", "https://example.com/a%20b")]
    [InlineData("ada@example.com", "mailto:ada@example.com")]
    [InlineData("mailto:ada@example.com", "mailto:ada@example.com")]
    [InlineData("ftp://files.example.com/x", "ftp://files.example.com/x")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:text/html,hi", null)]
    [InlineData("not a link", null)]
    [InlineData("", null)]
    public void AddressesAreNormalized(string typed, string? expected) => Assert.Equal(expected, Links.Normalize(typed));

    [Fact]
    public void OnlyWebAndEmailAreSafe()
    {
        Assert.True(Links.IsSafe("https://example.com/"));
        Assert.True(Links.IsSafe("mailto:a@b.c"));
        Assert.False(Links.IsSafe("ftp://example.com/"));
        Assert.False(Links.IsSafe("file:///C:/Windows/notepad.exe"));
    }

    [Fact]
    public void LinksRoundTripThroughMuPdf()
    {
        var pdf = TestPdf.Make(3);
        var web = Links.Put(pdf, null, 0, new RectD(72, 80, 150, 30), "example.com", -1);
        var both = Links.Put(web, null, 0, new RectD(72, 200, 100, 20), null, 2);

        var read = Links.Read(both, null);
        Assert.Equal(2, read.Count);
        var w = read.Single(l => l.Uri is not null);
        Assert.Equal("https://example.com/", w.Uri);
        Assert.Equal(72, w.Rect.X, 1);
        Assert.Equal(80, w.Rect.Y, 1);
        Assert.Equal(2, read.Single(l => l.Uri is null).TargetPage);

        // What the page view follows: MuPDF's own reading of the same links.
        using var engine = PdfEngine.Open(both, InputFileTypes.PDF, null);
        var links = engine.GetLinks(0);
        Assert.Contains(links, l => l.Uri == "https://example.com/" && Math.Abs(l.Area.X - 72) < 1);
        Assert.Contains(links, l => l.Uri is null && l.PageIndex == 2);
    }

    [Fact]
    public void EditingMovesAndRetargets()
    {
        var pdf = Links.Put(TestPdf.Make(2), null, 0, new RectD(72, 80, 150, 30), "https://example.com/", -1);
        var id = Links.Read(pdf, null).Single().Id;
        var moved = Links.Put(pdf, null, 1, new RectD(100, 300, 50, 50), null, 0, id);
        var link = Links.Read(moved, null).Single();
        Assert.Equal(id, link.Id);
        Assert.Equal(1, link.PageIndex);
        Assert.Null(link.Uri);
        Assert.Equal(0, link.TargetPage);
        Assert.Equal(300, link.Rect.Y, 1);

        var gone = Links.Delete(moved, null, id);
        Assert.Empty(Links.Read(gone, null));
        Assert.Throws<InvalidOperationException>(() => Links.Delete(gone, null, id));
        Assert.Throws<ArgumentException>(() => Links.Put(pdf, null, 0, new RectD(0, 0, 10, 10), "javascript:alert(1)", -1));
    }

    [Fact]
    public void MovingKeepsActionsItCantEdit()
    {
        var pdf = Core2Pdf.Build(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [100 700 200 720] /A << /S /Named /N /NextPage >> >>",
        ]);
        var link = Links.Read(pdf, null).Single();
        Assert.Equal((null, -1), (link.Uri, link.TargetPage));
        Assert.Equal("Another action", link.Describe());
        var moved = Links.Move(pdf, null, link.Id, 0, new RectD(10, 10, 50, 20));
        using var doc = Core2Pdf.Open(moved);
        var annot = PdfObj.Dict(PdfObj.Array(doc.Pages[0].Elements["/Annots"])!.Elements[0])!;
        Assert.Equal("NextPage", PdfObj.Name(PdfObj.Dict(annot.Elements["/A"])!.Elements["/N"]));
        Assert.Equal(10, Links.Read(moved, null).Single().Rect.X, 1);
    }

    [Fact]
    public void LinksOnTurnedPagesUsePageSpace()
    {
        var turned = PageEditor.Rotate(TestPdf.Make(1), null, [0], 90);
        var pdf = Links.Put(turned, null, 0, new RectD(50, 60, 200, 40), "https://example.com/", -1);
        var link = Links.Read(pdf, null).Single();
        Assert.Equal(50, link.Rect.X, 1);
        Assert.Equal(60, link.Rect.Y, 1);
        Assert.Equal(200, link.Rect.Width, 1);
        Assert.Equal(40, link.Rect.Height, 1);
    }

    [Fact]
    public void ImagesArePlacedMovedAndListed()
    {
        var pdf = SignatureStamps.PutImage(TestPdf.Make(1), null, 0, new RectD(300, 400, 90, 60), Blue(), "logo.png", "Tester");
        var placed = SignatureStamps.Read(pdf, null).Single();
        Assert.Equal(StampKind.Image, placed.Kind);
        Assert.Equal("logo.png", placed.Text);
        Assert.True(TestImages.PixelAt(pdf, 0, 345, 430).B > 150);

        var moved = SignatureStamps.Put(pdf, null, 0, new RectD(100, 100, 45, 30), null, placed.Id);
        var after = SignatureStamps.Read(moved, null).Single();
        Assert.Equal((placed.Id, StampKind.Image), (after.Id, after.Kind));
        Assert.Equal(100, after.Rect.X, 1);
        Assert.True(TestImages.PixelAt(moved, 0, 120, 115).B > 150);
        Assert.True(TestImages.PixelAt(moved, 0, 345, 430).B > 240 && TestImages.PixelAt(moved, 0, 345, 430).R > 240);

        // A stamp in the Comments pane, with its author and subject.
        var comment = Annotations.Read(moved, null).Single();
        Assert.Equal(AnnotationKind.Stamp, comment.Kind);
        Assert.Equal("Image", comment.Subject);
        Assert.Equal("Tester", comment.Author);
        Assert.Empty(SignatureStamps.Read(TextStamps.Remove(moved, null, placed.Id), null));
    }
}
