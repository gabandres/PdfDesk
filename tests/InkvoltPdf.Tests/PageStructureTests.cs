using MuPDFCore;
using InkvoltPdf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Tests;

/// <summary>Page edits keep the file's structure honest: no hidden copies of other pages, page labels, page boxes.</summary>
public class PageStructureTests
{
    /// <summary>
    /// Page 1 has a form button jumping to page 2 (/A and /AA), a page /AA jumping there, an article thread bead and a
    /// reply to a comment on page 2. Page 2 holds SECRET text and a SECRET comment; the document opens on page 2.
    /// </summary>
    private static byte[] CrossLinked() => Core2Pdf.Build([
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> /Threads [8 0 R] /OpenAction << /S /GoTo /D [4 0 R /Fit] /Next << /S /GoTo /D [3 0 R /Fit] >> >> >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Annots [5 0 R 13 0 R] /AA << /O << /S /GoTo /D [4 0 R /Fit] >> >> /B [9 0 R] /Resources << /Font << /F1 10 0 R >> >> >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R /Annots [12 0 R] /B [11 0 R] /Resources << /Font << /F1 10 0 R >> >> >>",
        "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (Next) /Rect [72 72 172 100] /P 3 0 R /A << /S /GoTo /D [4 0 R /XYZ 0 792 0] >> /AA << /E << /S /GoTo /D [4 0 R /Fit] >> /D << /S /JavaScript /JS (app.alert\\(1\\)) >> >> >>",
        Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (Visible page) Tj ET"),
        Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (SECRET page two) Tj ET"),
        "<< /Type /Thread /F 9 0 R >>",
        "<< /Type /Bead /T 8 0 R /N 11 0 R /V 11 0 R /P 3 0 R /R [0 0 100 100] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /Bead /N 9 0 R /V 9 0 R /P 4 0 R /R [0 0 100 100] >>",
        "<< /Type /Annot /Subtype /Text /Rect [72 600 92 620] /P 4 0 R /Contents (SECRET note) >>",
        "<< /Type /Annot /Subtype /Text /Rect [72 600 92 620] /P 3 0 R /IRT 12 0 R /Contents (Reply) >>",
    ]);

    private static PdfDictionary D(PdfItem? item) => (PdfDictionary)(item is PdfReference r ? r.Value : item)!;

    [Fact]
    public void ExtractedPagesDontCarryTheOtherPagesAlong()
    {
        var extracted = PageEditor.Extract(CrossLinked(), null, [0]);
        Assert.DoesNotContain("SECRET", PdfText.Everything(extracted));

        using var doc = PdfReader.Open(new MemoryStream(extracted), PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.PageCount);
        var page = doc.Pages[0];
        var annots = page.Elements.GetArray("/Annots")!;
        var button = D(annots.Elements[0]);
        Assert.Equal("Next", button.Elements.GetString("/T"));
        Assert.False(button.Elements.ContainsKey("/A"), "the jump to the removed page is gone");
        var triggers = D(button.Elements["/AA"]);
        Assert.False(triggers.Elements.ContainsKey("/E"));
        Assert.True(triggers.Elements.ContainsKey("/D"), "other actions stay");
        Assert.False(D(page.Elements["/AA"]).Elements.ContainsKey("/O"));
        Assert.False(D(annots.Elements[1]).Elements.ContainsKey("/IRT"));
        Assert.False(doc.Internals.Catalog.Elements.ContainsKey("/OpenAction"));
        // The thread keeps its bead on the remaining page, linked to itself.
        var thread = D(doc.Internals.Catalog.Elements.GetArray("/Threads")!.Elements[0]);
        var bead = D(thread.Elements["/F"]);
        Assert.Same(bead, D(bead.Elements["/N"]));
        Assert.Equal(page.Reference!.ObjectID, ((PdfReference)bead.Elements["/P"]!).ObjectID);

        using var engine = PdfEngine.Open(extracted, InputFileTypes.PDF, null);
        Assert.Equal(1, engine.PageCount);
        Assert.Contains("Visible page", engine.GetText(0).Text);
    }

    [Fact]
    public void DeletingAPageDropsWhatLedToIt()
    {
        var deleted = PageEditor.Delete(CrossLinked(), null, [1]);
        Assert.DoesNotContain("SECRET", PdfText.Everything(deleted));
        using var doc = PdfReader.Open(new MemoryStream(deleted), PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.PageCount);
    }

    /// <summary>A two-page source whose page 1 has a button and a link jumping to page 2 (SECRET).</summary>
    private static byte[] Source() => Core2Pdf.Build([
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Annots [5 0 R 9 0 R] /Resources << /Font << /F1 8 0 R >> >> >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R /Resources << /Font << /F1 8 0 R >> >> >>",
        "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (Go) /Rect [72 72 172 100] /P 3 0 R /A << /S /GoTo /D [4 0 R /Fit] >> /AA << /U << /S /GoTo /D [4 0 R /Fit] >> >> >>",
        Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (Cover) Tj ET"),
        Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (SECRET appendix) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /Annot /Subtype /Link /Rect [72 200 172 220] /Dest [4 0 R /Fit] >>",
    ]);

