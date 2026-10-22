using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfDesk.Core;

public enum StampKind { Text, Check, Cross }

/// <summary>Text or a mark placed on a page by "Fill &amp; Sign" (a FreeText annotation PdfDesk created).</summary>
public sealed record TextStamp(string Id, int PageIndex, RectD Rect, StampKind Kind, string Text, double FontSize);

/// <summary>
/// "Fill &amp; Sign": typed text, check marks and crosses placed anywhere on a page, for forms without fields (scans).
/// Each is a standard FreeText annotation with its own appearance, so every viewer shows it and PdfDesk can edit it later.
/// Text uses the built-in Helvetica (Latin-1: accents work, no font embedding); marks use ZapfDingbats.
/// </summary>
public static class TextStamps
{
    private const string IdPrefix = "PdfDesk-";

    public static IReadOnlyList<TextStamp> Read(byte[] data, string? password)
    {
        using var doc = Open(data, password, PdfDocumentOpenMode.Import);
        var list = new List<TextStamp>();
        for (var p = 0; p < doc.PageCount; p++)
        {
            if (Resolve(doc.Pages[p].Elements["/Annots"]) is not PdfArray annots) continue;
            foreach (var a in annots.Elements.Select(Resolve).OfType<PdfDictionary>())
            {
                if (a.Elements.GetName("/Subtype") != "/FreeText") continue;
                if (Resolve(a.Elements["/NM"]) is not PdfString nm || !nm.Value.StartsWith(IdPrefix, StringComparison.Ordinal)) continue;
                if (Resolve(a.Elements["/Rect"]) is not PdfArray r || r.Elements.Count < 4) continue;
                var rect = FormEditor.ToPageSpace(doc.Pages[p], Num(r, 0), Num(r, 1), Num(r, 2), Num(r, 3));
                var kind = nm.Value.Contains("-check-") ? StampKind.Check : nm.Value.Contains("-cross-") ? StampKind.Cross : StampKind.Text;
                var contents = Resolve(a.Elements["/Contents"]) is PdfString c ? c.Value : "";
                var size = ParseFontSize(Resolve(a.Elements["/DA"]) is PdfString da ? da.Value : "");
                list.Add(new TextStamp(nm.Value, p, rect, kind, contents, size));
            }
        }
        return list;
    }

    /// <summary>Adds (or, with an existing id, replaces) a stamp. <paramref name="rect"/> is in page space; text lines are split on '\n'.</summary>
    public static byte[] Put(byte[] data, string? password, int pageIndex, RectD rect, StampKind kind, string text, double fontSize, string? replaceId = null)
    {
        using var doc = Open(data, password, PdfDocumentOpenMode.Modify);
        var page = doc.Pages[pageIndex];
        if (replaceId is not null) RemoveFrom(doc, replaceId);

        var annots = Resolve(page.Elements["/Annots"]) as PdfArray;
        if (annots is null)
        {
            annots = new PdfArray(doc);
            page.Elements["/Annots"] = annots;
        }

        var rot = ((page.Rotate % 360) + 360) % 360;
        var (x1, y1, x2, y2) = FromPageSpace(page, rect);

        // Appearance: drawn upright in the viewer, i.e. counter-rotated against the page's /Rotate.
        var content = kind == StampKind.Text ? TextContent(text, fontSize, rect.Height) : MarkContent(kind, rect.Width, rect.Height);
        var ap = new PdfDictionary(doc);
        ap.Elements["/Type"] = new PdfName("/XObject");
        ap.Elements["/Subtype"] = new PdfName("/Form");
        ap.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(rect.Width), new PdfReal(rect.Height));
        if (rot != 0)
        {
            var (a, b, c, d) = rot switch { 90 => (0, 1, -1, 0), 180 => (-1, 0, 0, -1), _ => (0, -1, 1, 0) };
            ap.Elements["/Matrix"] = new PdfArray(doc, new PdfInteger(a), new PdfInteger(b), new PdfInteger(c), new PdfInteger(d), new PdfInteger(0), new PdfInteger(0));
        }
        var fonts = new PdfDictionary(doc);
        fonts.Elements["/Helv"] = StandardFont(doc, "Helvetica", winAnsi: true);
        fonts.Elements["/ZaDb"] = StandardFont(doc, "ZapfDingbats", winAnsi: false);
        var resources = new PdfDictionary(doc);
        resources.Elements["/Font"] = fonts;
        ap.Elements["/Resources"] = resources;
        ap.CreateStream(content);
        doc.Internals.AddObject(ap);
        var normal = new PdfDictionary(doc);
        normal.Elements["/N"] = ap.Reference!;

