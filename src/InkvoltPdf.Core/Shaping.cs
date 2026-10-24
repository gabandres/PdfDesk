using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PdfSharp.Pdf;

namespace InkvoltPdf.Core;

/// <summary>A glyph placed on a line: origin (offsets applied) in ems from the line's start, the text it stands for, and
/// whether it belongs to a right-to-left run (odd bidi level).</summary>
internal readonly record struct PlacedGlyph(TrueTypeFont Font, int Glyph, double X, double Y, string Text, bool Rtl = false);

/// <summary>A shaped line: glyphs in the order they are written to the content stream (logical order), and the advance width in ems.</summary>
internal sealed class ShapedLine(string text, List<PlacedGlyph> glyphs, double width, bool rtl, bool mixed = false)
{
    /// <summary>Glyphs in logical order with both left-to-right and right-to-left runs (see <see cref="CidFontWriter.Emit"/>).</summary>
    public bool Mixed { get; } = mixed;
    public string Text { get; } = text;
    public List<PlacedGlyph> Glyphs { get; } = glyphs;
    public double Width { get; } = width;
    /// <summary>The paragraph direction is right-to-left (first strong character).</summary>
    public bool Rtl { get; } = rtl;
}

/// <summary>
/// Lays a line of Unicode text out for PDF: bidi levels (<see cref="Bidi"/>), font fallback per run (the primary font,
/// then <see cref="SystemFonts"/>), HarfBuzz shaping per run (Arabic joining, Indic reordering and conjuncts, marks,
/// kerning), and visual reordering of the runs. Each glyph carries the logical text it represents, so the font's
/// /ToUnicode makes copy and search return the typed string.
/// </summary>
internal static class TextShaper
{
    public static double Measure(string text, TrueTypeFont? primary) => Shape(text, primary).Width;

    /// <param name="visualOrder">Write the glyphs left to right (visual order) instead of in logical order. MuPDF turns
    /// right-to-left runs written that way back into logical order itself; it is the sturdier choice for stretched
    /// (Tz) text, where MuPDF would see gaps inside a right-to-left word written last glyph first.</param>
    public static ShapedLine Shape(string text, TrueTypeFont? primary, bool visualOrder = false)
    {
        if (text.Length == 0) return new ShapedLine(text, [], 0, false);
        var (levels, para) = Bidi.Levels(text);

        // Itemize: maximal runs of one font at one level.
        var runs = new List<(int Start, int Length, int Level, TrueTypeFont Font)>();
        TrueTypeFont? cur = null;
        int runStart = 0, runLevel = -1;
        for (var i = 0; i < text.Length;)
        {
            var len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var cp = len == 2 ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            var font = PickFont(cp, cur, primary) ?? throw new InvalidOperationException("No font is available to draw this text.");
            if (cur is null || font != cur || levels[i] != runLevel)
            {
                if (cur is not null && i > runStart) runs.Add((runStart, i - runStart, runLevel, cur));
                runStart = i;
                cur = font;
                runLevel = levels[i];
            }
            i += len;
        }
        runs.Add((runStart, text.Length - runStart, runLevel, cur!));

        // L2 on runs: reverse every maximal sequence at or above each level, from the highest down to the lowest odd one.
        var order = Enumerable.Range(0, runs.Count).ToList();
        var max = runs.Max(r => r.Level);
        var minOdd = runs.Where(r => r.Level % 2 == 1).Select(r => r.Level).DefaultIfEmpty(int.MaxValue).Min();
        for (var level = max; level >= minOdd; level--)
        {
            for (var i = 0; i < order.Count;)
            {
                if (runs[order[i]].Level < level)
                {
                    i++;
                    continue;
                }
                var j = i;
                while (j < order.Count && runs[order[j]].Level >= level) j++;
                order.Reverse(i, j - i);
                i = j;
            }
        }

        // Shape the runs left to right (visual order), but write the glyphs in logical order (right-to-left runs last
        // glyph first, runs as typed), so extraction reads the text as typed with exact glyph boxes. Where a mixed line
        // jumps between runs, CidFontWriter.Emit marks the direction so MuPDF keeps it one line.
        var byRun = new List<PlacedGlyph>[runs.Count];
        double pen = 0;
        foreach (var runIndex in order)
        {
            var r = runs[runIndex];
            var glyphs = byRun[runIndex] = [];
            var rtl = r.Level % 2 == 1;
            var upem = r.Font.UnitsPerEm;
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(text, r.Start, r.Length);
            buffer.Direction = rtl ? HarfBuzzSharp.Direction.RightToLeft : HarfBuzzSharp.Direction.LeftToRight;
            buffer.GuessSegmentProperties();
            r.Font.HbFont.Shape(buffer);
            var infos = buffer.GlyphInfos;
            var positions = buffer.GlyphPositions;

            // Glyphs in visual order with their positions.
            var placed = new List<(int Glyph, double X, double Y, int Cluster)>(infos.Length);
            for (var g = 0; g < infos.Length; g++)
            {
                placed.Add(((int)infos[g].Codepoint, pen + positions[g].XOffset / upem, positions[g].YOffset / upem, (int)infos[g].Cluster));
                pen += positions[g].XAdvance / upem;
            }

            // Logical order (what copy/search should read): right-to-left runs are written last glyph first.
            if (rtl) placed.Reverse();

            // Each cluster's text goes to its glyphs in written order: one character per glyph, the rest on the last one.
            var starts = placed.Select(p => p.Cluster).Distinct().Order().ToList();
            var texts = new Dictionary<int, List<string>>();
            for (var c = 0; c < starts.Count; c++)
            {
                var end = c + 1 < starts.Count ? starts[c + 1] : r.Start + r.Length;
                texts[starts[c]] = [.. Graphemes(text[starts[c]..end])];
            }
            var count = placed.GroupBy(p => p.Cluster).ToDictionary(g => g.Key, g => g.Count());
            var seen = new Dictionary<int, int>();
            foreach (var p in placed)
            {
                var k = seen.TryGetValue(p.Cluster, out var v) ? v : 0;
                seen[p.Cluster] = k + 1;
                var parts = texts[p.Cluster];
                var n = count[p.Cluster];
                var t = k < n - 1 ? (k < parts.Count ? parts[k] : "") : string.Concat(parts.Skip(k));
                glyphs.Add(new PlacedGlyph(r.Font, p.Glyph, p.X, p.Y, t, rtl));
            }
        }
        if (visualOrder)
        {
            foreach (var k in order.Where(k => runs[k].Level % 2 == 1)) byRun[k].Reverse();
            return new ShapedLine(text, [.. order.SelectMany(k => byRun[k])], pen, para == 1);
        }
        return new ShapedLine(text, [.. byRun.SelectMany(g => g)], pen, para == 1, runs.Any(r => r.Level % 2 == 0) && runs.Any(r => r.Level % 2 == 1));
    }

