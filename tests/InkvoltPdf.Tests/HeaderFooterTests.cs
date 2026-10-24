using System.Globalization;
using InkvoltPdf.Core;
using PdfSharp.Pdf;

namespace InkvoltPdf.Tests;

public class HeaderFooterTests
{
    private static readonly DateTime When = new(2026, 3, 9, 14, 5, 0);

    private static string Expand(string t, int page = 3, int pages = 12, string? bates = "ABC000042") =>
        HeaderFooter.Expand(t, page, pages, bates, "report.pdf", When, CultureInfo.InvariantCulture);

    [Fact]
    public void TokensExpand()
    {
        Assert.Equal("Page 3 of 12", Expand("Page <<page>> of <<pages>>"));
        Assert.Equal("03/09/2026 14:05", Expand("<<date>> <<time>>"));
        Assert.Equal("2026-03-09", Expand("<<date:yyyy-MM-dd>>"));
        Assert.Equal("report.pdf · ABC000042", Expand("<<FILE>> · << bates >>"));
        // Unknown tokens and stray brackets stay as typed; no Bates format means an empty number.
        Assert.Equal("<<chapter>> <<page", Expand("<<chapter>> <<page"));
        Assert.Equal("[]", Expand("[<<bates>>]", bates: null));
        // A bad date format falls back to the short date instead of failing.
        Assert.Equal("03/09/2026", Expand("<<date:%>>"));
    }

    [Fact]
    public void BatesNumbersArePadded()
    {
        var b = new BatesFormat("SMITH-", 41, 6, "-X");
        Assert.Equal("SMITH-000041-X", b.Format(0));
        Assert.Equal("SMITH-000050-X", b.Format(9));
        Assert.Equal("1234567", new BatesFormat("", 1234567, 3).Format(0)); // more digits than the padding: kept whole
        Assert.Equal("7", new BatesFormat(Start: 7, Digits: 0).Format(0)); // at least one digit
    }

    [Fact]
    public void SlotsArePlacedInsideTheMargins()
    {
        var o = new HeaderFooterOptions { MarginTop = 36, MarginBottom = 30, MarginLeft = 72, MarginRight = 50 };
        Assert.Equal((72.0, 792 - 36 - 9.0), HeaderFooter.Position(HeaderFooterSlot.HeaderLeft, 100, 612, 792, 9, -2, o));
        Assert.Equal((256.0, 32.0), HeaderFooter.Position(HeaderFooterSlot.FooterCenter, 100, 612, 792, 9, -2, o));
        Assert.Equal((612 - 50 - 100.0, 32.0), HeaderFooter.Position(HeaderFooterSlot.FooterRight, 100, 612, 792, 9, -2, o));
    }

    [Fact]
    public void WrittenAsPageTextWhereItBelongs()
    {
        var pdf = TestPdf.Make(3);
        var options = new HeaderFooterOptions
        {
            HeaderCenter = "Quarterly report",
            FooterRight = "Page <<page>> of <<pages>>",
            FooterLeft = "<<bates>>",
            Bates = new BatesFormat("ACME", 100, 5),
        };
        var result = HeaderFooter.Apply(pdf, null, [0, 2], options);

        // Page content, not annotations: the text is there with annotations left out.
        var first = Core2Pdf.Lines(result, 0, annotations: false);
        var footer = first.Single(l => l.Text == "Page 1 of 3");
        Assert.InRange(footer.Box.Bottom, 792 - 40, 792 - 30); // near the bottom (36 pt margin), page space y down
        Assert.InRange(footer.Box.Right, 612 - 75, 612 - 70);
        var header = first.Single(l => l.Text == "Quarterly report");
        Assert.InRange(header.Box.Y, 34, 40);
        Assert.InRange(header.Box.CenterX, 300, 312);
        Assert.Contains(first, l => l.Text == "ACME00100");

        // Page 2 wasn't in the range; page 3 gets the next Bates number.
        Assert.DoesNotContain("Quarterly", Core2Pdf.AllText(result, 1));
        var third = Core2Pdf.AllText(result, 2, annotations: false);
        Assert.Contains("Page 3 of 3", third);
        Assert.Contains("ACME00101", third);
        // The page's own text is still there.
        Assert.Equal([1, 2, 3], TestPdf.PageNumbers(result));
        Assert.True(HeaderFooter.Has(result, null));
        Assert.False(HeaderFooter.Has(pdf, null));
    }

