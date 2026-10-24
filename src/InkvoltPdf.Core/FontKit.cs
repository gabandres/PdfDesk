using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using PdfSharp.Pdf;

namespace InkvoltPdf.Core;

/// <summary>
/// A TrueType font read from disk: just enough of it (cmap, advance widths, ascent/descent) to lay text out and to
/// pick a font that has the glyphs; HarfBuzz shapes with it and <see cref="CidFontWriter"/> embeds a subset.
/// Font collections (.ttc) are split into a standalone font first.
/// </summary>
internal sealed class TrueTypeFont
{
    private readonly Dictionary<int, int> _glyphCache = [];
    private readonly Func<int, int> _lookup;
    private readonly ushort[] _advances;
    private readonly Lock _gate = new();
    private HarfBuzzSharp.Font? _hbFont;
    private HarfBuzzSharp.Face? _hbFace;
    private HarfBuzzSharp.Blob? _hbBlob;

    public string Family { get; }
    public byte[] Bytes { get; }
    public double UnitsPerEm { get; }
    /// <summary>Ascent and descent as fractions of the em (descent negative).</summary>
    public double Ascent { get; }
    public double Descent { get; }
    /// <summary>Glyph bounding box (head table) in font units: xMin, yMin, xMax, yMax.</summary>
    public short[] BBox { get; private init; } = [0, 0, 1000, 1000];

    private TrueTypeFont(string family, byte[] bytes, double upem, double ascent, double descent, ushort[] advances, Func<int, int> lookup)
    {
        Family = family;
        Bytes = bytes;
        UnitsPerEm = upem;
        Ascent = ascent;
        Descent = descent;
        _advances = advances;
        _lookup = lookup;
    }

    public int GlyphOf(int codePoint)
    {
        lock (_gate)
        {
            if (!_glyphCache.TryGetValue(codePoint, out var g))
            {
                g = _lookup(codePoint);
                _glyphCache[codePoint] = g;
            }
            return g;
        }
    }

    public bool Has(int codePoint) => codePoint is '\n' or '\r' or '\t' || GlyphOf(codePoint) != 0;

    /// <summary>Advance width of a code point, in ems.</summary>
    public double Advance(int codePoint)
    {
        var g = GlyphOf(codePoint);
        if (_advances.Length == 0) return 0.5;
        return _advances[Math.Min(g, _advances.Length - 1)] / UnitsPerEm;
    }

    public double Measure(string s)
    {
        double w = 0;
        foreach (var cp in CodePoints(s)) w += Advance(cp);
        return w;
    }

    /// <summary>Advance width of a glyph in font units (hmtx).</summary>
    public int GlyphAdvance(int glyph) => _advances.Length == 0 ? (int)(UnitsPerEm / 2) : _advances[Math.Clamp(glyph, 0, _advances.Length - 1)];

    /// <summary>The HarfBuzz font used to shape text with this font (created once; HarfBuzz fonts are safe to shape with concurrently).</summary>
    internal HarfBuzzSharp.Font HbFont
    {
        get
        {
            lock (_gate)
            {
                if (_hbFont is null)
                {
                    // Process-lifetime objects (fonts are cached for the process): the font data is copied to native
                    // memory that is never freed, and the wrappers are kept referenced so no finalizer releases them.
                    var data = System.Runtime.InteropServices.Marshal.AllocHGlobal(Bytes.Length);
                    System.Runtime.InteropServices.Marshal.Copy(Bytes, 0, data, Bytes.Length);
                    _hbBlob = new HarfBuzzSharp.Blob(data, Bytes.Length, HarfBuzzSharp.MemoryMode.ReadOnly);
                    _hbFace = new HarfBuzzSharp.Face(_hbBlob, 0);
                    _hbFont = new HarfBuzzSharp.Font(_hbFace);
                    _hbFont.SetScale((int)UnitsPerEm, (int)UnitsPerEm);
                }
                return _hbFont;
            }
        }
    }

    internal HarfBuzzSharp.Face HbFace
    {
        get
        {
            _ = HbFont;
            return _hbFace!;
        }
    }