    /// <summary>Code points of <paramref name="s"/> (surrogate pairs kept together).</summary>
    private static IEnumerable<string> Graphemes(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return s.Substring(i, 2);
                i++;
            }
            else yield return s[i].ToString();
        }
    }

    /// <summary>Spaces, marks, digits, punctuation and joiners stay in the current run's font when it has them; letters pick a font.</summary>
    private static TrueTypeFont? PickFont(int cp, TrueTypeFont? current, TrueTypeFont? primary)
    {
        if (current is not null)
        {
            if (Bidi.Classify(cp) == Bidi.Cls.BN || cp is >= 0xFE00 and <= 0xFE0F) return current;
            var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
            var letter = cat is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;
            if (!letter && current.Has(cp)) return current;
        }
        return SystemFonts.For(cp, primary);
    }
}

/// <summary>
/// Writes shaped text with embedded TrueType fonts as Type0 / CIDFontType2 / Identity-H. Codes are CIDs handed out per
/// (glyph, text) pair, mapped to the glyphs of a subset font by /CIDToGIDMap and to the text by /ToUnicode, so the same
/// glyph can stand for different text (a ligature, a reordered matra) and extraction still returns the logical string.
/// Positions come from the shaper and are written as TJ adjustments against the /W widths.
/// </summary>
internal sealed class CidFontWriter(string namePrefix = "/PdU")
{
    private sealed class Used(TrueTypeFont font, string name)
    {
        public TrueTypeFont Font { get; } = font;
        public string Name { get; } = name;
        public Dictionary<(int Glyph, string Text), int> Cids { get; } = [];
        public List<(int Glyph, string Text)> ByCid { get; } = [(0, "")];
    }

    private readonly List<Used> _fonts = [];

    public bool IsEmpty => _fonts.Count == 0;

    private Used For(TrueTypeFont font)
    {
        foreach (var u in _fonts)
            if (u.Font == font) return u;
        var used = new Used(font, $"{namePrefix}{_fonts.Count}");
        _fonts.Add(used);
        return used;
    }