        var id = replaceId ?? $"{IdPrefix}{kind.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}";
        if (replaceId is not null && !replaceId.Contains($"-{kind.ToString().ToLowerInvariant()}-")) id = $"{IdPrefix}{kind.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}";
        var annot = new PdfDictionary(doc);
        annot.Elements["/Type"] = new PdfName("/Annot");
        annot.Elements["/Subtype"] = new PdfName("/FreeText");
        annot.Elements["/Rect"] = new PdfArray(doc, new PdfReal(x1), new PdfReal(y1), new PdfReal(x2), new PdfReal(y2));
        annot.Elements["/Contents"] = MakeString(kind switch { StampKind.Check => "✓", StampKind.Cross => "✗", _ => text });
        annot.Elements["/NM"] = new PdfString(id);
        annot.Elements["/F"] = new PdfInteger(4); // print
        annot.Elements["/DA"] = new PdfString($"0 g /Helv {fontSize.ToString("0.##", CultureInfo.InvariantCulture)} Tf");
        annot.Elements["/BS"] = new PdfDictionary(doc) { Elements = { ["/W"] = new PdfInteger(0) } };
        annot.Elements["/M"] = new PdfDate(DateTime.Now);
        annot.Elements["/AP"] = normal;
        annot.Elements["/P"] = page.Reference!;
        doc.Internals.AddObject(annot);
        annots.Elements.Add(annot.Reference!);

        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    public static byte[] Remove(byte[] data, string? password, string id)
    {
        using var doc = Open(data, password, PdfDocumentOpenMode.Modify);
        RemoveFrom(doc, id);
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static void RemoveFrom(PdfDocument doc, string id)
    {
        for (var p = 0; p < doc.PageCount; p++)
        {
            if (Resolve(doc.Pages[p].Elements["/Annots"]) is not PdfArray annots) continue;
            for (var i = annots.Elements.Count - 1; i >= 0; i--)
                if (Resolve(annots.Elements[i]) is PdfDictionary a && Resolve(a.Elements["/NM"]) is PdfString nm && nm.Value == id)
                    annots.Elements.RemoveAt(i);
        }
    }

    /// <summary>Text lines, top-left aligned, black Helvetica.</summary>
    private static byte[] TextContent(string text, double size, double boxHeight)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var leading = size * 1.2;
        sb.Append(CultureInfo.InvariantCulture, $"/Tx BMC q BT 0 g /Helv {size.ToString("0.##", inv)} Tf {leading.ToString("0.##", inv)} TL ");
        sb.Append(CultureInfo.InvariantCulture, $"1 0 0 1 {(size * 0.15).ToString("0.##", inv)} {(boxHeight - size).ToString("0.##", inv)} Tm ");
        var first = true;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (!first) sb.Append("T* ");
            sb.Append('(').Append(Escape(line)).Append(") Tj ");
            first = false;
        }
        sb.Append("ET Q EMC");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>ZapfDingbats ✔ ("4") or ✘ ("8") scaled to the box.</summary>
    private static byte[] MarkContent(StampKind kind, double w, double h)
    {
        var inv = CultureInfo.InvariantCulture;
        var size = Math.Min(w, h) * 1.05;
        var glyph = kind == StampKind.Check ? "4" : "8";
        // ZapfDingbats check is ~0.75em wide; center it in the box.
        var x = (w - size * 0.8) / 2;
        var y = (h - size * 0.7) / 2;
        return Encoding.Latin1.GetBytes($"q BT 0 g /ZaDb {size.ToString("0.##", inv)} Tf 1 0 0 1 {x.ToString("0.##", inv)} {y.ToString("0.##", inv)} Tm ({glyph}) Tj ET Q");
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            var c = ch <= 255 ? ch : WinAnsi(ch);
            if (c is '(' or ')' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();

        // Common typography outside Latin-1 that WinAnsi still has (curly quotes, dashes, euro); anything else becomes '?'.
        static char WinAnsi(char ch) => ch switch
        {
            '€' => (char)0x80, '‘' => (char)0x91, '’' => (char)0x92, '“' => (char)0x93, '”' => (char)0x94,
            '•' => (char)0x95, '–' => (char)0x96, '—' => (char)0x97, '…' => (char)0x85,
            _ => '?',
        };
    }

    private static PdfDictionary StandardFont(PdfDocument doc, string name, bool winAnsi)
    {
        var font = new PdfDictionary(doc);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/" + name);
        if (winAnsi) font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        return font;
    }

    /// <summary>Inverse of <see cref="FormEditor.ToPageSpace"/>: page space (rotated, top-left origin) to PDF user space.</summary>
    internal static (double X1, double Y1, double X2, double Y2) FromPageSpace(PdfPage page, RectD r)
    {
        var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        var bx1 = Math.Min(box.X1, box.X2);
        var by2 = Math.Max(box.Y1, box.Y2);
        var w = Math.Abs(box.X2 - box.X1);
        var h = Math.Abs(box.Y2 - box.Y1);
        var rot = ((page.Rotate % 360) + 360) % 360;

        (double X, double Y) Map(double px, double py)
        {
            var (u, v) = rot switch
            {
                90 => (py, h - px),
                180 => (w - px, h - py),
                270 => (w - py, px),
                _ => (px, py),
            };
            return (bx1 + u, by2 - v);
        }

        var a = Map(r.X, r.Y);
        var b = Map(r.Right, r.Bottom);
        return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    private static double ParseFontSize(string da)
    {
        var parts = da.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.IndexOf(parts, "Tf");
        return i > 0 && double.TryParse(parts[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 12;
    }

    private static double Num(PdfArray a, int i) => Resolve(a.Elements[i]) switch
    {
        PdfReal r => r.Value,
        PdfInteger n => n.Value,
        _ => 0,
    };

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    private static PdfString MakeString(string value) =>
        value.All(c => c < 128) ? new PdfString(value) : new PdfString(value, PdfStringEncoding.Unicode);

    private static PdfDocument Open(byte[] data, string? password, PdfDocumentOpenMode mode)
    {
        try
        {
            var stream = new MemoryStream(data, writable: false);
            return string.IsNullOrEmpty(password) ? PdfReader.Open(stream, mode) : PdfReader.Open(stream, password, mode);
        }
        catch (PdfReaderException ex)
        {
            throw new InvalidOperationException($"This PDF can't be edited: {ex.Message}", ex);
        }
    }
}