    public static IEnumerable<int> CodePoints(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else yield return s[i];
        }
    }

    /// <summary>Loads a TrueType-outline font (TTF, or the first font of a TTC). Null if it isn't usable.</summary>
    public static TrueTypeFont? Load(string family, byte[] file)
    {
        try
        {
            var bytes = file;
            if (bytes.Length > 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "ttcf")
                bytes = ExtractFromCollection(bytes, 0);
            var tables = Tables(bytes);
            if (!tables.ContainsKey("glyf") || !tables.ContainsKey("loca") || !tables.TryGetValue("cmap", out var cmap)
                || !tables.TryGetValue("head", out var head) || !tables.TryGetValue("hhea", out var hhea) || !tables.TryGetValue("hmtx", out var hmtx))
                return null;
            var span = bytes.AsSpan();
            double upem = BinaryPrimitives.ReadUInt16BigEndian(span[(head.Offset + 18)..]);
            if (upem <= 0) upem = 1000;
            var ascent = BinaryPrimitives.ReadInt16BigEndian(span[(hhea.Offset + 4)..]) / upem;
            var descent = BinaryPrimitives.ReadInt16BigEndian(span[(hhea.Offset + 6)..]) / upem;
            var nHm = BinaryPrimitives.ReadUInt16BigEndian(span[(hhea.Offset + 34)..]);
            var advances = new ushort[Math.Min(nHm, hmtx.Length / 4)];
            for (var i = 0; i < advances.Length; i++) advances[i] = BinaryPrimitives.ReadUInt16BigEndian(span[(hmtx.Offset + i * 4)..]);
            var lookup = CmapLookup(bytes, cmap.Offset);
            if (lookup is null) return null;
            var bbox = new short[4];
            for (var i = 0; i < 4; i++) bbox[i] = BinaryPrimitives.ReadInt16BigEndian(span[(head.Offset + 36 + 2 * i)..]);
            return new TrueTypeFont(family, bytes, upem, ascent, descent, advances, lookup) { BBox = bbox };
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static Dictionary<string, (int Offset, int Length)> Tables(byte[] font, int at = 0)
    {
        var span = font.AsSpan();
        var n = BinaryPrimitives.ReadUInt16BigEndian(span[(at + 4)..]);
        var map = new Dictionary<string, (int, int)>();
        for (var i = 0; i < n; i++)
        {
            var rec = at + 12 + i * 16;
            var tag = Encoding.ASCII.GetString(font, rec, 4);
            map[tag] = ((int)BinaryPrimitives.ReadUInt32BigEndian(span[(rec + 8)..]), (int)BinaryPrimitives.ReadUInt32BigEndian(span[(rec + 12)..]));
        }
        return map;
    }

    /// <summary>Rebuilds font #<paramref name="index"/> of a TrueType collection as a standalone font file.</summary>
    internal static byte[] ExtractFromCollection(byte[] ttc, int index)
    {
        var span = ttc.AsSpan();
        var at = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(12 + 4 * index)..]);
        var n = BinaryPrimitives.ReadUInt16BigEndian(span[(at + 4)..]);
        var headerLen = 12 + 16 * n;
        var records = new List<(byte[] Rec, int Offset, int Length)>();
        for (var i = 0; i < n; i++)
        {
            var rec = at + 12 + i * 16;
            records.Add((ttc[rec..(rec + 16)], (int)BinaryPrimitives.ReadUInt32BigEndian(span[(rec + 8)..]), (int)BinaryPrimitives.ReadUInt32BigEndian(span[(rec + 12)..])));
        }
        var total = headerLen + records.Sum(r => (r.Length + 3) & ~3);
        var output = new byte[total];
        ttc.AsSpan(at, 12).CopyTo(output);
        var pos = headerLen;
        for (var i = 0; i < records.Count; i++)
        {
            var (rec, offset, length) = records[i];
            rec.CopyTo(output, 12 + i * 16);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12 + i * 16 + 8), (uint)pos);
            ttc.AsSpan(offset, length).CopyTo(output.AsSpan(pos));
            pos += (length + 3) & ~3;
        }
        return output;
    }

    /// <summary>A code point → glyph lookup from the best Unicode cmap subtable (format 12, else 4).</summary>
    private static Func<int, int>? CmapLookup(byte[] font, int cmap)
    {
        var span = font.AsSpan();
        var n = BinaryPrimitives.ReadUInt16BigEndian(span[(cmap + 2)..]);
        int? f12 = null, f4 = null;
        for (var i = 0; i < n; i++)
        {
            var rec = cmap + 4 + i * 8;
            var platform = BinaryPrimitives.ReadUInt16BigEndian(span[rec..]);
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(span[(rec + 2)..]);
            var sub = cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(span[(rec + 4)..]);
            var format = BinaryPrimitives.ReadUInt16BigEndian(span[sub..]);
            var unicode = platform == 0 || (platform == 3 && encoding is 1 or 10);
            if (!unicode) continue;
            if (format == 12) f12 ??= sub;
            else if (format == 4) f4 ??= sub;
        }

        if (f12 is { } t12)
        {
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(t12 + 12)..]);
            var groups = new (uint Start, uint End, uint Glyph)[count];
            for (var i = 0; i < count; i++)
            {
                var g = t12 + 16 + i * 12;
                groups[i] = (BinaryPrimitives.ReadUInt32BigEndian(span[g..]), BinaryPrimitives.ReadUInt32BigEndian(span[(g + 4)..]), BinaryPrimitives.ReadUInt32BigEndian(span[(g + 8)..]));
            }
            return cp =>
            {
                int lo = 0, hi = groups.Length - 1;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    if (cp < groups[mid].Start) hi = mid - 1;
                    else if (cp > groups[mid].End) lo = mid + 1;
                    else return (int)(groups[mid].Glyph + (uint)cp - groups[mid].Start);
                }
                return 0;
            };
        }

        if (f4 is { } t4)
        {
            var segX2 = BinaryPrimitives.ReadUInt16BigEndian(span[(t4 + 6)..]);
            var segs = segX2 / 2;
            var ends = t4 + 14;
            var starts = ends + segX2 + 2;
            var deltas = starts + segX2;
            var ranges = deltas + segX2;
            return cp =>
            {
                if (cp > 0xFFFF) return 0;
                var s = font.AsSpan();
                for (var i = 0; i < segs; i++)
                {
                    var end = BinaryPrimitives.ReadUInt16BigEndian(s[(ends + 2 * i)..]);
                    if (cp > end) continue;
                    var start = BinaryPrimitives.ReadUInt16BigEndian(s[(starts + 2 * i)..]);
                    if (cp < start) return 0;
                    var delta = BinaryPrimitives.ReadInt16BigEndian(s[(deltas + 2 * i)..]);
                    var rangeAt = ranges + 2 * i;
                    var range = BinaryPrimitives.ReadUInt16BigEndian(s[rangeAt..]);
                    if (range == 0) return (cp + delta) & 0xFFFF;
                    var addr = rangeAt + range + 2 * (cp - start);
                    if (addr + 2 > s.Length) return 0;
                    var g = BinaryPrimitives.ReadUInt16BigEndian(s[addr..]);
                    return g == 0 ? 0 : (g + delta) & 0xFFFF;
                }
                return 0;
            };
        }
        return null;
    }
}