    private static int Width(TrueTypeFont font, int glyph) => (int)Math.Round(font.GlyphAdvance(glyph) * 1000.0 / font.UnitsPerEm);

    private const string Lrm = "‎", Rlm = "‏";

    /// <summary>Width of a code in the font's /W: direction marks take no room.</summary>
    private static int Width(Used u, int cid) => u.ByCid[cid].Text is Lrm or Rlm ? 0 : Width(u.Font, u.ByCid[cid].Glyph);

    private static int Cid(Used u, int glyph, string text)
    {
        if (u.Cids.TryGetValue((glyph, text), out var cid)) return cid;
        cid = u.ByCid.Count;
        if (cid > 0xFFFF) throw new InvalidOperationException("Too much distinct text for one appearance.");
        u.ByCid.Add((glyph, text));
        u.Cids[(glyph, text)] = cid;
        return cid;
    }

    /// <summary>
    /// Appends Tf / Ts / TJ operators drawing <paramref name="line"/> from the current text-matrix origin (inside BT/ET).
    /// <para>
    /// A mixed-direction line written in logical order jumps where its runs meet (e.g. from the left end of a
    /// right-to-left word to the space after its right end). MuPDF accepts a jump only where the text switches direction,
    /// and it takes the direction of a space or punctuation from the character before it, so the space after a
    /// right-to-left word would count as right-to-left and start a new line. Each run therefore begins with an invisible,
    /// zero-width direction mark (LRM / RLM, a code drawn with the space glyph) inside an empty /ActualText: it sets the
    /// direction the run's leading spaces inherit, and extraction leaves it out.
    /// </para>
    /// </summary>
    public void Emit(StringBuilder sb, ShapedLine line, double size)
    {
        // A cluster drawn with more glyphs than it has characters leaves glyphs with no text of their own: the line's
        // /ActualText then gives extraction the exact string.
        var actual = line.Glyphs.Any(g => g.Text.Length == 0);
        if (actual)
        {
            sb.Append("/Span <</ActualText <FEFF");
            foreach (var ch in line.Text) sb.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
            sb.Append(">>> BDC ");
        }
        Used? cur = null;
        double pen = 0, rise = 0;
        var open = false;
        void Close()
        {
            if (open) sb.Append("] TJ ");
            open = false;
        }
        bool? direction = null;
        foreach (var g in line.Glyphs)
        {
            var u = For(g.Font);
            if (u != cur)
            {
                Close();
                sb.Append(CultureInfo.InvariantCulture, $"{u.Name} {PdfObj.F(size)} Tf ");
                cur = u;
            }
            if (line.Mixed && direction != g.Rtl)
            {
                Close();
                var mark = Cid(u, g.Font.GlyphOf(' '), g.Rtl ? Rlm : Lrm);
                sb.Append(CultureInfo.InvariantCulture, $"/Span <</ActualText ()>> BDC <{mark:X4}> Tj EMC ");
                direction = g.Rtl;
            }
            var y = g.Y * size;
            if (Math.Abs(y - rise) > 0.001)
            {
                Close();
                sb.Append(CultureInfo.InvariantCulture, $"{PdfObj.F(y)} Ts ");
                rise = y;
            }
            var cid = Cid(u, g.Glyph, g.Text);
            if (!open) sb.Append('[');
            open = true;
            var target = g.X * 1000;
            var adjust = pen - target;
            if (Math.Abs(adjust) >= 0.001) sb.Append(PdfObj.F(adjust)).Append(' ');
            sb.Append('<').Append(cid.ToString("X4", CultureInfo.InvariantCulture)).Append('>');
            pen = target + Width(g.Font, g.Glyph);
        }
        Close();
        if (rise != 0) sb.Append("0 Ts ");
        if (actual) sb.Append("EMC ");
    }

    /// <summary>Adds the font objects to <paramref name="doc"/> and returns a /Font resource dictionary naming them.</summary>
    public PdfDictionary BuildFonts(PdfDocument doc)
    {
        var fonts = new PdfDictionary(doc);
        foreach (var u in _fonts) fonts.Elements[u.Name] = BuildFont(doc, u).Reference!;
        return fonts;
    }

