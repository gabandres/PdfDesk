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
    public void MovingKeepsTheAnnotationItself()
    {
        var design = RubberStamps.BuiltIn[0];
        var (w, h) = RubberStamps.NaturalSize(design);
        var image = new StampImage(4, 4, [.. Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 30, 30, 200 })], null);
        var pdf = RubberStamps.Put(TestPdf.Make(2), null, 0, new RectD(100, 100, w, h), design, "Tester");
        pdf = SignatureStamps.PutImage(pdf, null, 0, new RectD(300, 300, 40, 40), image, "logo.png");
        var ids = SignatureStamps.Read(pdf, null).Select(s => s.Id).ToList();

        // Another app's reply to each, a see-through look, a tag and a layer: what a move used to lose.
        using (var doc = PdfObj.Open(pdf, null, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            var annots = PdfObj.Array(doc.Pages[0].Elements["/Annots"])!;
            foreach (var stamp in annots.Elements.Select(PdfObj.Dict).ToList())
            {
                stamp!.Elements["/CA"] = new PdfReal(0.5);
                stamp.Elements["/StructParent"] = new PdfInteger(7);
                var reply = new PdfDictionary(doc);
                reply.Elements["/Type"] = new PdfName("/Annot");
                reply.Elements["/Subtype"] = new PdfName("/Text");
                reply.Elements["/Rect"] = PdfObj.Reals(doc, 0, 0, 20, 20);
                reply.Elements["/Contents"] = new PdfString("Agreed");
                reply.Elements["/IRT"] = stamp.Reference!;
                doc.Internals.AddObject(reply);
                annots.Elements.Add(reply.Reference!);
            }
            pdf = PdfObj.Save(doc);
        }

        var moved = RubberStamps.Move(pdf, null, ids[0], 0, new RectD(50, 600, w * 2, h * 2));
        moved = RubberStamps.Move(moved, null, ids[1], 0, new RectD(400, 500, 80, 80));
        using var after = Core2Pdf.Open(moved);
        var list = PdfObj.Array(after.Pages[0].Elements["/Annots"])!.Elements.Select(PdfObj.Dict).ToList();
        Assert.Equal(4, list.Count);
        for (var i = 0; i < 2; i++)
        {
            // Same place among the page's annotations, same id, its extras kept, and the replies still point at it.
            var stamp = list[i]!;
            Assert.Equal(ids[i], PdfObj.Str(stamp.Elements["/NM"]));
            Assert.Equal(0.5, PdfObj.Num(stamp.Elements["/CA"]));
            Assert.Equal(7, PdfObj.Int(stamp.Elements["/StructParent"]));
            Assert.Contains(list, r => r is not null && ReferenceEquals(PdfObj.Dict(r.Elements["/IRT"]), stamp));
        }
        var rects = SignatureStamps.Read(moved, null);
        Assert.Equal(w * 2, rects.Single(s => s.Id == ids[0]).Rect.Width, 1);
        Assert.Equal(400, rects.Single(s => s.Id == ids[1]).Rect.X, 1);
        Assert.True(TestImages.PixelAt(moved, 0, 440, 540).B > 150);
        Assert.Equal(2, Annotations.Read(moved, null).Count(a => a.ReplyTo is not null));
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
