using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace PdfDesk.Tests;

/// <summary>Builds synthetic PDFs: page N shows "Page N" plus a marker word, so tests can identify pages by text.</summary>
internal static class TestPdf
{
    static TestPdf() => GlobalFontSettings.UseWindowsFontsUnderWindows = true;

    public static byte[] Make(int pages, string marker = "alpha")
    {
        using var doc = new PdfDocument();
        doc.Info.Title = "Synthetic";
        var font = new XFont("Arial", 20);
        for (var i = 1; i <= pages; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"Page {i} {marker}", font, XBrushes.Black, new XPoint(72, 100));
            gfx.DrawString("The quick brown fox", font, XBrushes.Black, new XPoint(72, 140));
            gfx.DrawString("jumps over the lazy dog", font, XBrushes.Black, new XPoint(72, 170));
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>The "Page N" number on each page, in order, read back through MuPDF (0 = no number).</summary>
    public static int[] PageNumbers(byte[] data)
    {
        using var engine = Core.PdfEngine.Open(data, MuPDFCore.InputFileTypes.PDF, null);
        return [.. Enumerable.Range(0, engine.PageCount).Select(i =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(engine.GetText(i).Text, @"Page (\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        })];
    }
}
