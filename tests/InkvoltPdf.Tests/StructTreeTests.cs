using InkvoltPdf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace InkvoltPdf.Tests;

/// <summary>Tagged PDFs: removed pages must not stay reachable (and in the file) through the structure tree.</summary>
public class StructTreeTests
{
    /// <summary>
    /// Three tagged pages. Page 2 has its own paragraph (also in the /IDTree) and starts a paragraph that continues on
    /// page 3 (an MCR to page 3). Each page's text is a unique marker.
    /// </summary>
    private static byte[] Tagged() => Core2Pdf.Build([
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 9 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /StructParents 0 /Resources << /Font << /F1 10 0 R >> >> >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R /StructParents 1 /Resources << /Font << /F1 10 0 R >> >> >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 8 0 R /StructParents 2 /Resources << /Font << /F1 10 0 R >> >> >>",
        Core2Pdf.Stream("/P << /MCID 0 >> BDC BT /F1 24 Tf 72 700 Td (PAGEONEMARK) Tj ET EMC"),
        Core2Pdf.Stream("/P << /MCID 0 >> BDC BT /F1 24 Tf 72 700 Td (PAGETWOMARK) Tj ET EMC /P << /MCID 1 >> BDC BT /F1 24 Tf 72 600 Td (PAGETWOSPAN) Tj ET EMC"),
        Core2Pdf.Stream("/P << /MCID 0 >> BDC BT /F1 24 Tf 72 700 Td (PAGETHREEMARK) Tj ET EMC"),
        "<< /Type /StructTreeRoot /K 11 0 R /ParentTree 12 0 R /IDTree << /Names [(p2) 14 0 R] >> /ParentTreeNextKey 3 >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /StructElem /S /Document /P 9 0 R /K [13 0 R 14 0 R 15 0 R] >>",
        "<< /Nums [0 [13 0 R] 1 [14 0 R 15 0 R] 2 [15 0 R]] >>",
        "<< /Type /StructElem /S /P /P 11 0 R /Pg 3 0 R /K 0 >>",
        "<< /Type /StructElem /S /P /P 11 0 R /Pg 4 0 R /K 0 /ID (p2) >>",
        "<< /Type /StructElem /S /P /P 11 0 R /Pg 4 0 R /K [1 << /Type /MCR /Pg 5 0 R /MCID 0 >>] >>",
    ]);

    [Fact]
    public void DeletedPageContentLeavesTheFile()
    {
        var data = Tagged();
        Assert.Contains("PAGETWOMARK", Core2RedactionTests.AllBytesInflated(data));

        var deleted = PageEditor.Delete(data, null, [1]);

        var all = Core2RedactionTests.AllBytesInflated(deleted);
        Assert.DoesNotContain("PAGETWOMARK", all);
        Assert.DoesNotContain("PAGETWOSPAN", all);
        Assert.Contains("PAGEONEMARK", all);
        Assert.Contains("PAGETHREEMARK", all);

        using var doc = Core2Pdf.Open(deleted);
        var root = doc.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot")!;
        var document = Deref(root.Elements["/K"]) as PdfDictionary;
        var kids = (PdfArray)Deref(document!.Elements["/K"])!;
        Assert.Equal(2, kids.Elements.Count); // page 1's paragraph, and the one continuing on page 3
        var continued = (PdfDictionary)Deref(kids.Elements[1])!;
        Assert.False(continued.Elements.ContainsKey("/Pg"));
        var mcr = (PdfDictionary)Deref(Assert.Single(((PdfArray)Deref(continued.Elements["/K"])!).Elements))!;
        Assert.Same(doc.Pages[1], Deref(mcr.Elements["/Pg"]));

        var nums = ((PdfDictionary)Deref(root.Elements["/ParentTree"])!).Elements.GetArray("/Nums")!;
        Assert.Equal([0, 2], Enumerable.Range(0, nums.Elements.Count / 2).Select(i => ((PdfInteger)nums.Elements[2 * i]).Value));
        Assert.Empty(((PdfDictionary)Deref(root.Elements["/IDTree"])!).Elements.GetArray("/Names")!.Elements);
    }

    [Fact]
    public void ExtractedPagesDoNotCarryTheOthers()
    {
        var extracted = PageEditor.Extract(Tagged(), null, [1]);
        var all = Core2RedactionTests.AllBytesInflated(extracted);
        Assert.Contains("PAGETWOMARK", all);
        Assert.DoesNotContain("PAGEONEMARK", all);
        Assert.DoesNotContain("PAGETHREEMARK", all);
        Assert.Contains("PAGETWOSPAN", Core2Pdf.AllText(extracted));
    }

    [Fact]
    public void RedactedPageDropsOutOfTheStructureTree()
    {
        var redacted = Redaction.Apply(Tagged(), null, new Dictionary<int, IReadOnlyList<RectD>> { [0] = [new RectD(0, 0, 10, 10)] }, dpi: 72);
        using var doc = Core2Pdf.Open(redacted);
        var root = doc.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot")!;
        var kids = (PdfArray)Deref(((PdfDictionary)Deref(root.Elements["/K"])!).Elements["/K"])!;
        Assert.Equal(2, kids.Elements.Count); // page 1's paragraph is gone: its marked content no longer exists
        var nums = ((PdfDictionary)Deref(root.Elements["/ParentTree"])!).Elements.GetArray("/Nums")!;
        Assert.DoesNotContain(0, Enumerable.Range(0, nums.Elements.Count / 2).Select(i => ((PdfInteger)nums.Elements[2 * i]).Value));
    }

    private static PdfItem? Deref(PdfItem? item) => item is PdfReference r ? r.Value : item;
}
