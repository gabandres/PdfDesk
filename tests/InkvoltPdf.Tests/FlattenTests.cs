using InkvoltPdf.Core;
using PdfSharp.Pdf;

namespace InkvoltPdf.Tests;

public class FlattenTests
{
    private static List<string> Subtypes(byte[] pdf, int page = 0)
    {
        using var doc = Core2Pdf.Open(pdf);
        return [.. PdfObj.Array(doc.Pages[page].Elements["/Annots"])?.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>().Select(a => PdfObj.Name(a.Elements["/Subtype"]) ?? "") ?? []];
    }

    [Fact]
    public void FormValuesBecomePageText()
    {
        var form = Core2Pdf.Form(
        [
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [72 600 300 630] /P 3 0 R /F 4 /DA (/Helv 12 Tf 0 g) >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (City) /Rect [72 500 300 530] /P 3 0 R /F 4 /DA (/Helv 12 Tf 0 g) >>",
        ]);
        var filled = FormEditor.SetValues(form, null, new Dictionary<string, string> { ["Name"] = "Ada Lovelace", ["City"] = "London" });
        Assert.DoesNotContain("Ada Lovelace", Core2Pdf.AllText(filled, annotations: false));

        var flat = Flattener.Flatten(filled, null);
        var text = Core2Pdf.AllText(flat, annotations: false);
        Assert.Contains("Ada Lovelace", text);
        Assert.Contains("London", text);
        Assert.Empty(Subtypes(flat));
        Assert.Empty(FormEditor.Read(flat, null));
        using var doc = Core2Pdf.Open(flat);
        Assert.Null(doc.Internals.Catalog.Elements["/AcroForm"]);
    }

    [Fact]
    public void ValuesWithoutAppearancesGetOneFirst()
    {
        // /V set but no /AP (a producer relying on NeedAppearances).
        var form = Core2Pdf.Form(["<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /V (Grace Hopper) /Rect [72 600 300 630] /P 3 0 R /F 4 /DA (/Helv 12 Tf 0 g) >>"],
            acroExtra: "/NeedAppearances true");
        var flat = Flattener.Flatten(form, null);
        Assert.Contains("Grace Hopper", Core2Pdf.AllText(flat, annotations: false));
    }

    private static byte[] Annotated() => Core2Pdf.Build(
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 6 0 R 8 0 R 9 0 R 12 0 R 13 0 R] >>",
        "<< /Type /Annot /Subtype /Square /Rect [100 100 200 150] /F 4 /AP << /N 5 0 R >> /Popup 13 0 R >>",
        Core2Pdf.Stream("0 g 0 0 20 10 re f", "/Type /XObject /Subtype /Form /BBox [0 0 20 10] /Matrix [0 1 -1 0 0 0]"),
        "<< /Type /Annot /Subtype /Square /Rect [300 300 400 400] /F 2 /AP << /N 7 0 R >> >>",
        Core2Pdf.Stream("0 g 0 0 10 10 re f", "/BBox [0 0 10 10]"),
        "<< /Type /Annot /Subtype /Link /Rect [50 50 60 60] /A << /S /URI /URI (https://example.com/) >> >>",
        "<< /Type /Annot /Subtype /Widget /FT /Btn /T (Agree) /Rect [400 600 420 620] /F 4 /AS /On /AP << /N << /On 10 0 R /Off 11 0 R >> >> >>",
        Core2Pdf.Stream("0 g 0 0 20 20 re f", "/Type /XObject /Subtype /Form /BBox [0 0 20 20]"),
        Core2Pdf.Stream(" ", "/Type /XObject /Subtype /Form /BBox [0 0 20 20]"),
        "<< /Type /Annot /Subtype /Square /Rect [450 100 500 150] /F 4 /CA 0.5 /AP << /N 7 0 R >> >>",
        "<< /Type /Annot /Subtype /Popup /Rect [200 200 300 300] /Parent 4 0 R >>",
    ]);

    [Fact]
    public void AppearancesAreMappedOntoTheirRect()
    {
        var pdf = Annotated();
        var flat = Flattener.Flatten(pdf, null, new FlattenOptions { FormFields = false });
        // The link and the check box stay; the squares and the popup go.
        Assert.Equal(["Link", "Widget"], Subtypes(flat));

        // The rotated 20x10 appearance fills the 100x50 rect (BBox through /Matrix, fitted to /Rect).
        Assert.True(Core2Pdf.Ink(flat, 0, new RectD(102, 792 - 148, 96, 46)) > 0.97);
        Assert.True(Core2Pdf.Ink(flat, 0, new RectD(205, 792 - 148, 40, 46)) < 0.01);
        // The hidden square was dropped, not drawn.
        Assert.True(Core2Pdf.Ink(flat, 0, new RectD(302, 792 - 398, 96, 96)) < 0.01);
        // Half-transparent black on white: mid gray (the Ink threshold counts it as dark only when darker than 128).
        var gray = TestImages.PixelAt(flat, 0, 475, 792 - 125);
        Assert.InRange(gray.R, 100, 160);

        var all = Flattener.Flatten(pdf, null);
        Assert.Equal(["Link"], Subtypes(all));
        // The check box's /AS state (On) is what's drawn.
        Assert.True(Core2Pdf.Ink(all, 0, new RectD(402, 792 - 618, 16, 16)) > 0.97);

        var everything = Flattener.Flatten(pdf, null, new FlattenOptions { Links = true });
        Assert.Empty(Subtypes(everything));
    }

    [Fact]
    public void InheritedResourcesAndSharedAnnotsSurvive()
    {
        // Both pages take their font from the page tree and share one /Annots array.
        var pdf = Core2Pdf.Build(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Annots 7 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Annots 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            Core2Pdf.Stream("BT /F1 24 Tf 72 720 Td (Inherited font) Tj ET"),
            "[8 0 R]",
            "<< /Type /Annot /Subtype /Square /Rect [100 100 200 150] /F 4 /AP << /N 9 0 R >> >>",
            Core2Pdf.Stream("0 g 0 0 10 10 re f", "/Type /XObject /Subtype /Form /BBox [0 0 10 10]"),
        ]);
        var flat = Flattener.Flatten(pdf, null);
        for (var page = 0; page < 2; page++)
        {
            Assert.Contains("Inherited font", Core2Pdf.AllText(flat, page, annotations: false));
            Assert.Empty(Subtypes(flat, page));
            Assert.True(Core2Pdf.Ink(flat, page, new RectD(102, 792 - 148, 96, 46)) > 0.97);
        }
    }

    [Fact]
    public void NothingToFlattenLeavesTheBytes()
    {
        var pdf = TestPdf.Make(1);
        Assert.Same(pdf, Flattener.Flatten(pdf, null));
    }

    [Fact]
    public void SignedFilesNeedConsent()
    {
        var signed = SignedEditTests.Signed();
        var values = new Dictionary<string, string> { ["Name"] = "Ada" };
        var filled = FormEditor.SetValues(signed, null, values);
        Assert.Throws<SignaturesWouldBreakException>(() => Flattener.Flatten(filled, null));
        var (flat, report) = SignedEdits.Run(() => Flattener.Flatten(filled, null), allowRewrite: true);
        Assert.False(report.Appended);
        Assert.Contains("Ada", Core2Pdf.AllText(flat, annotations: false));
        // The signature field stays a field.
        Assert.Equal(["Widget"], Subtypes(flat));
        Assert.Contains(FormEditor.Read(flat, null), w => w.FieldName == "Approval");
    }
}