    private static PdfDictionary BuildFont(PdfDocument doc, Used u)
    {
        var font = u.Font;
        var glyphs = u.ByCid.Select(c => c.Glyph).Append(0).Distinct().ToList();
        var subset = HbSubset.Subset(font, glyphs);
        var (fileBytes, map) = subset ?? (font.Bytes, (Func<int, int>)(g => g));

        var tag = new StringBuilder(6);
        var hash = (uint)glyphs.Aggregate(17, (h, g) => h * 31 + g) ^ (uint)font.Family.GetHashCode(StringComparison.Ordinal);
        for (var i = 0; i < 6; i++)
        {
            tag.Append((char)('A' + hash % 26));
            hash /= 26;
        }
        var family = new string([.. font.Family.Where(char.IsAsciiLetterOrDigit)]);
        var baseFont = $"/{tag}+{(family.Length == 0 ? "Font" : family)}";

        var file = PdfObj.AddStream(doc, fileBytes);
        file.Elements["/Length1"] = new PdfInteger(fileBytes.Length);

        var gidMap = new byte[u.ByCid.Count * 2];
        for (var cid = 0; cid < u.ByCid.Count; cid++)
        {
            var gid = Math.Clamp(map(u.ByCid[cid].Glyph), 0, 0xFFFF);
            gidMap[cid * 2] = (byte)(gid >> 8);
            gidMap[cid * 2 + 1] = (byte)gid;
        }
        var cidToGid = PdfObj.AddStream(doc, gidMap);

        double Em(double units) => Math.Round(units * 1000 / font.UnitsPerEm);
        var descriptor = new PdfDictionary(doc);
        descriptor.Elements["/Type"] = new PdfName("/FontDescriptor");
        descriptor.Elements["/FontName"] = new PdfName(baseFont);
        descriptor.Elements["/Flags"] = new PdfInteger(4);
        descriptor.Elements["/FontBBox"] = PdfObj.Reals(doc, Em(font.BBox[0]), Em(font.BBox[1]), Em(font.BBox[2]), Em(font.BBox[3]));
        descriptor.Elements["/ItalicAngle"] = new PdfInteger(0);
        descriptor.Elements["/Ascent"] = new PdfReal(Math.Round(font.Ascent * 1000));
        descriptor.Elements["/Descent"] = new PdfReal(Math.Round(font.Descent * 1000));
        descriptor.Elements["/CapHeight"] = new PdfReal(Math.Round(font.Ascent * 1000));
        descriptor.Elements["/StemV"] = new PdfInteger(80);
        descriptor.Elements["/FontFile2"] = file.Reference!;
        doc.Internals.AddObject(descriptor);

        var widths = new PdfArray(doc);
        for (var cid = 1; cid < u.ByCid.Count; cid++) widths.Elements.Add(new PdfInteger(Width(u, cid)));
        var cidFont = new PdfDictionary(doc);
        cidFont.Elements["/Type"] = new PdfName("/Font");
        cidFont.Elements["/Subtype"] = new PdfName("/CIDFontType2");
        cidFont.Elements["/BaseFont"] = new PdfName(baseFont);
        var info = new PdfDictionary(doc);
        info.Elements["/Registry"] = new PdfString("Adobe");
        info.Elements["/Ordering"] = new PdfString("Identity");
        info.Elements["/Supplement"] = new PdfInteger(0);
        cidFont.Elements["/CIDSystemInfo"] = info;
        cidFont.Elements["/FontDescriptor"] = descriptor.Reference!;
        cidFont.Elements["/DW"] = new PdfInteger(1000);
        if (widths.Elements.Count > 0) cidFont.Elements["/W"] = new PdfArray(doc, new PdfInteger(1), widths);
        cidFont.Elements["/CIDToGIDMap"] = cidToGid.Reference!;
        doc.Internals.AddObject(cidFont);

        var type0 = new PdfDictionary(doc);
        type0.Elements["/Type"] = new PdfName("/Font");
        type0.Elements["/Subtype"] = new PdfName("/Type0");
        type0.Elements["/BaseFont"] = new PdfName(baseFont);
        type0.Elements["/Encoding"] = new PdfName("/Identity-H");
        type0.Elements["/DescendantFonts"] = new PdfArray(doc, cidFont.Reference!);
        type0.Elements["/ToUnicode"] = PdfObj.AddStream(doc, ToUnicode(u.ByCid)).Reference!;
        doc.Internals.AddObject(type0);
        return type0;
    }