    [Fact]
    public void InsertedButtonsDontBringHiddenPagesAlong()
    {
        var inserted = PageEditor.Insert(TestPdf.Make(2), null, new PdfSource(Source(), null, "src.pdf"), [0], 1);
        Assert.DoesNotContain("SECRET", PdfText.Everything(inserted));
        Assert.Equal([1, 0, 2], TestPdf.PageNumbers(inserted));
        using (var doc = PdfReader.Open(new MemoryStream(inserted), PdfDocumentOpenMode.Import))
        {
            var annots = doc.Pages[1].Elements.GetArray("/Annots")!;
            var button = D(Assert.Single(annots.Elements));
            Assert.Equal("Go", button.Elements.GetString("/T"));
            Assert.False(button.Elements.ContainsKey("/A"));
            Assert.False(D(button.Elements["/AA"]).Elements.ContainsKey("/U"));
        }

        // With its target page imported too, the button jumps to the imported copy.
        var both = PageEditor.Insert(TestPdf.Make(2), null, new PdfSource(Source(), null, "src.pdf"), null, 2);
        using (var doc = PdfReader.Open(new MemoryStream(both), PdfDocumentOpenMode.Import))
        {
            Assert.Equal(4, doc.PageCount);
            var button = D(doc.Pages[2].Elements.GetArray("/Annots")!.Elements[0]);
            Assert.Equal("Go", button.Elements.GetString("/T"));
            foreach (var action in new[] { D(button.Elements["/A"]), D(D(button.Elements["/AA"]).Elements["/U"]) })
            {
                var dest = (PdfArray)(action.Elements["/D"] is PdfReference r ? r.Value : action.Elements["/D"])!;
                Assert.Equal(doc.Pages[3].Reference!.ObjectID, ((PdfReference)dest.Elements[0]).ObjectID);
            }
        }
        Assert.Contains("SECRET", PdfText.Everything(both));
    }

    [Fact]
    public void DuplicatedAndMergedPagesDontBringHiddenPagesAlong()
    {
        var merged = PageEditor.Merge([new PdfSource(Source(), null, "a.pdf")], false);
        var duplicated = PageEditor.Duplicate(Source(), null, [0]);
        foreach (var pdf in new[] { merged, duplicated })
        {
            using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
            var pages = Enumerable.Range(0, doc.PageCount).Select(i => (PdfDictionary)doc.Pages[i]).ToList();
            // Every page object in the file is one of the document's pages: no hidden copies.
            Assert.All(doc.Internals.GetAllObjects().OfType<PdfDictionary>().Where(d => d.Elements.GetName("/Type") == "/Page"),
                p => Assert.Contains(pages, q => q.Reference!.ObjectID == p.Reference!.ObjectID));
        }
    }

    // ---- Page labels ----

