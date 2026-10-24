using MuPDFCore;
using InkvoltPdf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Tests;

public class Core2FeatureTests
{
    private static byte[] Modify(byte[] data, Action<PdfDocument> change)
    {
        using var doc = PdfReader.Open(new MemoryStream(data), PdfDocumentOpenMode.Modify);
        change(doc);
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static PdfDictionary Dict(PdfDocument doc, params (string Key, PdfItem Value)[] entries)
    {
        var d = new PdfDictionary(doc);
        foreach (var (k, v) in entries) d.Elements[k] = v;
        return d;
    }

    // ---------------- Page labels and open settings ----------------

    [Fact]
    public void ReadsPageLabels()
    {
        var data = Modify(TestPdf.Make(7), doc =>
        {
            var nums = new PdfArray(doc,
                new PdfInteger(0), Dict(doc, ("/S", new PdfName("/r"))),
                new PdfInteger(2), Dict(doc, ("/S", new PdfName("/D"))),
                new PdfInteger(4), Dict(doc, ("/S", new PdfName("/D")), ("/P", new PdfString("A-")), ("/St", new PdfInteger(8))),
                new PdfInteger(6), Dict(doc, ("/S", new PdfName("/A")), ("/St", new PdfInteger(27))));
            doc.Internals.Catalog.Elements["/PageLabels"] = Dict(doc, ("/Nums", nums));
        });
        var labels = DocumentFeatures.PageLabels(data, null);
        Assert.Equal(["i", "ii", "1", "2", "A-8", "A-9", "AA"], labels!);
        Assert.Equal(4, DocumentFeatures.FindPageByLabel(labels, "A-8"));
        Assert.Equal(1, DocumentFeatures.FindPageByLabel(labels, "II"));
        Assert.Null(DocumentFeatures.FindPageByLabel(labels, "99"));
        Assert.Null(DocumentFeatures.PageLabels(TestPdf.Make(2), null));
        Assert.Equal("xiv", DocumentFeatures.FormatNumber(14, "r"));
    }

    [Fact]
    public void HostilePageLabelsAndCyclicTreesStayCheap()
    {
        var data = Core2Pdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /PageLabels 4 0 R /Names << /EmbeddedFiles 5 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Kids [4 0 R 4 0 R 4 0 R] /Nums [0 << /S /A /St 2147483647 >>] >>",
            "<< /Kids [5 0 R 5 0 R 5 0 R 5 0 R] >>",
        ]);
        var labels = DocumentFeatures.PageLabels(data, null)!;
        Assert.Single(labels);
        Assert.True(labels[0].Length < 32, labels[0]);
        Assert.Empty(Attachments.List(data, null));
        Assert.Equal("2147483647", DocumentFeatures.FormatNumber(int.MaxValue, "R"));
    }

    [Fact]
    public void ReadsViewerPreferencesAndOpenAction()
    {
        var data = Modify(TestPdf.Make(3), doc =>
        {
            var c = doc.Internals.Catalog.Elements;
            c["/PageMode"] = new PdfName("/UseOutlines");
            c["/PageLayout"] = new PdfName("/TwoColumnRight");
            c["/ViewerPreferences"] = Dict(doc, ("/DisplayDocTitle", new PdfBoolean(true)));
            c["/OpenAction"] = new PdfArray(doc, doc.Pages[1].Reference!, new PdfName("/XYZ"), new PdfInteger(0), new PdfInteger(700), new PdfReal(1.5));
        });
        var prefs = DocumentFeatures.ReadViewerPrefs(data, null);
        Assert.Equal(PageModeKind.Outlines, prefs.PageMode);
        Assert.Equal(PageLayoutKind.TwoColumnRight, prefs.PageLayout);
        Assert.True(prefs.DisplayDocTitle);
        Assert.Equal(new PageDestination(1, ZoomKind.XYZ, 0, 92, 1.5), prefs.OpenAction);

        var named = Modify(TestPdf.Make(3), doc =>
        {
            var dest = new PdfArray(doc, doc.Pages[2].Reference!, new PdfName("/FitH"), new PdfInteger(500));
            var tree = Dict(doc, ("/Names", new PdfArray(doc, new PdfString("chapter"), dest)));
            doc.Internals.Catalog.Elements["/Names"] = Dict(doc, ("/Dests", tree));
            doc.Internals.Catalog.Elements["/OpenAction"] = Dict(doc, ("/S", new PdfName("/GoTo")), ("/D", new PdfString("chapter")));
        });
        var p2 = DocumentFeatures.ReadViewerPrefs(named, null);
        Assert.Equal(new PageDestination(2, ZoomKind.FitH, null, 292, null), p2.OpenAction);
        Assert.Null(p2.PageMode);
        Assert.Null(DocumentFeatures.ReadViewerPrefs(TestPdf.Make(1), null).OpenAction);
    }

    // ---------------- Annotations ----------------

    private static List<RectD> LineRects(byte[] data, string word, int page = 0)
    {
        using var engine = PdfEngine.Open(data, InputFileTypes.PDF, null);
        var text = engine.GetText(page);
        var (s, e) = text.Find(word, true, false).First();
        return text.GetRects(s, e);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void HighlightsNotesRepliesAndDeletes(int rotation)
    {
        var doc = rotation == 0 ? TestPdf.Make(1) : PageEditor.Rotate(TestPdf.Make(1), null, [0], rotation);
        var rects = LineRects(doc, "quick brown");
        var id = Annotations.NewId();
        var data = Annotations.AddTextMarkup(doc, null, 0, TextMarkupKind.Highlight, rects, AnnotColor.Yellow, "check this", "Gabriel", id);
        data = Annotations.AddTextMarkup(data, null, 0, TextMarkupKind.StrikeOut, LineRects(data, "lazy"), AnnotColor.Red, null, "Gabriel");
        data = Annotations.AddNote(data, null, 0, 400, 50, "A sticky note", "Ana");

        var list = Annotations.Read(data, null);
        Assert.Equal(3, list.Count);
        var hl = list.Single(a => a.Id == id);
        Assert.Equal(AnnotationKind.Highlight, hl.Kind);
        Assert.Equal("check this", hl.Contents);
        Assert.Equal("Gabriel", hl.Author);
        Assert.NotNull(hl.Modified);
        var q = Assert.Single(hl.QuadRects);
        Assert.Equal(rects[0].X, q.X, 0.5);
        Assert.Equal(rects[0].Y, q.Y, 0.5);
        Assert.Equal(rects[0].Width, q.Width, 0.5);
        var note = list.Single(a => a.Kind == AnnotationKind.Text);
        Assert.Equal(new RectD(400, 50, 20, 20), note.Rect with { X = Math.Round(note.Rect.X), Y = Math.Round(note.Rect.Y), Width = Math.Round(note.Rect.Width), Height = Math.Round(note.Rect.Height) });

        // Drawn: the highlight tints the words yellow, the text stays readable; the note icon is drawn.
        using (var engine = PdfEngine.Open(data, InputFileTypes.PDF, null))
        {
            var img = engine.Render(0, 1, rects[0]);
            var yellow = 0;
            for (var i = 0; i + 2 < img.Pixels.Length; i += 3)
                if (img.Pixels[i] < 120 && img.Pixels[i + 1] > 150 && img.Pixels[i + 2] > 200) yellow++;
            Assert.True(yellow > img.Width * img.Height / 4, "highlight should be yellow");
            Assert.Contains("quick brown", engine.GetText(0, cache: false).Text);
        }
        Assert.True(Core2Pdf.Ink(data, 0, new RectD(402, 52, 16, 16)) > 0.1);

        // A reply to the highlight, then edit and delete.
        var noteId = note.Id;
        data = Modify(data, d =>
        {
            var annots = d.Pages[0].Elements.GetArray("/Annots")!;
            var target = annots.Elements.Select(e => (e as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfDictionary).First(a => a?.Elements.GetString("/NM") == noteId)!;
            var hlDict = annots.Elements.Select(e => (e as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfDictionary).First(a => a?.Elements.GetString("/NM") == id)!;
            target.Elements["/IRT"] = hlDict.Reference!;
        });
        Assert.Equal(id, Annotations.Read(data, null).Single(a => a.Id == noteId).ReplyTo);
        data = Annotations.SetContents(data, null, id, "edited");
        Assert.Equal("edited", Annotations.Read(data, null).Single(a => a.Id == id).Contents);
        data = Annotations.Delete(data, null, id);
        var left = Annotations.Read(data, null);
        Assert.Single(left); // the reply went with it
        Assert.Equal(AnnotationKind.StrikeOut, left[0].Kind);
        Assert.Throws<InvalidOperationException>(() => Annotations.Delete(data, null, id));
    }

    [Fact]
    public void ListsStampsAndSkipsWidgets()
    {
        var data = TextStamps.Put(FormTests.MakeForm(), null, 0, new RectD(300, 300, 100, 20), StampKind.Text, "Hi", 12);
        var a = Assert.Single(Annotations.Read(data, null));
        Assert.True(a.IsInkvoltPdfStamp);
        Assert.Equal(AnnotationKind.FreeText, a.Kind);
    }

    // ---------------- Attachments ----------------

    [Fact]
    public void AddsListsExtractsAndRemovesAttachments()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello attachment ", 50)));
        var data = Attachments.Add(TestPdf.Make(1), null, @"C:\temp\notes.txt", bytes, "Meeting notes");
        data = Attachments.Add(data, null, "notes.txt", [1, 2, 3]);
        var list = Attachments.List(data, null);
        Assert.Equal(2, list.Count);
        var first = list.Single(a => a.FileName == "notes.txt");
        Assert.Equal("Meeting notes", first.Description);
        Assert.Equal(bytes.Length, first.Size);
        Assert.Equal(AttachmentSource.Document, first.Source);
        Assert.Equal(-1, first.PageIndex);
        Assert.Equal("text/plain", first.MimeType);
        Assert.NotNull(first.Modified);
        Assert.Equal(bytes, Attachments.Extract(data, null, first.Key));
        var second = list.Single(a => a.FileName == "notes (2).txt");
        Assert.Equal([1, 2, 3], Attachments.Extract(data, null, second.Key));

        data = Attachments.Remove(data, null, first.Key);
        Assert.Equal("notes (2).txt", Assert.Single(Attachments.List(data, null)).FileName);
        Assert.Empty(Attachments.List(TestPdf.Make(1), null));
    }

    [Fact]
    public void ListsFileAttachmentAnnotations()
    {
        var data = Modify(TestPdf.Make(2), doc =>
        {
            var stream = new PdfDictionary(doc);
            stream.CreateStream([9, 8, 7, 6]);
            doc.Internals.AddObject(stream);
            var fs = Dict(doc, ("/Type", new PdfName("/Filespec")), ("/F", new PdfString("data.bin")), ("/EF", Dict(doc, ("/F", stream.Reference!))));
            var annot = Dict(doc, ("/Type", new PdfName("/Annot")), ("/Subtype", new PdfName("/FileAttachment")), ("/Rect", new PdfArray(doc, new PdfInteger(10), new PdfInteger(10), new PdfInteger(30), new PdfInteger(30))), ("/FS", fs), ("/NM", new PdfString("att1")));
            doc.Internals.AddObject(annot);
            doc.Pages[1].Elements["/Annots"] = new PdfArray(doc, annot.Reference!);
        });
        var a = Assert.Single(Attachments.List(data, null));
        Assert.Equal(AttachmentSource.Annotation, a.Source);
        Assert.Equal(1, a.PageIndex);
        Assert.Equal("data.bin", a.FileName);
        Assert.Equal(4, a.Size);
        Assert.Equal([9, 8, 7, 6], Attachments.Extract(data, null, a.Key));
        Assert.Empty(Attachments.List(Attachments.Remove(data, null, a.Key), null));
    }

    // ---------------- Layers ----------------

    internal static byte[] LayeredPdf() => Core2Pdf.Build([
        "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /ON [5 0 R] /OFF [6 0 R] /Locked [6 0 R] /Order [[(Group) 5 0 R] 6 0 R] >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Properties << /L1 5 0 R /L2 6 0 R >> >> >>",
        Core2Pdf.Stream("/OC /L1 BDC 0 0 1 rg 100 600 100 100 re f EMC /OC /L2 BDC 1 0 0 rg 300 600 100 100 re f EMC"),
        "<< /Type /OCG /Name (Blue) >>",
        "<< /Type /OCG /Name (Red) >>",
    ]);

    [Fact]
    public void ListsLayersAndRendersChosenVisibility()
    {
        var data = LayeredPdf();
        var layers = Layers.List(data, null);
        Assert.Equal([new LayerInfo(5, "Blue", true, false), new LayerInfo(6, "Red", false, true)], layers);
        var order = Layers.ReadOrder(data, null);
        Assert.Equal("Group", order[0].Label);
        Assert.Equal(5, order[0].Children.Single().LayerId);
        Assert.Equal(6, order[1].LayerId);

        RectD blue = new(100, 92, 100, 100), red = new(300, 92, 100, 100);
        Assert.True(Core2Pdf.Ink(data, 0, blue) > 0.9);
        Assert.True(Core2Pdf.Ink(data, 0, red) < 0.01);

        var swapped = Layers.WithVisibility(data, null, new Dictionary<int, bool> { [5] = false, [6] = true });
        Assert.True(Core2Pdf.Ink(swapped, 0, blue) < 0.01);
        Assert.True(Core2Pdf.Ink(swapped, 0, red) > 0.9);

        var saved = Layers.SetDefaultVisibility(data, null, new Dictionary<int, bool> { [6] = true });
        Assert.All(Layers.List(saved, null), l => Assert.True(l.DefaultVisible));
        Assert.Empty(Layers.List(TestPdf.Make(1), null));
    }
}
