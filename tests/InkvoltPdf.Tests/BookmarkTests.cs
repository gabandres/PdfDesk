using MuPDFCore;
using InkvoltPdf.Core;
using PdfSharp.Pdf;

namespace InkvoltPdf.Tests;

public class BookmarkTests
{
    private static IReadOnlyList<OutlineNode> MuPdfOutline(byte[] pdf)
    {
        using var engine = PdfEngine.Open(pdf, InputFileTypes.PDF, null);
        return engine.GetOutline();
    }

    private static Bookmark Tree() =>
        new("Part 1", 0)
        {
            Open = true,
            Bold = true,
            Color = new AnnotColor(1, 0, 0),
            Children =
            [
                new Bookmark("Chapter A", 1, 200) { Italic = true },
                new Bookmark("Chapter B", 2) { Children = [new Bookmark("Section B.1", 2, 400)] },
            ],
        };

    [Fact]
    public void WritesANestedTreeAndReadsItBack()
    {
        var pdf = Bookmarks.SetOutline(TestPdf.Make(3), null, [Tree(), new Bookmark("Heading only")]);
        var read = Bookmarks.Read(pdf, null);
        Assert.Equal(["Part 1", "Heading only"], read.Select(b => b.Title));
        var part = read[0];
        Assert.True(part.Open && part.Bold && !part.Italic);
        Assert.Equal(new AnnotColor(1, 0, 0), part.Color);
        Assert.Equal(0, part.PageIndex);
        Assert.Equal(["Chapter A", "Chapter B"], part.Children.Select(b => b.Title));
        Assert.True(part.Children[0].Italic);
        Assert.Equal(1, part.Children[0].PageIndex);
        Assert.Equal(200, part.Children[0].Top!.Value, 1);
        Assert.False(part.Children[1].Open);
        Assert.Equal(400, part.Children[1].Children[0].Top!.Value, 1);
        Assert.Equal(-1, read[1].PageIndex);

        // /Count: the open Part 1 shows its 2 children (B is closed: its child counts negative); the root counts 4 visible.
        using var doc = Core2Pdf.Open(pdf);
        var root = PdfObj.Dict(doc.Internals.Catalog.Elements["/Outlines"])!;
        Assert.Equal(4, PdfObj.Int(root.Elements["/Count"]));
        var first = PdfObj.Dict(root.Elements["/First"])!;
        Assert.Equal(2, PdfObj.Int(first.Elements["/Count"]));
        Assert.Equal(-1, PdfObj.Int(PdfObj.Dict(first.Elements["/Last"])!.Elements["/Count"]));

        // MuPDF sees the same tree.
        var mu = MuPdfOutline(pdf);
        Assert.Equal(["Part 1", "Heading only"], mu.Select(n => n.Title));
        Assert.Equal(["Chapter A", "Chapter B"], mu[0].Children.Select(n => n.Title));
        Assert.Equal(1, mu[0].Children[0].PageIndex);
        Assert.Equal("Section B.1", mu[0].Children[1].Children[0].Title);
        Assert.Equal(2, mu[0].Children[1].Children[0].PageIndex);
    }

    [Fact]
    public void RenamesDeletesReordersAndNests()
    {
        var pdf = Bookmarks.SetOutline(TestPdf.Make(3), null, [Tree()]);
        var part = Bookmarks.Read(pdf, null)[0];
        var a = part.Children[0];
        var b = part.Children[1];
        // B moves to the top level before Part 1 and is renamed; A moves under B; B.1 is deleted.
        var edited = Bookmarks.SetOutline(pdf, null,
        [
            b with { Title = "Chapter Bee", Children = [a], Open = true },
            part with { Children = [] },
        ]);
        var read = Bookmarks.Read(edited, null);
        Assert.Equal(["Chapter Bee", "Part 1"], read.Select(x => x.Title));
        Assert.Equal(["Chapter A"], read[0].Children.Select(x => x.Title));
        Assert.Equal(200, read[0].Children[0].Top!.Value, 1);
        Assert.Empty(read[1].Children);
        Assert.Equal(["Chapter Bee", "Part 1"], MuPdfOutline(edited).Select(n => n.Title));

        // Retargeting rewrites the destination.
        var moved = Bookmarks.SetOutline(edited, null, [read[0] with { PageIndex = 0, Top = 100 }, read[1]]);
        Assert.Equal(0, Bookmarks.Read(moved, null)[0].PageIndex);
        Assert.Equal(100, Bookmarks.Read(moved, null)[0].Top!.Value, 1);

        // An empty outline removes it.
        var none = Bookmarks.SetOutline(moved, null, []);
        Assert.Empty(Bookmarks.Read(none, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bookmarks.SetOutline(none, null, [new Bookmark("x", 9)]));
    }

    [Fact]
    public void KeepsOtherActionsWhenUnchanged()
    {
        var pdf = Core2Pdf.Build(
        [
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Outlines /First 5 0 R /Last 6 0 R /Count 2 >>",
            "<< /Title (Web) /Parent 4 0 R /Next 6 0 R /A << /S /URI /URI (https://example.com/) >> >>",
            "<< /Title (Go) /Parent 4 0 R /Prev 5 0 R /A << /S /GoTo /D [3 0 R /FitH 700] >> >>",
        ]);
        var read = Bookmarks.Read(pdf, null);
        Assert.Equal("URI", read[0].ActionKind);
        Assert.Equal("https://example.com/", read[0].Uri);
        Assert.Equal(0, read[1].PageIndex);

        var edited = Bookmarks.SetOutline(pdf, null, [read[1] with { Title = "Go there" }, read[0] with { Bold = true }]);
        using var doc = Core2Pdf.Open(edited);
        var first = PdfObj.Dict(PdfObj.Dict(doc.Internals.Catalog.Elements["/Outlines"])!.Elements["/First"])!;
        Assert.Equal("Go there", PdfObj.Str(first.Elements["/Title"]));
        Assert.Equal("FitH", PdfObj.Name(PdfObj.Array(PdfObj.Dict(first.Elements["/A"])!.Elements["/D"])!.Elements[1]));
        var second = PdfObj.Dict(first.Elements["/Next"])!;
        Assert.Equal("https://example.com/", PdfObj.Str(PdfObj.Dict(second.Elements["/A"])!.Elements["/URI"]));
        Assert.Equal(2, PdfObj.Int(second.Elements["/F"]));
    }

    [Fact]
    public void SignedFilesGetAnIncrementalUpdate()
    {
        var signed = SignedEditTests.Signed();
        var (edited, report) = SignedEdits.Run(() => Bookmarks.SetOutline(signed, null, [new Bookmark("Contract", 0) { Children = [new Bookmark("Page two", 1)] }]));
        Assert.True(report.Appended);
        Assert.True(edited.AsSpan(0, signed.Length).SequenceEqual(signed));
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, Signatures.Verify(edited).Single(s => s.FieldName == "Approval").Status);
        Assert.Equal(["Contract"], MuPdfOutline(edited).Select(n => n.Title));

        var renamed = Bookmarks.SetOutline(edited, null, [Bookmarks.Read(edited, null)[0] with { Title = "Agreement" }]);
        Assert.Equal(SignatureStatus.ModifiedAfterSigning, Signatures.Verify(renamed).Single(s => s.FieldName == "Approval").Status);
        Assert.Equal("Agreement", Bookmarks.Read(renamed, null)[0].Title);
        Assert.Equal("Page two", Bookmarks.Read(renamed, null)[0].Children[0].Title);
    }
}