    private static byte[] ToUnicode(List<(int Glyph, string Text)> byCid)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = Enumerable.Range(1, byCid.Count - 1).Where(c => byCid[c].Text.Length > 0).ToList();
        foreach (var chunk in entries.Chunk(100))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{chunk.Length} beginbfchar\n");
            foreach (var cid in chunk)
            {
                sb.Append('<').Append(cid.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                foreach (var ch in byCid[cid].Text) sb.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

/// <summary>Font subsetting through HarfBuzz's hb-subset (exported by the same native library HarfBuzzSharp loads).</summary>
internal static partial class HbSubset
{
    private const string Lib = "libHarfBuzzSharp";
    private const uint NoHinting = 0x1;
    private const int DropTableTags = 3;

    /// <summary>A font with just <paramref name="glyphs"/> (plus components of composite glyphs), and the old → new glyph map; null on failure.</summary>
    public static (byte[] Bytes, Func<int, int> Map)? Subset(TrueTypeFont font, IReadOnlyCollection<int> glyphs)
    {
        try
        {
            var face = font.HbFace.Handle;
            var input = hb_subset_input_create_or_fail();
            if (input == IntPtr.Zero) return null;
            try
            {
                var set = hb_subset_input_glyph_set(input);
                foreach (var g in glyphs) hb_set_add(set, (uint)g);
                var drop = hb_subset_input_set(input, DropTableTags);
                foreach (var t in new[] { "GSUB", "GPOS", "GDEF", "BASE", "JSTF", "MATH", "kern", "morx", "mort", "DSIG", "vhea", "vmtx", "VORG" })
                    hb_set_add(drop, (uint)(t[0] << 24 | t[1] << 16 | t[2] << 8 | t[3]));
                hb_subset_input_set_flags(input, NoHinting);
                var plan = hb_subset_plan_create_or_fail(face, input);
                if (plan == IntPtr.Zero) return null;
                try
                {
                    var mapping = hb_subset_plan_old_to_new_glyph_mapping(plan);
                    var map = new Dictionary<int, int>();
                    foreach (var g in glyphs)
                    {
                        var n = hb_map_get(mapping, (uint)g);
                        if (n != uint.MaxValue) map[g] = (int)n;
                    }
                    var result = hb_subset_plan_execute_or_fail(plan);
                    if (result == IntPtr.Zero) return null;
                    try
                    {
                        var blob = hb_face_reference_blob(result);
                        try
                        {
                            var data = hb_blob_get_data(blob, out var length);
                            if (data == IntPtr.Zero || length == 0) return null;
                            var bytes = new byte[length];
                            Marshal.Copy(data, bytes, 0, (int)length);
                            return (bytes, g => map.TryGetValue(g, out var n) ? n : 0);
                        }
                        finally
                        {
                            hb_blob_destroy(blob);
                        }
                    }
                    finally
                    {
                        hb_face_destroy(result);
                    }
                }
                finally
                {
                    hb_subset_plan_destroy(plan);
                }
            }
            finally
            {
                hb_subset_input_destroy(input);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport(Lib)] private static partial IntPtr hb_subset_input_create_or_fail();
    [LibraryImport(Lib)] private static partial void hb_subset_input_destroy(IntPtr input);
    [LibraryImport(Lib)] private static partial IntPtr hb_subset_input_glyph_set(IntPtr input);
    [LibraryImport(Lib)] private static partial IntPtr hb_subset_input_set(IntPtr input, int setType);
    [LibraryImport(Lib)] private static partial void hb_subset_input_set_flags(IntPtr input, uint flags);
    [LibraryImport(Lib)] private static partial void hb_set_add(IntPtr set, uint value);
    [LibraryImport(Lib)] private static partial IntPtr hb_subset_plan_create_or_fail(IntPtr face, IntPtr input);
    [LibraryImport(Lib)] private static partial void hb_subset_plan_destroy(IntPtr plan);
    [LibraryImport(Lib)] private static partial IntPtr hb_subset_plan_old_to_new_glyph_mapping(IntPtr plan);
    [LibraryImport(Lib)] private static partial IntPtr hb_subset_plan_execute_or_fail(IntPtr plan);
    [LibraryImport(Lib)] private static partial uint hb_map_get(IntPtr map, uint key);
    [LibraryImport(Lib)] private static partial IntPtr hb_face_reference_blob(IntPtr face);
    [LibraryImport(Lib)] private static partial void hb_face_destroy(IntPtr face);
    [LibraryImport(Lib)] private static partial IntPtr hb_blob_get_data(IntPtr blob, out uint length);
    [LibraryImport(Lib)] private static partial void hb_blob_destroy(IntPtr blob);
}
