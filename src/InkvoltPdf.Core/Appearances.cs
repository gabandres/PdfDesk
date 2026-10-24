using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace InkvoltPdf.Core;

/// <summary>A device color from a PDF operand list: 1 = gray, 3 = RGB, 4 = CMYK.</summary>
internal readonly record struct PdfColorValue(double[] C)
{
    public static readonly PdfColorValue Black = new([0]);

    public static PdfColorValue? From(PdfArray? a)
    {
        if (a is null || a.Elements.Count is not (1 or 3 or 4)) return null;
        return new PdfColorValue([.. Enumerable.Range(0, a.Elements.Count).Select(i => PdfObj.Num(a, i))]);
    }

    public string Fill => string.Join(' ', C.Select(PdfObj.F)) + (C.Length switch { 1 => " g", 3 => " rg", _ => " k" });
    public string Stroke => string.Join(' ', C.Select(PdfObj.F)) + (C.Length switch { 1 => " G", 3 => " RG", _ => " K" });
}

/// <summary>A parsed default-appearance string (/DA): font resource name, size (0 = auto) and text color.</summary>
internal sealed record DefaultAppearance(string FontName, double Size, PdfColorValue Color)
{
    public static DefaultAppearance Parse(string? da)
    {
        var tokens = (da ?? "").Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        string font = "/Helv";
        double size = 0;
        var color = PdfColorValue.Black;
        for (var i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i])
            {
                case "Tf" when i >= 2:
                    if (tokens[i - 2].StartsWith('/')) font = tokens[i - 2];
                    // "NaN"/"Infinity" parse too, and would end up as such in the content stream.
                    size = double.TryParse(tokens[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s) ? Math.Min(s, 1000) : 0;
                    break;
                case "g" or "rg" or "k":
                {
                    var n = tokens[i] switch { "g" => 1, "rg" => 3, _ => 4 };
                    if (i < n) break;
                    var c = new double[n];
                    var ok = true;
                    for (var k = 0; k < n; k++)
                        ok &= double.TryParse(tokens[i - n + k], NumberStyles.Float, CultureInfo.InvariantCulture, out c[k]) && double.IsFinite(c[k]);
                    if (ok) color = new PdfColorValue(c);
                    break;
                }
            }
        }
        return new DefaultAppearance(font, size, color);
    }
}

/// <summary>
/// Lays out and draws text for an appearance stream. Text the standard font can encode (WinAnsi) is written with
/// that non-embedded font; anything else (Greek, Cyrillic, Arabic, Hebrew, Indic, CJK, ...) is shaped with HarfBuzz,
/// ordered by the bidi algorithm and drawn with embedded, subset system TrueType fonts chosen per run
/// (see <see cref="TextShaper"/> and <see cref="CidFontWriter"/>), so any script renders in every viewer.
/// </summary>
internal sealed class TextPainter
{
    private readonly StdFont _std;
    private readonly TrueTypeFont? _primary;
    private readonly List<(string Text, double X, double Y, double Size)> _items = [];

    public bool Unicode { get; }

    public TextPainter(StdFont std, IEnumerable<string> texts)
    {
        _std = std;
        if (!texts.All(WinAnsi.CanEncode))
        {
            _primary = std.Metrics ?? SystemFonts.Get("arial.ttf") ?? SystemFonts.Get("segoeui.ttf");
            Unicode = _primary is not null;
        }
    }

    public double Ascent => _std.Ascent;
    public double Descent => _std.Descent;

    /// <summary>Width of <paramref name="s"/> at 1 pt.</summary>
    public double Measure(string s) =>
        Unicode ? TextShaper.Measure(s, _primary) : _std.Measure(s);

    /// <summary>Queues a line of text with its baseline-left at (x, y) in the appearance's bbox coordinates.</summary>
    public void Draw(string text, double x, double y, double size)
    {
        if (text.Length > 0) _items.Add((text, x, y, size));
    }

    /// <summary>The text-drawing operators and the resources they need (font under <paramref name="resourceName"/>).</summary>
    public (byte[] Ops, PdfDictionary Resources) Build(PdfDocument doc, double width, double height, string resourceName, PdfColorValue color)
    {
        var ops = Ops(resourceName, color);
        return (ops, Resources(doc, resourceName));
    }

    private CidFontWriter? _writer;

