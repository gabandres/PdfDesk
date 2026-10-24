using System.Text;
using MuPDFCore;
using MuPDFCore.StructuredText;
using InkvoltPdf.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Tests;

/// <summary>Raw PDF building and inspection helpers for the Core2 tests.</summary>
internal static class Core2Pdf
{
    /// <summary>A PDF from object bodies (object 1 = catalog), numbered in order.</summary>
    public static byte[] Build(IReadOnlyList<string> objects, string trailerExtra = "")
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R {trailerExtra}>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    public static string Stream(string content, string dict = "") => $"<< {dict} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    /// <summary>A one-page form: the given field objects (numbered from 5) as widgets on page 3, with Helvetica in /DR as 4.</summary>
    public static byte[] Form(IReadOnlyList<string> fields, string acroExtra = "", string pageExtra = "", string catalogExtra = "", IReadOnlyList<string>? more = null, string extraAnnots = "")
    {
        var refs = string.Join(' ', Enumerable.Range(5, fields.Count).Select(i => $"{i} 0 R"));
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{refs}] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> {acroExtra} >> {catalogExtra} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [{refs} {extraAnnots}] /Resources << >> {pageExtra} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        objects.AddRange(fields);
        if (more is not null) objects.AddRange(more);
        return Build(objects);
    }

    /// <summary>The same document without /NeedAppearances, so viewers must use the appearance streams we wrote.</summary>
    public static byte[] WithoutNeedAppearances(byte[] data)
    {
        using var doc = PdfReader.Open(new MemoryStream(data), PdfDocumentOpenMode.Modify);
        if (doc.Internals.Catalog.Elements.GetDictionary("/AcroForm") is { } acro) acro.Elements.Remove("/NeedAppearances");
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    public static PdfDictionary? Field(PdfDocument doc, string name)
    {
        var acro = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!;
        foreach (var item in acro.Elements.GetArray("/Fields")!.Elements)
        {
            var d = (item is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : item) as PdfDictionary;
            if (d?.Elements.GetString("/T") == name) return d;
        }
        return null;
    }

    public static PdfDocument Open(byte[] data) => PdfReader.Open(new MemoryStream(data), PdfDocumentOpenMode.Import);

    /// <summary>Text lines MuPDF extracts from a page, annotations (and widget appearances) included, with their boxes.</summary>
    public static List<(string Text, RectD Box, List<(string Ch, RectD Box)> Chars)> Lines(byte[] data, int page = 0, bool annotations = true)
    {
        using var ctx = new MuPDFContext();
        using var doc = new MuPDFDocument(ctx, data, InputFileTypes.PDF);
        using var st = doc.GetStructuredTextPage(page, includeAnnotations: annotations);
        var list = new List<(string, RectD, List<(string, RectD)>)>();
        foreach (var block in st)
        {
            if (block is not MuPDFTextStructuredTextBlock tb) continue;
            foreach (var line in tb)
            {
                var chars = line.Select(c => (c.Character, RectD.FromEdges(c.BoundingQuad.LowerLeft.X, c.BoundingQuad.UpperLeft.Y, c.BoundingQuad.LowerRight.X, c.BoundingQuad.LowerLeft.Y))).ToList();
                var b = line.BoundingBox;
                list.Add((string.Concat(line.Select(c => c.Character)), RectD.FromEdges(b.X0, b.Y0, b.X1, b.Y1), chars));
            }
        }
        doc.Pages.Dispose();
        return list;
    }

    public static string AllText(byte[] data, int page = 0, bool annotations = true) =>
        string.Join("\n", Lines(data, page, annotations).Select(l => l.Text));

    /// <summary>Fraction of dark pixels in a page region rendered at 2x.</summary>
    public static double Ink(byte[] data, int page, RectD region)
    {
        using var engine = PdfEngine.Open(data, InputFileTypes.PDF, null);
        var img = engine.Render(page, 2, region);
        var dark = 0;
        for (var i = 0; i + 2 < img.Pixels.Length; i += 3)
            if (img.Pixels[i] + img.Pixels[i + 1] + img.Pixels[i + 2] < 384) dark++;
        return dark / (double)(img.Width * img.Height);
    }
}