    [Fact]
    public void FollowsTheTurnedPage()
    {
        var turned = PageEditor.Rotate(TestPdf.Make(1), null, [0], 90);
        var result = HeaderFooter.Apply(turned, null, [0], new HeaderFooterOptions { FooterCenter = "bottom line" });
        // Shown landscape (792 x 612): the footer sits at the bottom of the page as shown, read left to right.
        var line = Core2Pdf.Lines(result, 0).Single(l => l.Text == "bottom line");
        Assert.InRange(line.Box.Bottom, 612 - 40, 612 - 30);
        Assert.InRange(line.Box.CenterX, 380, 412);
        Assert.True(line.Box.Width > line.Box.Height);
    }

    [Fact]
    public void ApplyingAgainReplaces()
    {
        var once = HeaderFooter.Apply(TestPdf.Make(1), null, [0], new HeaderFooterOptions { HeaderLeft = "first version" });
        var twice = HeaderFooter.Apply(once, null, [0], new HeaderFooterOptions { HeaderLeft = "second version" });
        var text = Core2Pdf.AllText(twice);
        Assert.DoesNotContain("first version", text);
        Assert.Contains("second version", text);
    }

    [Fact]
    public void RemoveTakesOutOnlyWhatWasAdded()
    {
        var pdf = TestPdf.Make(2);
        var added = HeaderFooter.Apply(pdf, null, [0, 1], new HeaderFooterOptions { FooterCenter = "<<page>>/<<pages>>", HeaderRight = "Draft" });
        var removed = HeaderFooter.Remove(added, null);
        Assert.DoesNotContain("Draft", Core2Pdf.AllText(removed));
        Assert.Equal([1, 2], TestPdf.PageNumbers(removed));
        Assert.False(HeaderFooter.Has(removed, null));
        using (var doc = Core2Pdf.Open(removed))
            foreach (var page in doc.Pages)
            {
                Assert.DoesNotContain(PdfObj.Array(page.Elements["/Contents"])!.Elements, e => PdfObj.Dict(e)?.Elements.ContainsKey(PageMarks.Key) == true);
                var xobjects = PdfObj.Dict(PdfObj.Dict(page.Elements["/Resources"])?.Elements["/XObject"]);
                Assert.True(xobjects is null || xobjects.Elements.Keys.All(k => !k.StartsWith("/Iv", StringComparison.Ordinal)));
            }
        // Nothing to remove: the same bytes back (no undo step).
        Assert.Same(pdf, HeaderFooter.Remove(pdf, null));
    }

    [Fact]
    public void OtherScriptsGetAnEmbeddedFont()
    {
        var result = HeaderFooter.Apply(TestPdf.Make(1), null, [0], new HeaderFooterOptions { HeaderCenter = "Отчёт Ω" });
        Assert.Contains("Отчёт Ω", Core2Pdf.AllText(result));
    }

    [Fact]
    public void OtherScriptsEmbedOneFontForAllPages()
    {
        static int Type0Fonts(byte[] pdf)
        {
            using var doc = Core2Pdf.Open(pdf);
            return doc.Internals.GetAllObjects().OfType<PdfDictionary>().Count(d => PdfObj.NameOf(d, "/Subtype") == "/Type0");
        }
        var pdf = TestPdf.Make(50);
        var result = HeaderFooter.Apply(pdf, null, [.. Enumerable.Range(0, 50)],
            new HeaderFooterOptions { HeaderCenter = "Отчёт <<page>>", FooterRight = "Ωμέγα <<page>>/<<pages>>" });
        Assert.Equal(1, Type0Fonts(result) - Type0Fonts(pdf));
        // Subset to the glyphs of every page: the last page's digits are there too.
        Assert.Contains("Отчёт 50", Core2Pdf.AllText(result, 49));
        Assert.Contains("Ωμέγα 37/50", Core2Pdf.AllText(result, 36));
    }

