using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>A recognized word and its box in page space (points, top-left origin of the displayed, rotated and cropped page).</summary>
public sealed record OcrWord(string Text, RectD Rect);

/// <summary>
/// Invisible text over scanned pages (an OCR text layer): the page looks the same, but its words can be selected,
/// copied and searched. Each word is written with text render mode 3 (no paint), stretched horizontally to its box, in
/// a content stream of its own placed before the page's content (so the existing content's graphics state, balanced or
/// not, can't leak into it), with an embedded subset Unicode font whose /ToUnicode returns the words as recognized.
/// </summary>
public static class TextLayer
{
    /// <param name="wordsByPage">Words per page index, in reading order.</param>
    public static byte[] AddInvisibleText(byte[] data, string? password, IReadOnlyDictionary<int, IReadOnlyList<OcrWord>> wordsByPage)
    {
        static bool Usable(OcrWord w) => !string.IsNullOrWhiteSpace(w.Text) && !w.Rect.IsEmpty && double.IsFinite(w.Rect.Width) && double.IsFinite(w.Rect.Height);
        var pages = wordsByPage.Where(kv => kv.Value.Any(Usable)).OrderBy(kv => kv.Key).ToList();
        if (pages.Count == 0) return data;

        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        var primary = SystemFonts.Get("arial.ttf") ?? SystemFonts.Get("segoeui.ttf")
            ?? throw new InvalidOperationException("No font is available for the text layer.");
        foreach (var (index, all) in pages)
        {
            if (index < 0 || index >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(wordsByPage), $"There is no page {index + 1}.");
            var page = doc.Pages[index];
            var fonts = FontDictionary(doc, page);
            var prefix = "/PdOcr";
            for (var n = 1; fonts.Elements.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)); n++) prefix = $"/PdOcr{n}_";
            var writer = new CidFontWriter(prefix);

            var ops = new StringBuilder("q BT 3 Tr 0 Tc 0 Tw 0 Ts\n");
            var words = all.Where(Usable).ToList();
            var space = TextShaper.Shape(" ", primary);
            for (var i = 0; i < words.Count; i++)
            {
                var w = words[i];
                var line = TextShaper.Shape(w.Text.Trim(), primary, visualOrder: true);
                if (line.Glyphs.Count == 0 || line.Width <= 0) continue;
                var font = line.Glyphs[0].Font;
                var size = w.Rect.Height / Math.Max(0.1, font.Ascent - font.Descent);
                var baseline = w.Rect.Bottom + font.Descent * size;
                Place(ops, page, w.Rect.X, baseline, w.Rect.Width / (line.Width * size));
                writer.Emit(ops, line, size);
                ops.Append('\n');

                // An explicit space, stretched over the gap, to the next word on the same line (not across columns).
                if (i + 1 < words.Count && space.Width > 0 && words[i + 1].Rect is var next)
                {
                    var overlap = Math.Min(w.Rect.Bottom, next.Bottom) - Math.Max(w.Rect.Y, next.Y);
                    var gap = next.X - w.Rect.Right;
                    if (overlap > 0.5 * Math.Min(w.Rect.Height, next.Height) && gap > 0.01 && gap < 2 * Math.Max(w.Rect.Height, next.Height))
                    {
                        Place(ops, page, w.Rect.Right, baseline, gap / (space.Width * size));
                        writer.Emit(ops, space, size);
                        ops.Append('\n');
                    }
                }
            }
            ops.Append("ET Q\n");
            if (writer.IsEmpty) continue;

            foreach (var (name, font) in writer.BuildFonts(doc).Elements) fonts.Elements[name] = font;
            // The layer goes first, in the page's initial graphics state: whatever the existing content does to the state
            // (a scaling cm left in an unbalanced q, or a stray Q that pops more than it pushed) can't reach it, and as the
            // text is invisible, painting order doesn't matter. The existing content is wrapped in q…Q so what follows it
            // (another tool's additions) starts clean too.
            var contents = new PdfArray(doc);
            contents.Elements.Add(PdfObj.AddStream(doc, Encoding.ASCII.GetBytes(ops.ToString())).Reference!);
            contents.Elements.Add(PdfObj.AddStream(doc, "q\n"u8.ToArray(), compress: false).Reference!);
            switch (page.Elements["/Contents"])
            {
                case PdfReference { Value: PdfArray existing }:
                    foreach (var e in existing.Elements) contents.Elements.Add(e);
                    break;
                case PdfArray existing:
                    foreach (var e in existing.Elements) contents.Elements.Add(e);
                    break;
                case PdfReference r:
                    contents.Elements.Add(r);
                    break;
            }
            contents.Elements.Add(PdfObj.AddStream(doc, "\nQ\n"u8.ToArray(), compress: false).Reference!);
            page.Elements["/Contents"] = contents;
        }
        return PdfObj.Save(doc);
    }

    /// <summary>Whether MuPDF finds any text on the page (a page that already has text needs no OCR).</summary>
    public static bool HasText(PdfEngine engine, int page) => engine.GetText(page).Text.Any(c => !char.IsWhiteSpace(c));

    /// <summary>
    /// Sets the text matrix so text runs left to right along the displayed page from (<paramref name="x"/>,
    /// <paramref name="baseline"/>) in page space, whatever the page's /Rotate, stretched by <paramref name="scale"/>.
    /// </summary>
    private static void Place(StringBuilder ops, PdfPage page, double x, double baseline, double scale)
    {
        var o = PdfObj.ToPdf(page, x, baseline);
        var u = PdfObj.ToPdf(page, x + 1, baseline);
        var v = PdfObj.ToPdf(page, x, baseline - 1);
        var tz = double.IsFinite(scale) && scale > 0 ? scale * 100 : 100;
        ops.Append(CultureInfo.InvariantCulture,
            $"{PdfObj.F(tz)} Tz {PdfObj.F(u.X - o.X)} {PdfObj.F(u.Y - o.Y)} {PdfObj.F(v.X - o.X)} {PdfObj.F(v.Y - o.Y)} {PdfObj.F(o.X)} {PdfObj.F(o.Y)} Tm ");
    }

    /// <summary>The page's /Font resource dictionary (created when missing).</summary>
    private static PdfDictionary FontDictionary(PdfDocument doc, PdfPage page)
    {
        var resources = PdfObj.Dict(page.Elements["/Resources"]);
        if (resources is null)
        {
            resources = new PdfDictionary(doc);
            page.Elements["/Resources"] = resources;
        }
        var fonts = PdfObj.Dict(resources.Elements["/Font"]);
        if (fonts is null)
        {
            fonts = new PdfDictionary(doc);
            resources.Elements["/Font"] = fonts;
        }
        return fonts;
    }
}