/// <summary>The three standard PDF text font families (non-embedded, WinAnsi).</summary>
internal enum StdFamily { Helvetica, Times, Courier }

/// <summary>A standard-14 text font: its PDF name, and the metric-compatible system font used to measure and to embed.</summary>
internal sealed record StdFont(StdFamily Family, bool Bold, bool Italic)
{
    public static readonly StdFont Helvetica = new(StdFamily.Helvetica, false, false);

    public string BaseFont => Family switch
    {
        StdFamily.Times => (Bold, Italic) switch { (true, true) => "Times-BoldItalic", (true, false) => "Times-Bold", (false, true) => "Times-Italic", _ => "Times-Roman" },
        StdFamily.Courier => "Courier" + Suffix("Oblique"),
        _ => "Helvetica" + Suffix("Oblique"),
    };

    private string Suffix(string italic) => (Bold, Italic) switch { (true, true) => "-Bold" + italic, (true, false) => "-Bold", (false, true) => "-" + italic, _ => "" };

    /// <summary>The system TrueType font with the same metrics (Arial ≈ Helvetica, Times New Roman ≈ Times, Courier New ≈ Courier).</summary>
    public TrueTypeFont? Metrics => SystemFonts.Get(Family switch
    {
        StdFamily.Times => (Bold, Italic) switch { (true, true) => "timesbi.ttf", (true, false) => "timesbd.ttf", (false, true) => "timesi.ttf", _ => "times.ttf" },
        StdFamily.Courier => (Bold, Italic) switch { (true, true) => "courbi.ttf", (true, false) => "courbd.ttf", (false, true) => "couri.ttf", _ => "cour.ttf" },
        _ => (Bold, Italic) switch { (true, true) => "arialbi.ttf", (true, false) => "arialbd.ttf", (false, true) => "ariali.ttf", _ => "arial.ttf" },
    });

