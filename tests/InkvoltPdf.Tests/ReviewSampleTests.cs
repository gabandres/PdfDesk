using InkvoltPdf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Tests;

/// <summary>
/// Sample documents for the review features (labels, opening settings, comments with a reply, attachments, layers,
/// signatures, XFA). The test checks what the app reads from them; with INKVOLTPDF_SAMPLES set to a folder it also writes
/// them there, for UI checks with <c>InkvoltPdf.exe file.pdf --script steps.txt</c>.
/// </summary>
public class ReviewSampleTests
{
    internal static byte[] Features()
    {
        var data = TestPdf.Make(5);
        using (var doc = PdfReader.Open(new MemoryStream(data), PdfDocumentOpenMode.Modify))
        {
            var c = doc.Internals.Catalog.Elements;
            var nums = new PdfArray(doc,
                new PdfInteger(0), Dict(doc, ("/S", new PdfName("/r"))),
                new PdfInteger(2), Dict(doc, ("/S", new PdfName("/D"))));
            c["/PageLabels"] = Dict(doc, ("/Nums", nums));
            c["/PageMode"] = new PdfName("/UseAttachments");
            c["/PageLayout"] = new PdfName("/TwoColumnLeft");
            c["/ViewerPreferences"] = Dict(doc, ("/DisplayDocTitle", new PdfBoolean(true)));
            c["/OpenAction"] = new PdfArray(doc, doc.Pages[3].Reference!, new PdfName("/XYZ"), new PdfInteger(0), new PdfInteger(700), new PdfInteger(0));
            doc.Info.Title = "Quarterly Review";
            using var ms = new MemoryStream();
            doc.Save(ms, false);
            data = ms.ToArray();
        }

        var noteId = Annotations.NewId();
        data = Annotations.AddNote(data, null, 0, 400, 90, "Please check these numbers.", "Alice", id: noteId);
        data = Annotations.AddTextMarkup(data, null, 1, TextMarkupKind.Highlight, [new RectD(72, 122, 190, 24)], AnnotColor.Yellow, "Nice phrase", "Bob");

        // A reply to Alice's note (/IRT), as other editors write them.
        using (var doc = PdfReader.Open(new MemoryStream(data), PdfDocumentOpenMode.Modify))
        {
            var page = doc.Pages[0];
            var annots = (PdfArray)((page.Elements["/Annots"] as PdfReference)?.Value ?? page.Elements["/Annots"]!);
            var note = annots.Elements.Select(e => (e as PdfReference)?.Value as PdfDictionary).First(d => d?.Elements.GetString("/NM") == noteId)!;
            var reply = new PdfDictionary(doc);
            reply.Elements["/Type"] = new PdfName("/Annot");
            reply.Elements["/Subtype"] = new PdfName("/Text");
            reply.Elements["/Rect"] = new PdfArray(doc, new PdfReal(400), new PdfReal(682), new PdfReal(420), new PdfReal(702));
            reply.Elements["/Contents"] = new PdfString("Checked, they add up.");
            reply.Elements["/T"] = new PdfString("Bob");
            reply.Elements["/IRT"] = note.Reference!;
            reply.Elements["/F"] = new PdfInteger(4);
            doc.Internals.AddObject(reply);
            annots.Elements.Add(reply.Reference!);
            using var ms = new MemoryStream();
            doc.Save(ms, false);
            data = ms.ToArray();
        }

        data = Attachments.Add(data, null, "notes.txt", "Synthetic notes for the attachment pane.\r\n"u8.ToArray(), "Meeting notes");
        data = Attachments.Add(data, null, "inner.pdf", TestPdf.Make(2, "inner"), "A PDF inside the PDF");
        return data;
    }

    internal static byte[] XfaOnly() => Core2Pdf.Build([
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA 4 0 R >> /NeedsRendering true >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        Core2Pdf.Stream("<xdp:xdp/>"),
    ]);

    private static PdfDictionary Dict(PdfDocument doc, params (string Key, PdfItem Value)[] entries)
    {
        var d = new PdfDictionary(doc);
        foreach (var (k, v) in entries) d.Elements[k] = v;
        return d;
    }

    [Fact]
    public void SamplesCarryTheReviewFeatures()
    {
        var features = Features();
        Assert.Equal(["i", "ii", "1", "2", "3"], DocumentFeatures.PageLabels(features, null)!);
        var prefs = DocumentFeatures.ReadViewerPrefs(features, null);
        Assert.Equal(PageModeKind.Attachments, prefs.PageMode);
        Assert.Equal(PageLayoutKind.TwoColumnLeft, prefs.PageLayout);
        Assert.Equal(3, prefs.OpenAction!.PageIndex);
        Assert.True(prefs.DisplayDocTitle);
        var annots = Annotations.Read(features, null);
        Assert.Equal(3, annots.Count);
        Assert.Single(annots, a => a.ReplyTo is not null);
        Assert.Equal(["notes.txt", "inner.pdf"], Attachments.List(features, null).Select(a => a.FileName).Order(StringComparer.Ordinal).Reverse());
        Assert.Equal("Quarterly Review", PageEditor.ReadMetadata(features, null).Title);

        var signed = Core2SignatureTests.Signed();
        Assert.Contains(Signatures.Verify(signed), s => s.Status == SignatureStatus.Valid);
        Assert.Equal((true, true), FormEditor.HasXfa(XfaOnly(), null));

        if (Environment.GetEnvironmentVariable("INKVOLTPDF_SAMPLES") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "features.pdf"), features);
            File.WriteAllBytes(Path.Combine(dir, "layers.pdf"), Core2FeatureTests.LayeredPdf());
            File.WriteAllBytes(Path.Combine(dir, "signed.pdf"), signed);
            File.WriteAllBytes(Path.Combine(dir, "xfa.pdf"), XfaOnly());
            File.WriteAllBytes(Path.Combine(dir, "plain.pdf"), TestPdf.Make(3));
            File.WriteAllText(Path.Combine(dir, "attach-me.txt"), "Attached by the dev script.");
        }
    }
}