    /// <summary>A Letter page whose content moves the origin and leaves it so (an unclosed q), or restores more than it saved.</summary>
    private static byte[] Unbalanced(string content, string pageExtra = "") => Core2Pdf.Build(
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R {pageExtra} >>",
        Core2Pdf.Stream(content),
    ]);

    [Theory]
    [InlineData("q 1 0 0 1 250 -300 cm 0 0 1 rg 0 0 10 10 re f")]
    [InlineData("q q 0.5 0 0 0.5 0 0 cm (Q) Tj % Q\n0 0 1 rg 0 0 10 10 re f")]
    [InlineData("Q 1 0 0 1 250 -300 cm 0 0 1 rg 0 0 10 10 re f")]
    [InlineData("BI /W 2 /H 1 /CS /G /BPC 8 ID QQ EI q 2 0 0 2 0 0 cm 0 0 1 rg 0 0 10 10 re f")]
    public void UnbalancedPageContentDoesntMoveTheFooter(string content)
    {
        var result = HeaderFooter.Apply(Unbalanced(content), null, [0], new HeaderFooterOptions { FooterRight = "Page <<page>>", FontSize = 10 });
        var footer = Core2Pdf.Lines(result, 0, annotations: false).Single(l => l.Text == "Page 1");
        Assert.InRange(footer.Box.Bottom, 792 - 40, 792 - 30);
        Assert.InRange(footer.Box.Right, 612 - 75, 612 - 70);
        Assert.InRange(footer.Box.Height, 6, 14);
        // Removed again, the page is as it was.
        using var doc = Core2Pdf.Open(HeaderFooter.Remove(result, null));
        var left = PdfObj.Array(doc.Pages[0].Elements["/Contents"])!;
        Assert.Equal(content, System.Text.Encoding.Latin1.GetString(PdfObj.StreamBytes(PdfObj.Dict(left.Elements.Single())!)));
    }

    [Fact]
    public void SaveDepthSkipsStringsCommentsAndImages()
    {
        static (int, int) Depth(string s) => PageMarks.SaveDepth([System.Text.Encoding.Latin1.GetBytes(s)]);
        Assert.Equal((0, 0), Depth("q 1 0 0 1 5 5 cm Q"));
        Assert.Equal((1, 0), Depth("q (Q\\) Q) Tj <51> Tj % Q Q\n"));
        Assert.Equal((-1, -2), Depth("Q Q q"));
        Assert.Equal((2, 0), Depth("q BI /W 1 /H 1 ID QQ EI q /Q Do"));
        Assert.Equal((0, -1), PageMarks.SaveDepth([[(byte)'Q'], [(byte)'q']])); // streams in a row
    }

    [Fact]
    public void UserUnitPagesGetPointSizes()
    {
        // One unit is 2 points: the 612 x 792 unit page is 1224 x 1584 points on paper.
        var result = HeaderFooter.Apply(Unbalanced("0 0 1 rg 0 0 10 10 re f", "/UserUnit 2"), null, [0],
            new HeaderFooterOptions { FooterRight = "Page <<page>>", FontSize = 10, MarginBottom = 36, MarginRight = 72 });
        using var doc = Core2Pdf.Open(result);
        var page = doc.Pages[0];
        var xobjects = PdfObj.Dict(PdfObj.Dict(page.Elements["/Resources"])!.Elements["/XObject"])!;
        var form = PdfObj.Dict(xobjects.Elements.Values.Single())!;
        var bbox = PdfObj.Array(form.Elements["/BBox"])!;
        var matrix = PdfObj.Array(form.Elements["/Matrix"])!;
        // Drawn in points and scaled by 1/2 into the page's units: a 10 pt footer is 10 pt on paper.
        Assert.Equal(1224, PdfObj.Num(bbox, 2), 1);
        Assert.Equal(1584, PdfObj.Num(bbox, 3), 1);
        Assert.Equal(0.5, PdfObj.Num(matrix, 0), 3);
        Assert.Equal(0.5, PdfObj.Num(matrix, 3), 3);
    }

    [Fact]
    public void KeepsThePasswordProtection()
    {
        var pdf = TestPdf.Encrypted(1, "secret");
        var result = HeaderFooter.Apply(pdf, "secret-owner", [0], new HeaderFooterOptions { FooterCenter = "<<page>>" });
        using var engine = PdfEngine.Open(result, MuPDFCore.InputFileTypes.PDF, "secret");
        Assert.True(engine.IsEncrypted);
        Assert.Throws<ArgumentException>(() => HeaderFooter.Apply(TestPdf.Make(1), null, [0], new HeaderFooterOptions()));
    }

    [Fact]
    public void SignedFilesGetItAsAnUpdateThatBreaksTheSignature()
    {
        var signed = SignedEditTests.Signed();
        var result = HeaderFooter.Apply(signed, null, [0], new HeaderFooterOptions { FooterCenter = "<<page>>" });
        // Appended after the signed bytes, but a page content change, which no signature allows: the UI warns first.
        Assert.True(result.AsSpan(0, signed.Length).SequenceEqual(signed));
        Assert.NotEmpty(Signatures.BrokenByEdits(Signatures.Verify(signed), result, null));
    }
}