    /// <summary>Five pages labelled i, ii, then (with <paramref name="prefix"/>) numbers from <paramref name="start"/>.</summary>
    private static byte[] Labelled(string prefix = "", int start = 1)
    {
        using var doc = PdfReader.Open(new MemoryStream(TestPdf.Make(5)), PdfDocumentOpenMode.Modify);
        var roman = new PdfDictionary(doc);
        roman.Elements.SetName("/S", "/r");
        var arabic = new PdfDictionary(doc);
        arabic.Elements.SetName("/S", "/D");
        if (prefix.Length > 0) arabic.Elements.SetString("/P", prefix);
        if (start != 1) arabic.Elements.SetInteger("/St", start);
        var tree = new PdfDictionary(doc);
        tree.Elements["/Nums"] = new PdfArray(doc, new PdfInteger(0), roman, new PdfInteger(2), arabic);
        doc.Internals.Catalog.Elements["/PageLabels"] = tree;
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static string[] Labels(byte[] pdf) => DocumentFeatures.PageLabels(pdf, null)!;

    [Fact]
    public void PageLabelsFollowTheirPages()
    {
        var pdf = Labelled();
        Assert.Equal(["i", "ii", "1", "2", "3"], Labels(pdf));
        Assert.Equal(["i", "1", "2", "3"], Labels(PageEditor.Delete(pdf, null, [1])));
        Assert.Equal(["3", "2", "1", "ii", "i"], Labels(PageEditor.Reverse(pdf, null, 5)));
        Assert.Equal(["3", "i", "ii", "1", "2"], Labels(PageEditor.Move(pdf, null, 5, [4], 0)));
        Assert.Equal(["1", "2"], Labels(PageEditor.Extract(pdf, null, [2, 3])));
        Assert.Equal(["ii", "1"], Labels(PageEditor.Extract(pdf, null, [1, 2])));
        // New pages continue the numbering of the page before them.
        Assert.Equal(["i", "ii", "iii", "1", "2", "3"], Labels(PageEditor.InsertBlank(pdf, null, 2, 1, 612, 792)));
        Assert.Equal(["i", "ii", "iii", "iv", "1", "2", "3"], Labels(PageEditor.Insert(pdf, null, new PdfSource(TestPdf.Make(2), null, "x.pdf"), null, 2)));
        Assert.Equal(["i", "ii", "ii", "1", "2", "3"], Labels(PageEditor.Duplicate(pdf, null, [0])));
        Assert.Equal(["1", "i", "ii", "1", "2", "3"], Labels(PageEditor.InsertBlank(pdf, null, 0, 1, 612, 792)));
        // Edits that keep the pages leave the labels alone.
        Assert.Equal(["i", "ii", "1", "2", "3"], Labels(PageEditor.Rotate(pdf, null, [0], 90)));
    }

    [Fact]
    public void PageLabelRangesKeepPrefixAndStart()
    {
        var pdf = Labelled("A-", 5);
        Assert.Equal(["i", "ii", "A-5", "A-6", "A-7"], Labels(pdf));
        var deleted = PageEditor.Delete(pdf, null, [3]);
        Assert.Equal(["i", "ii", "A-5", "A-7"], Labels(deleted));
        using var doc = PdfReader.Open(new MemoryStream(deleted), PdfDocumentOpenMode.Import);
        var nums = D(doc.Internals.Catalog.Elements["/PageLabels"]).Elements.GetArray("/Nums")!;
        Assert.Equal(6, nums.Elements.Count); // three ranges: i–ii, A-5, A-7
        var last = D(nums.Elements[5]);
        Assert.Equal("A-", last.Elements.GetString("/P"));
        Assert.Equal(7, last.Elements.GetInteger("/St"));
    }

    // ---- Page boxes ----

    private static byte[] Boxed(string boxes) => Core2Pdf.Build([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {boxes} /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        Core2Pdf.Stream("BT /F1 30 Tf 50 400 Td (Hello boxes) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]);

    [Fact]
    public void UncropKeepsThePrintBoxes()
    {
        var pdf = Boxed("/TrimBox [10 10 600 780] /BleedBox [-5 -5 620 800] /ArtBox [700 700 800 800]");
        var cropped = PageEditor.Crop(pdf, null, [0], new Margins(20, 20, 20, 20));
        var uncropped = PageEditor.Uncrop(cropped, null, [0]);
        using var doc = PdfReader.Open(new MemoryStream(uncropped), PdfDocumentOpenMode.Import);
        var page = doc.Pages[0];
        Assert.False(page.Elements.ContainsKey("/CropBox"));
        Assert.Equal([10.0, 10, 600, 780], Box(page, "/TrimBox"));
        Assert.Equal([0.0, 0, 612, 792], Box(page, "/BleedBox")); // clipped to the media box
        Assert.False(page.Elements.ContainsKey("/ArtBox")); // entirely outside it

        static double[] Box(PdfPage page, string key) =>
            [.. page.Elements.GetArray(key)!.Elements.Select(e => e is PdfInteger i ? i.Value : ((PdfReal)e).Value)];
    }

    [Fact]
    public void ACropBoxBeyondTheMediaBoxIsClipped()
    {
        // MuPDF (like every viewer) shows the CropBox clipped to the MediaBox: 400 x 500 here, not 500 x 600.
        var pdf = Boxed("/CropBox [-100 -100 400 500]");
        using (var engine = PdfEngine.Open(pdf, InputFileTypes.PDF, null))
            Assert.Equal((400.0, 500.0), (engine.GetPage(0).Width, engine.GetPage(0).Height));
        using (var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import))
            Assert.Equal((400.0, 500.0), PdfObj.DisplaySize(doc.Pages[0]));

        // Redaction maps the page image onto the visible area: the text stays where it was.
        var text = HelloBox(pdf);
        var redacted = Redaction.Apply(pdf, null, new Dictionary<int, IReadOnlyList<RectD>> { [0] = [new RectD(0, 0, 2, 2)] }, dpi: 144);
        Assert.Equal(Core2Pdf.Ink(pdf, 0, text), Core2Pdf.Ink(redacted, 0, text), 2);
        Assert.True(Core2Pdf.Ink(redacted, 0, text) > 0.05);

        // Fill & Sign text lands where it was put, relative to the page's own text as the viewer shows it.
        var stamped = TextStamps.Put(pdf, null, 0, new RectD(text.X, text.Bottom + 20, 200, 30), StampKind.Text, "Stamped", 20);
        Assert.Equal(text.X, Assert.Single(TextStamps.Read(stamped, null)).Rect.X, 1);
        var lines = Core2Pdf.Lines(stamped);
        var hello = lines.Single(l => l.Text.Contains("Hello boxes")).Box;
        var stamp = lines.Single(l => l.Text.Contains("Stamped")).Box;        Assert.InRange(stamp.X - hello.X, -4, 6);
        Assert.InRange(stamp.Y - hello.Bottom, 14, 30);
    }

    private static RectD HelloBox(byte[] pdf)
    {
        using var engine = PdfEngine.Open(pdf, InputFileTypes.PDF, null);
        var t = engine.GetText(0);
        var (s, e) = t.Find("Hello boxes", true, false).Single();
        return t.GetRects(s, e).Aggregate(default(RectD), (a, r) => a.Union(r));
    }
}
