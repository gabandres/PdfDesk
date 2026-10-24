using System.Globalization;
using InkvoltPdf.Core;
using PdfSharp.Pdf;

namespace InkvoltPdf.Tests;

public class RubberStampTests
{
    private static PdfDictionary Annot(byte[] pdf)
    {
        var doc = Core2Pdf.Open(pdf);
        return PdfObj.Dict(PdfObj.Array(doc.Pages[0].Elements["/Annots"])!.Elements.Last())!;
    }

    [Fact]
    public void BuiltInSetAndDynamicApproval()
    {
        Assert.Equal(["APPROVED", "REJECTED", "DRAFT", "FINAL", "CONFIDENTIAL", "FOR COMMENT", "RECEIVED"], RubberStamps.BuiltIn.Select(d => d.Title));
        var by = RubberStamps.ApprovedBy("Ada Lovelace", new DateTime(2026, 3, 9, 14, 5, 0), CultureInfo.InvariantCulture);
        Assert.Equal("By Ada Lovelace on 03/09/2026 14:05", by.Detail);
        // Wider titles make wider stamps; a detail line makes it taller.
        var (dw, dh) = RubberStamps.NaturalSize(RubberStamps.BuiltIn[2]);
        var (cw, _) = RubberStamps.NaturalSize(RubberStamps.BuiltIn[4]);
        var (_, bh) = RubberStamps.NaturalSize(by);
        Assert.True(cw > dw * 1.5);
        Assert.True(bh > dh);
    }

    [Fact]
    public void AStampAnnotationWithItsOwnAppearance()
    {
        var design = RubberStamps.BuiltIn[0];
        var (w, h) = RubberStamps.NaturalSize(design);
        var pdf = RubberStamps.Put(TestPdf.Make(1), null, 0, new RectD(300, 500, w, h), design, "Tester");

        var a = Annot(pdf);
        Assert.Equal("/Stamp", PdfObj.NameOf(a, "/Subtype"));
        Assert.Equal("/Approved", PdfObj.NameOf(a, "/Name"));
        Assert.Equal("Approved", PdfObj.Str(a.Elements["/Subj"]));
        var ap = PdfObj.Dict(PdfObj.Dict(a.Elements["/AP"])!.Elements["/N"])!;
        var bbox = PdfObj.Array(ap.Elements["/BBox"])!;
        Assert.Equal(w, PdfObj.Num(bbox, 2), 1);
        Assert.Equal(h, PdfObj.Num(bbox, 3), 1);

        // What any viewer draws: the title, in green, inside the rectangle.
        Assert.Contains("APPROVED", Core2Pdf.AllText(pdf));
        Assert.DoesNotContain("APPROVED", Core2Pdf.AllText(pdf, annotations: false));
        var frame = TestImages.PixelAt(pdf, 0, 300 + 2, 500 + h / 2);
        Assert.True(frame.G > frame.R + 40 && frame.G > frame.B + 40, $"frame color {frame}");

        var placed = SignatureStamps.Read(pdf, null).Single();
        Assert.Equal(StampKind.Stamp, placed.Kind);
        var comment = Annotations.Read(pdf, null).Single();
        Assert.Equal((AnnotationKind.Stamp, "Approved", "Tester"), (comment.Kind, comment.Subject, comment.Author));
    }

    [Fact]
    public void ResizingRedrawsTheSameDesign()
    {
        var by = RubberStamps.ApprovedBy("Zoë Ñúñez", DateTime.Now);
        var (w, h) = RubberStamps.NaturalSize(by);
        var pdf = RubberStamps.Put(TestPdf.Make(2), null, 0, new RectD(100, 100, w, h), by, "Zoë");
        var id = SignatureStamps.Read(pdf, null).Single().Id;

        var moved = RubberStamps.Move(pdf, null, id, 1, new RectD(50, 600, w * 2, h * 2));
        var after = SignatureStamps.Read(moved, null).Single();
        Assert.Equal((id, 1), (after.Id, after.PageIndex));
        Assert.Equal(w * 2, after.Rect.Width, 1);
        using var doc = Core2Pdf.Open(moved);
        var a = PdfObj.Dict(PdfObj.Array(doc.Pages[1].Elements["/Annots"])!.Elements.Single())!;
        var design = RubberStamps.DesignOf(a)!;
        Assert.Equal(by, design);
        Assert.Equal("Zoë", PdfObj.Str(a.Elements["/T"]));
        var text = Core2Pdf.AllText(moved, 1);
        Assert.Contains("APPROVED", text);
        Assert.Contains("Zoë Ñúñez", text);
        Assert.DoesNotContain(Annotations.Read(moved, null), x => x.PageIndex == 0);
    }

    [Fact]
    public void OtherScriptsInTheDetailLine()
    {
        var by = RubberStamps.ApprovedBy("Андрей", DateTime.Now);
        var (w, h) = RubberStamps.NaturalSize(by);
        var pdf = RubberStamps.Put(TestPdf.Make(1), null, 0, new RectD(100, 100, w, h), by, null);
        var text = Core2Pdf.AllText(pdf);
        Assert.Contains("APPROVED", text);
        Assert.Contains("Андрей", text);
    }

    [Fact]
    public void CustomStampsFromAnImage()
    {
        var img = new StampImage(10, 10, [.. Enumerable.Range(0, 100).SelectMany(_ => new byte[] { 200, 30, 30 })], null);
        var pdf = RubberStamps.PutImage(TestPdf.Make(1), null, 0, new RectD(200, 200, 80, 80), img, "Paid in full", "Tester");
        var placed = SignatureStamps.Read(pdf, null).Single();
        Assert.Equal(StampKind.Stamp, placed.Kind);
        Assert.Equal("/Paidinfull", PdfObj.NameOf(Annot(pdf), "/Name"));
        Assert.Equal("Paid in full", Annotations.Read(pdf, null).Single().Subject);
        // Moved: the picture comes along.
        var moved = RubberStamps.Move(pdf, null, placed.Id, 0, new RectD(400, 600, 40, 40));
        Assert.True(TestImages.PixelAt(moved, 0, 420, 620).R > 150 && TestImages.PixelAt(moved, 0, 420, 620).G < 80);
        Assert.Empty(SignatureStamps.Read(Annotations.Delete(moved, null, placed.Id), null));
    }

    [Fact]
    public void FlattenedStampsBecomePageContent()
    {
        var design = RubberStamps.BuiltIn[2];
        var (w, h) = RubberStamps.NaturalSize(design);
        var pdf = RubberStamps.Put(TestPdf.Make(1), null, 0, new RectD(100, 100, w, h), design, null);
        var flat = Flattener.Flatten(pdf, null);
        Assert.Contains("DRAFT", Core2Pdf.AllText(flat, annotations: false));
    }
}