    /// <summary>
    /// The operators drawing the text queued since the last call, which is then forgotten. Text on many pages can share
    /// one painter this way: each page's operators, then one <see cref="Resources"/> after the last, so the document
    /// embeds each font once, subset to every page's glyphs.
    /// </summary>
    public byte[] Ops(string resourceName, PdfColorValue color)
    {
        var sb = new StringBuilder();
        if (!Unicode)
            foreach (var (text, x, y, size) in _items)
                sb.Append(CultureInfo.InvariantCulture, $"BT {color.Fill} {resourceName} {PdfObj.F(size)} Tf 1 0 0 1 {PdfObj.F(x)} {PdfObj.F(y)} Tm ({WinAnsi.Literal(text)}) Tj ET\n");
        else
        {
            _writer ??= new CidFontWriter();
            foreach (var (text, x, y, size) in _items)
            {
                sb.Append(CultureInfo.InvariantCulture, $"BT {color.Fill} 1 0 0 1 {PdfObj.F(x)} {PdfObj.F(y)} Tm ");
                _writer.Emit(sb, TextShaper.Shape(text, _primary), size);
                sb.Append("ET\n");
            }
        }
        _items.Clear();
        return Unicode ? Encoding.ASCII.GetBytes(sb.ToString()) : Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The resources every <see cref="Ops"/> so far needs (call once, after the last).</summary>
    public PdfDictionary Resources(PdfDocument doc, string resourceName)
    {
        var fonts = new PdfDictionary(doc);
        if (!Unicode) fonts.Elements[resourceName] = FontResources.Standard(doc, _std.BaseFont);
        else if (_writer is not null) fonts = _writer.BuildFonts(doc);
        var res = new PdfDictionary(doc);
        res.Elements["/Font"] = fonts;
        return res;
    }

    /// <summary>Greedy word wrap to <paramref name="maxWidth"/> (honors explicit line breaks; splits words that don't fit).</summary>
    public List<string> Wrap(string text, double maxWidth, double size)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = "";
            foreach (var word in para.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (Measure(candidate) * size <= maxWidth || candidate.Length == 0)
                {
                    line = candidate;
                    continue;
                }
                if (line.Length > 0) lines.Add(line);
                line = word;
                // A single word wider than the box: break it by characters, in one pass (a long unbroken value must not
                // cost quadratic time), at least one character per line, never inside a surrogate pair.
                if (Measure(line) * size > maxWidth)
                {
                    var start = 0;
                    var width = 0.0;
                    for (var i = 0; i < word.Length;)
                    {
                        var step = char.IsHighSurrogate(word[i]) && i + 1 < word.Length && char.IsLowSurrogate(word[i + 1]) ? 2 : 1;
                        var cw = Measure(word.Substring(i, step)) * size;
                        if (i > start && width + cw > maxWidth)
                        {
                            lines.Add(word[start..i]);
                            start = i;
                            width = 0;
                        }
                        width += cw;
                        i += step;
                    }
                    line = word[start..];
                }
            }
            lines.Add(line);
        }
        return lines;
    }
}

/// <summary>Standard-14 font dictionaries, shared per document (re-using the form's /DR fonts or earlier stamps' when they match).</summary>
internal static class FontResources
{
    private static readonly ConditionalWeakTable<PdfDocument, Dictionary<string, PdfItem>> Cache = [];

    public static PdfItem Standard(PdfDocument doc, string baseFont)
    {
        var map = Cache.GetOrCreateValue(doc);
        if (map.TryGetValue(baseFont, out var hit)) return hit;
        var winAnsi = baseFont is not ("ZapfDingbats" or "Symbol");

        bool Matches(PdfItem? item) =>
            item is PdfReference && PdfObj.Dict(item) is { } d && PdfObj.NameOf(d, "/Subtype") == "/Type1"
            && PdfObj.NameOf(d, "/BaseFont") == "/" + baseFont && !d.Elements.ContainsKey("/FontDescriptor")
            && (winAnsi ? PdfObj.NameOf(d, "/Encoding") == "/WinAnsiEncoding" : !d.Elements.ContainsKey("/Encoding"));

        IEnumerable<PdfItem?> Candidates()
        {
            if (PdfObj.Dict(doc.Internals.Catalog.Elements["/AcroForm"]) is { } acro && PdfObj.Dict(PdfObj.Dict(acro.Elements["/DR"])?.Elements["/Font"]) is { } dr)
                foreach (var v in dr.Elements.Values) yield return v;
            for (var p = 0; p < doc.PageCount; p++)
            {
                if (PdfObj.Array(doc.Pages[p].Elements["/Annots"]) is not { } annots) continue;
                foreach (var a in annots.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>())
                {
                    if (PdfObj.Str(a.Elements["/NM"]) is not { } nm || !TextStamps.IsOwnId(nm) && !Annotations.IsOwnId(nm)) continue;
                    var n = PdfObj.Dict(PdfObj.Dict(a.Elements["/AP"])?.Elements["/N"]);
                    if (PdfObj.Dict(PdfObj.Dict(n?.Elements["/Resources"])?.Elements["/Font"]) is { } f)
                        foreach (var v in f.Elements.Values) yield return v;
                }
            }
        }

        var found = Candidates().FirstOrDefault(Matches);
        if (found is null)
        {
            var font = new PdfDictionary(doc);
            font.Elements["/Type"] = new PdfName("/Font");
            font.Elements["/Subtype"] = new PdfName("/Type1");
            font.Elements["/BaseFont"] = new PdfName("/" + baseFont);
            if (winAnsi) font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
            doc.Internals.AddObject(font);
            found = font.Reference!;
        }
        map[baseFont] = found;
        return found;
    }
}