    /// <summary>Maps any font name (a /DA resource name or a /BaseFont) to the closest standard family.</summary>
    public static StdFont FromName(string? name)
    {
        var n = (name ?? "").TrimStart('/');
        var plus = n.IndexOf('+');
        if (plus == 6) n = n[7..]; // subset prefix
        var bold = n.Contains("Bold", StringComparison.OrdinalIgnoreCase) || n.EndsWith("Bd", StringComparison.Ordinal) || n.Contains("Black", StringComparison.OrdinalIgnoreCase);
        var italic = n.Contains("Italic", StringComparison.OrdinalIgnoreCase) || n.Contains("Oblique", StringComparison.OrdinalIgnoreCase) || n.EndsWith("It", StringComparison.Ordinal);
        var family = n.Contains("Cour", StringComparison.OrdinalIgnoreCase) || n.Contains("Mono", StringComparison.OrdinalIgnoreCase) ? StdFamily.Courier
            : n.Contains("Times", StringComparison.OrdinalIgnoreCase) || n.StartsWith("TiRo", StringComparison.Ordinal) || n.Contains("Serif", StringComparison.OrdinalIgnoreCase) && !n.Contains("Sans", StringComparison.OrdinalIgnoreCase) || n.Contains("Georgia", StringComparison.OrdinalIgnoreCase) || n.Contains("Garamond", StringComparison.OrdinalIgnoreCase) ? StdFamily.Times
            : StdFamily.Helvetica;
        if (n is "TiBo" or "HeBo" or "CoBo") bold = true;
        return new StdFont(family, bold, italic);
    }

    /// <summary>Advance in ems of a WinAnsi-encodable character.</summary>
    public double Advance(char c) => Family == StdFamily.Courier ? 0.6 : Metrics?.Advance(c) ?? 0.5;

    public double Measure(string s)
    {
        double w = 0;
        foreach (var c in s) w += Advance(c);
        return w;
    }

    public double Ascent => Metrics?.Ascent ?? 0.9;
    public double Descent => Metrics?.Descent ?? -0.21;
}

/// <summary>System fonts (Windows font folders), loaded lazily and cached for the process.</summary>
internal static class SystemFonts
{
    private static readonly ConcurrentDictionary<string, TrueTypeFont?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fallback chain for scripts the primary font lacks: broad coverage first, big CJK fonts last.</summary>
    private static readonly string[] FallbackFiles =
    [
        "arial.ttf", "segoeui.ttf", "seguisym.ttf", "Nirmala.ttc", "nirmala.ttf", "LeelawUI.ttf", "tahoma.ttf", "sylfaen.ttf",
        "ebrima.ttf", "gadugi.ttf", "seguihis.ttf", "himalaya.ttf", "monbaiti.ttf", "mmrtext.ttf", "javatext.ttf",
        "malgun.ttf", "msyh.ttc", "YuGothR.ttc", "msgothic.ttc", "msjh.ttc", "simsun.ttc", "SimsunExtG.ttf", "seguiemj.ttf",
    ];

    private static IEnumerable<string> FontDirs()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrEmpty(win)) yield return win;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "Microsoft", "Windows", "Fonts");
    }

    public static TrueTypeFont? Get(string file) => Cache.GetOrAdd(file, f =>
    {
        foreach (var dir in FontDirs())
        {
            var path = Path.Combine(dir, f);
            if (!File.Exists(path)) continue;
            try
            {
                return TrueTypeFont.Load(Path.GetFileNameWithoutExtension(f), File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    });

    /// <summary>The first font (primary, then the fallback chain) with a glyph for <paramref name="codePoint"/>, else the primary.</summary>
    public static TrueTypeFont? For(int codePoint, TrueTypeFont? primary)
    {
        if (primary is not null && primary.Has(codePoint)) return primary;
        foreach (var f in FallbackFiles)
            if (Get(f) is { } font && font.Has(codePoint)) return font;
        return primary ?? Get("arial.ttf") ?? Get("segoeui.ttf");
    }
}


/// <summary>WinAnsiEncoding (the encoding the standard fonts use here).</summary>
internal static class WinAnsi
{
    private static readonly Dictionary<char, byte> Specials = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88,
        ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93,
        ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97, ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B,
        ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    public static bool TryEncode(char c, out byte b)
    {
        if (c is >= ' ' and <= '~' || c is >= ' ' and <= 'ÿ')
        {
            b = (byte)c;
            return true;
        }
        return Specials.TryGetValue(c, out b);
    }

    public static bool CanEncode(string s) => s.All(c => c is '\n' or '\r' or '\t' || TryEncode(c, out _));

    /// <summary>A PDF literal string body (escaped), WinAnsi bytes as Latin-1 chars; unencodable chars become '?'.</summary>
    public static string Literal(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            var c = TryEncode(ch, out var b) ? (char)b : ch == '\t' ? ' ' : '?';
            if (c is '(' or ')' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
