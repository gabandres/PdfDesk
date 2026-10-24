using MuPDFCore;
using MuPDFCore.StructuredText;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class TextStampTests
{
    [Fact]
    public void AddsReadsEditsAndRemovesText()
    {
        var doc = TestPdf.Make(2);
        var withText = TextStamps.Put(doc, null, 1, new RectD(100, 300, 200, 20), StampKind.Text, "Juan Pérez", 12);

        var stamps = TextStamps.Read(withText, null);
        var s = Assert.Single(stamps);
        Assert.Equal(1, s.PageIndex);
        Assert.Equal("Juan Pérez", s.Text);
        Assert.Equal(12, s.FontSize);
        Assert.Equal(100, s.Rect.X, 0.5);
        Assert.Equal(300, s.Rect.Y, 0.5);

        // MuPDF draws it, accents included.
        Assert.Contains("Juan Pérez", AnnotationText(withText, 1));

        var edited = TextStamps.Put(withText, null, 1, s.Rect, StampKind.Text, "María", 14, s.Id);
        var e = Assert.Single(TextStamps.Read(edited, null));
        Assert.Equal("María", e.Text);
        Assert.Equal(s.Id, e.Id);

        Assert.Empty(TextStamps.Read(TextStamps.Remove(edited, null, e.Id), null));
    }

    [Fact]
    public void AddsCheckMarks()
    {
        var data = TextStamps.Put(TestPdf.Make(1), null, 0, new RectD(300, 400, 14, 14), StampKind.Check, "", 12);
        var s = Assert.Single(TextStamps.Read(data, null));
        Assert.Equal(StampKind.Check, s.Kind);
        using var engine = PdfEngine.Open(data, InputFileTypes.PDF, null);
        var img = engine.Render(0, 3, new RectD(300, 400, 14, 14));
        var dark = 0;
        for (var i = 0; i + 2 < img.Pixels.Length; i += 3)
            if (img.Pixels[i] + img.Pixels[i + 1] + img.Pixels[i + 2] < 300) dark++;
        Assert.True(dark / (double)(img.Width * img.Height) > 0.1, "check mark should be drawn");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TextIsUprightAndInPlaceOnRotatedPages(int rotation)
    {
        var page = rotation == 0 ? TestPdf.Make(1) : PageEditor.Rotate(TestPdf.Make(1), null, [0], rotation);
        var rect = new RectD(150, 250, 260, 20);
        var data = TextStamps.Put(page, null, 0, rect, StampKind.Text, "UPRIGHT STAMP TEXT", 14);

        Assert.Equal(rect.X, TextStamps.Read(data, null)[0].Rect.X, 0.5);
        var (text, box) = AnnotationLine(data, "UPRIGHT");
        Assert.Contains("UPRIGHT STAMP TEXT", text);
        Assert.True(box.Width > box.Height * 3, $"rotation {rotation}: text runs sideways ({box})");
        Assert.InRange(box.X, rect.X - 2, rect.X + 20);
        Assert.InRange(box.Y, rect.Y - 4, rect.Bottom);
    }

    private static string AnnotationText(byte[] data, int page) => AnnotationLine(data, null, page).Text;

    /// <summary>Text drawn by annotations (MuPDF structured text with annotations), and the box of the matching line.</summary>
    private static (string Text, RectD Box) AnnotationLine(byte[] data, string? contains, int page = 0)
    {
        using var ctx = new MuPDFContext();
        using var doc = new MuPDFDocument(ctx, data, InputFileTypes.PDF);
        using var st = doc.GetStructuredTextPage(page, includeAnnotations: true);
        var all = new System.Text.StringBuilder();
        var box = default(RectD);
        foreach (var block in st)
        {
            if (block is not MuPDFTextStructuredTextBlock tb) continue;
            foreach (var line in tb)
            {
                var t = string.Concat(line.Select(c => c.Character));
                all.AppendLine(t);
                if (contains is not null && t.Contains(contains))
                {
                    var b = line.BoundingBox;
                    box = RectD.FromEdges(b.X0, b.Y0, b.X1, b.Y1);
                }
            }
        }
        doc.Pages.Dispose();
        return (all.ToString(), box);
    }
}