/// <summary>What a text or choice widget should show.</summary>
internal sealed class FieldLook
{
    public string Da { get; init; } = "/Helv 0 Tf 0 g";
    public string? DrBaseFont { get; init; }
    public int Quadding { get; init; }
    public bool Multiline { get; init; }
    public bool Comb { get; init; }
    public bool Password { get; init; }
    public int MaxLen { get; init; }
    public string Text { get; init; } = "";
    public bool ListBox { get; init; }
    public IReadOnlyList<string> Items { get; init; } = [];
    public IReadOnlySet<int> Selected { get; init; } = new HashSet<int>();
    public int TopIndex { get; init; }
    public PdfColorValue? ColorOverride { get; init; }
}

/// <summary>Builds normal appearance streams for text and choice widgets (what viewers ignoring NeedAppearances show).</summary>
internal static class FieldAppearance
{
    /// <summary>Beyond this a comb field (/MaxLen from the file) is drawn as plain text: a hostile /MaxLen of a billion
    /// would otherwise draw a billion cell dividers.</summary>
    private const int MaxCombCells = 1000;

    public static PdfDictionary? Build(PdfDocument doc, PdfDictionary widget, FieldLook look)
    {
        if (PdfObj.Array(widget.Elements["/Rect"]) is not { Elements.Count: >= 4 } r) return null;
        var rw = Math.Abs(PdfObj.Num(r, 2) - PdfObj.Num(r, 0));
        var rh = Math.Abs(PdfObj.Num(r, 3) - PdfObj.Num(r, 1));
        if (rw <= 0 || rh <= 0) return null;

        var mk = PdfObj.Dict(widget.Elements["/MK"]);
        var rot = (((PdfObj.Int(mk?.Elements["/R"]) ?? 0) % 360) + 360) % 360;
        var bg = PdfColorValue.From(PdfObj.Array(mk?.Elements["/BG"]));
        var bc = PdfColorValue.From(PdfObj.Array(mk?.Elements["/BC"]));
        var bs = PdfObj.Dict(widget.Elements["/BS"]);
        var bw = bs?.Elements.ContainsKey("/W") == true ? PdfObj.Num(bs.Elements["/W"], 1)
            : PdfObj.Array(widget.Elements["/Border"]) is { Elements.Count: >= 3 } border ? PdfObj.Num(border, 2) : 1;
        var style = PdfObj.Name(bs?.Elements["/S"]) ?? "S";
        var drawBorder = bc is not null && bw > 0;
        var (w, h) = rot is 90 or 270 ? (rh, rw) : (rw, rh);

        var ops = new StringBuilder();
        if (bg is { } back) ops.Append(CultureInfo.InvariantCulture, $"q {back.Fill} 0 0 {PdfObj.F(w)} {PdfObj.F(h)} re f Q\n");
        if (drawBorder)
        {
            var half = bw / 2;
            ops.Append(CultureInfo.InvariantCulture, $"q {bc!.Value.Stroke} {PdfObj.F(bw)} w ");
            if (style == "D") ops.Append("[3] 0 d ");
            if (style == "U") ops.Append(CultureInfo.InvariantCulture, $"0 {PdfObj.F(half)} m {PdfObj.F(w)} {PdfObj.F(half)} l S");
            else ops.Append(CultureInfo.InvariantCulture, $"{PdfObj.F(half)} {PdfObj.F(half)} {PdfObj.F(w - bw)} {PdfObj.F(h - bw)} re S");
            if (look.Comb && look.MaxLen is > 1 and <= MaxCombCells && !look.Multiline)
                for (var i = 1; i < look.MaxLen; i++)
                {
                    var x = w * i / look.MaxLen;
                    ops.Append(CultureInfo.InvariantCulture, $" {PdfObj.F(x)} 0 m {PdfObj.F(x)} {PdfObj.F(h)} l S");
                }
            ops.Append(" Q\n");
        }

        var inset = drawBorder ? bw * (style is "B" or "I" ? 2 : 1) : 0;
        var pad = inset + 2;
        ops.Append("/Tx BMC\nq\n");
        ops.Append(CultureInfo.InvariantCulture, $"{PdfObj.F(inset)} {PdfObj.F(inset)} {PdfObj.F(Math.Max(0, w - 2 * inset))} {PdfObj.F(Math.Max(0, h - 2 * inset))} re W n\n");

        var da = DefaultAppearance.Parse(look.Da);
        var std = StdFont.FromName(look.DrBaseFont ?? da.FontName);
        var color = look.ColorOverride ?? da.Color;
        var auto = da.Size <= 0;
        var size = da.Size;

        TextPainter painter;
        if (look.ListBox)
        {
            painter = new TextPainter(std, look.Items);
            if (auto) size = 12;
            var lh = size * 1.15;
            var y = h - pad;
            for (var i = Math.Max(0, look.TopIndex); i < look.Items.Count && y > 0; i++)
            {
                if (look.Selected.Contains(i))
                    ops.Append(CultureInfo.InvariantCulture, $"0.6 0.75 0.85 rg {PdfObj.F(inset)} {PdfObj.F(y - lh)} {PdfObj.F(w - 2 * inset)} {PdfObj.F(lh)} re f\n");
                var lw = painter.Measure(look.Items[i]) * size;
                painter.Draw(look.Items[i], AlignX(look.Quadding, lw, w, pad), y - lh + (lh - size * (painter.Ascent - painter.Descent)) / 2 - painter.Descent * size, size);
                y -= lh;
            }
        }
        else
        {
            var text = look.Password ? new string('*', TrueTypeFont.CodePoints(look.Text).Count()) : look.Text;
            painter = new TextPainter(std, [text]);
            var lineHeightEm = painter.Ascent - painter.Descent;
            if (look.Multiline)
            {
                var avail = Math.Max(1, w - 2 * pad);
                List<string> lines;
                if (auto)
                {
                    size = 12;
                    while (true)
                    {
                        lines = painter.Wrap(text, avail, size);
                        if (lines.Count * size * 1.15 <= h - 2 * pad || size <= 4) break;
                        size -= 0.5;
                    }
                }
                else lines = painter.Wrap(text, avail, size);
                var y = h - pad - painter.Ascent * size;
                foreach (var line in lines)
                {
                    painter.Draw(line, AlignX(look.Quadding, painter.Measure(line) * size, w, pad), y, size);
                    y -= size * 1.15;
                }
            }
            else
            {
                text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                if (look.Comb && look.MaxLen is > 0 and <= MaxCombCells)
                {
                    var cell = w / look.MaxLen;
                    var chars = TrueTypeFont.CodePoints(text).Take(look.MaxLen).Select(char.ConvertFromUtf32).ToList();
                    if (auto)
                    {
                        size = Math.Max(1, (h - 2 * pad) / lineHeightEm);
                        var widest = chars.Count == 0 ? 0 : chars.Max(painter.Measure);
                        if (widest > 0) size = Math.Min(size, cell * 0.9 / widest);
                    }
                    var y = (h - size * lineHeightEm) / 2 - painter.Descent * size;
                    for (var i = 0; i < chars.Count; i++)
                        painter.Draw(chars[i], i * cell + (cell - painter.Measure(chars[i]) * size) / 2, y, size);
                }
                else
                {
                    var tw = painter.Measure(text);
                    if (auto)
                    {
                        size = Math.Max(1, (h - 2 * pad) / lineHeightEm);
                        if (tw * size > w - 2 * pad && tw > 0) size = Math.Max(4, (w - 2 * pad) / tw);
                    }
                    var y = (h - size * lineHeightEm) / 2 - painter.Descent * size;
                    painter.Draw(text, AlignX(look.Quadding, tw * size, w, pad), y, size);
                }
            }
        }

        var (textOps, resources) = painter.Build(doc, w, h, da.FontName, color);
        using var content = new MemoryStream();
        content.Write(Encoding.Latin1.GetBytes(ops.ToString()));
        content.Write(textOps);
        content.Write("Q\nEMC\n"u8);

        PdfArray? matrix = rot switch
        {
            90 => new PdfArray(doc, new PdfInteger(0), new PdfInteger(1), new PdfInteger(-1), new PdfInteger(0), new PdfReal(rh), new PdfInteger(0)),
            180 => new PdfArray(doc, new PdfInteger(-1), new PdfInteger(0), new PdfInteger(0), new PdfInteger(-1), new PdfReal(rw), new PdfReal(rh)),
            270 => new PdfArray(doc, new PdfInteger(0), new PdfInteger(-1), new PdfInteger(1), new PdfInteger(0), new PdfInteger(0), new PdfReal(rw)),
            _ => null,
        };
        return PdfObj.FormXObject(doc, content.ToArray(), 0, 0, w, h, resources, matrix);
    }

    private static double AlignX(int q, double lineWidth, double w, double pad) => q switch
    {
        1 => (w - lineWidth) / 2,
        2 => w - pad - lineWidth,
        _ => pad,
    };
}
