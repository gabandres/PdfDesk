using System.Security.Cryptography;
using System.Text;
using MuPDFCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Filters;
using PdfSharp.Pdf.IO;

namespace PdfDesk.Core;

/// <summary>What <see cref="Optimizer.Optimize"/> does besides rewriting the file.</summary>
public sealed record OptimizeOptions
{
    /// <summary>Flate-compress streams stored uncompressed (or only ASCII/LZW-encoded).</summary>
    public bool CompressStreams { get; init; } = true;

    /// <summary>Keep one copy of byte-identical streams (fonts, images, ...) and point every use at it.</summary>
    public bool DeduplicateStreams { get; init; } = true;

    /// <summary>
    /// Remove XMP metadata packets (the document's is replaced by a minimal one made from the title, author and so on,
    /// which stay).
    /// </summary>
    public bool RemoveMetadata { get; init; }

    /// <summary>Remove embedded page thumbnails (viewers draw their own).</summary>
    public bool RemoveThumbnails { get; init; } = true;

    /// <summary>Remove application private data (/PieceInfo, e.g. Illustrator's editing copy of the page).</summary>
    public bool RemovePieceInfo { get; init; } = true;

    /// <summary>Images shown at more than this resolution are downsampled to it (and stored as JPEG); null = keep.</summary>
    public double? MaxImageDpi { get; init; } = 150;

    /// <summary>JPEG quality (1-100) of downsampled images.</summary>
    public int JpegQuality { get; init; } = 75;
}

/// <summary>What <see cref="Optimizer.Optimize"/> produced. The output can come out larger (e.g. a file already compact
/// in object streams, which PDFsharp doesn't write): compare the sizes before replacing the original.</summary>
public sealed record OptimizeResult(byte[] Data, long SizeBefore, long SizeAfter, int StreamsCompressed, int DuplicatesRemoved, int ImagesDownsampled)
{
    public long Saved => SizeBefore - SizeAfter;
}

/// <summary>
/// Reduces file size. Always a full rewrite, which by itself drops unreferenced objects and earlier incremental
/// revisions; then, as <see cref="OptimizeOptions"/> asks: identical streams merged, uncompressed streams Flate-compressed,
/// thumbnails / private data / XMP removed, and images shown above a resolution downsampled to it as JPEG. Images are
/// decoded and resampled by MuPDF (a one-image page rendered at the target size, so every filter it reads works),
/// limited to 8-bit DeviceGray/RGB/CMYK (or ICC-based) images without a color-key /Mask; their soft mask is resampled
/// too (kept lossless). An image's resolution is judged from where page content and form XObjects draw it (largest use
/// wins); images drawn only elsewhere (patterns, annotations, Type 3 glyphs) are left alone. PDFsharp 6.2 can't write
/// object streams, so none are made. Signed documents need the caller's consent (<see cref="SignedEdits.Run"/> with the
/// rewrite allowed), as the rewrite invalidates their signatures. An encrypted document (opened with its owner password)
/// keeps its protection (see <see cref="KeptProtection"/>).
/// </summary>
public static class Optimizer
{
    /// <exception cref="SignaturesWouldBreakException">The document is signed and the rewrite wasn't allowed.</exception>
    public static OptimizeResult Optimize(byte[] data, string? password, OptimizeOptions? options = null)
    {
        options ??= new OptimizeOptions();
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        SignedEdits.RequireRewrite(data, doc, "optimizing rewrites the whole file");

        RemoveExtras(doc, options);
        var duplicates = options.DeduplicateStreams ? Deduplicate(doc) : 0;
        var downsampled = options.MaxImageDpi is { } dpi and > 0 ? Downsample(doc, dpi, Math.Clamp(options.JpegQuality, 1, 100)) : 0;
        var compressed = options.CompressStreams ? Compress(doc) : 0;

        doc.Options.CompressContentStreams = true;
        doc.Options.NoCompression = false;
        var saved = PdfObj.Save(doc, rewrite: true);
        return new OptimizeResult(saved, data.Length, saved.Length, compressed, duplicates, downsampled);
    }

    /// <summary>The indirect dictionaries (and streams) the document still uses: what a rewrite keeps.</summary>
    private static List<PdfDictionary> Objects(PdfDocument doc) =>
        [.. doc.Internals.GetClosure(doc.Internals.Catalog).OfType<PdfDictionary>().Distinct(ReferenceEqualityComparer.Instance).Cast<PdfDictionary>()];

    private static void RemoveExtras(PdfDocument doc, OptimizeOptions options)
    {
        if (options.RemoveThumbnails)
            foreach (var page in doc.Pages) page.Elements.Remove("/Thumb");
        if (!options.RemovePieceInfo && !options.RemoveMetadata) return;
        foreach (var d in Objects(doc))
        {
            if (options.RemovePieceInfo) d.Elements.Remove("/PieceInfo");
            if (options.RemoveMetadata && PdfObj.Dict(d.Elements["/Metadata"]) is { Stream: not null }) d.Elements.Remove("/Metadata");
        }
    }

    // ---- Identical streams ----

    /// <summary>Points every use of a byte-identical stream (same dictionary too) at one copy; returns how many copies went.</summary>
    private static int Deduplicate(PdfDocument doc)
    {
        var total = 0;
        // Streams referring to merged ones (an image to its mask, a font to its file) only match on the next round.
        for (var round = 0; round < 4; round++)
        {
            var keep = new Dictionary<string, PdfReference>();
            var map = new Dictionary<PdfObjectID, PdfReference>();
            foreach (var d in Objects(doc))
            {
                if (d.Stream is null || d.Reference is not { } r || PdfObj.NameOf(d, "/Type") is "/Metadata" or "/XRef" or "/ObjStm") continue;
                var key = Convert.ToHexString(SHA256.HashData(d.Stream.Value)) + Canonical(d, 0);
                if (keep.TryGetValue(key, out var first)) map[r.ObjectID] = first;
                else keep[key] = r;
            }
            if (map.Count == 0) break;
            total += map.Count;
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var d in Objects(doc).ToList()) Redirect(d, map, seen, 0);
            Redirect(doc.Internals.Catalog, map, seen, 0);
        }
        return total;
    }

    /// <summary>A dictionary's entries (not /Length) as text, references by object number.</summary>
    private static string Canonical(PdfItem? item, int depth)
    {
        if (depth > 16) return "?";
        return item switch
        {
            PdfReference r => $"R{r.ObjectNumber}.{r.GenerationNumber}",
            PdfDictionary d => "<<" + string.Concat(d.Elements.Keys.Where(k => k != "/Length").Order(StringComparer.Ordinal)
                .Select(k => k + " " + Canonical(d.Elements[k], depth + 1) + " ")) + ">>",
            PdfArray a => "[" + string.Join(' ', a.Elements.Select(e => Canonical(e, depth + 1))) + "]",
            PdfString s => "(" + Convert.ToHexString(Encoding.Latin1.GetBytes(s.Value)) + ")",
            null => "null",
            _ => item.ToString() ?? "",
        };
    }

    private static void Redirect(PdfItem item, Dictionary<PdfObjectID, PdfReference> map, HashSet<object> seen, int depth)
    {
        if (depth > 64 || !seen.Add(item)) return;
        switch (item)
        {
            case PdfDictionary d:
                foreach (var key in d.Elements.Keys.ToList())
                {
                    var v = d.Elements[key];
                    if (v is PdfReference r && map.TryGetValue(r.ObjectID, out var to)) d.Elements[key] = to;
                    else if (v is PdfDictionary or PdfArray) Redirect(v, map, seen, depth + 1);
                }
                break;
            case PdfArray a:
                for (var i = 0; i < a.Elements.Count; i++)
                {
                    var v = a.Elements[i];
                    if (v is PdfReference r && map.TryGetValue(r.ObjectID, out var to)) a.Elements[i] = to;
                    else if (v is PdfDictionary or PdfArray) Redirect(v, map, seen, depth + 1);
                }
                break;
        }
    }

    // ---- Compression ----

    private static readonly HashSet<string> Recodable = ["ASCIIHexDecode", "ASCII85Decode", "LZWDecode", "FlateDecode"];

    /// <summary>Flate-compresses unfiltered streams, and re-encodes ASCII/LZW-encoded ones; returns how many changed.</summary>
    private static int Compress(PdfDocument doc)
    {
        var count = 0;
        foreach (var d in Objects(doc).ToList())
        {
            if (d.Stream is not { } stream || stream.Value.Length < 64 || PdfObj.NameOf(d, "/Type") is "/Metadata" or "/XRef" or "/ObjStm") continue;
            var filters = Filters(d);
            if (filters is null) continue;
            byte[]? raw = stream.Value;
            if (filters.Count > 0)
            {
                // Only chains holding an ASCII or LZW filter gain anything; parameters (predictors) are left alone.
                if (!filters.All(Recodable.Contains) || filters.All(f => f == "FlateDecode") || d.Elements.ContainsKey("/DecodeParms")) continue;
                foreach (var f in filters)
                {
                    raw = Filtering.Decode(raw, "/" + f);
                    if (raw is null) break;
                }
                if (raw is null) continue;
            }
            var packed = PdfObj.Deflate(raw);
            if (packed.Length >= stream.Value.Length) continue;
            stream.Value = packed;
            d.Elements["/Filter"] = new PdfName("/FlateDecode");
            d.Elements.Remove("/DecodeParms");
            d.Elements["/Length"] = new PdfInteger(packed.Length);
            count++;
        }
        return count;
    }

    /// <summary>The stream's filter names (empty = none), or null when they can't be read.</summary>
    private static List<string>? Filters(PdfDictionary d) => PdfObj.Resolve(d.Elements["/Filter"]) switch
    {
        null => [],
        PdfName n => [n.Value.TrimStart('/')],
        PdfArray a when a.Elements.All(e => PdfObj.Resolve(e) is PdfName) => [.. a.Elements.Select(e => PdfObj.Name(e)!)],
        _ => null,
    };

    // ---- Image downsampling ----

    private static int Downsample(PdfDocument doc, double targetDpi, int quality)
    {
        // Smallest resolution each image is shown at (pixels per inch along its width and height).
        var shown = new Dictionary<PdfDictionary, (double X, double Y)>(ReferenceEqualityComparer.Instance);
        foreach (var page in doc.Pages)
        {
            ContentImages.Scan(page, (image, ctm) =>
            {
                var w = PdfObj.Int(image.Elements["/Width"]) ?? 0;
                var h = PdfObj.Int(image.Elements["/Height"]) ?? 0;
                var lx = Math.Sqrt(ctm[0] * ctm[0] + ctm[1] * ctm[1]) / 72;
                var ly = Math.Sqrt(ctm[2] * ctm[2] + ctm[3] * ctm[3]) / 72;
                var dpi = (X: lx > 1e-6 ? w / lx : double.MaxValue, Y: ly > 1e-6 ? h / ly : double.MaxValue);
                shown[image] = shown.TryGetValue(image, out var was) ? (Math.Min(was.X, dpi.X), Math.Min(was.Y, dpi.Y)) : dpi;
            });
        }

        var count = 0;
        foreach (var (image, dpi) in shown)
        {
            if (dpi.X <= targetDpi * 1.1 && dpi.Y <= targetDpi * 1.1) continue;
            var w = PdfObj.Int(image.Elements["/Width"]) ?? 0;
            var h = PdfObj.Int(image.Elements["/Height"]) ?? 0;
            var nw = Math.Max(1, (int)Math.Ceiling(w * Math.Min(1, targetDpi / dpi.X)));
            var nh = Math.Max(1, (int)Math.Ceiling(h * Math.Min(1, targetDpi / dpi.Y)));
            if ((double)nw * nh > 0.8 * w * h) continue;
            if (Resample(image, nw, nh, quality)) count++;
        }
        return count;
    }

    /// <summary>Replaces the image (and its soft mask) by a <paramref name="w"/> x <paramref name="h"/> version, if that is smaller.</summary>
    private static bool Resample(PdfDictionary image, int w, int h, int quality)
    {
        if (ImageSource(image, maskOnly: false) is not { } source) return false;
        var smask = PdfObj.Dict(image.Elements["/SMask"]);
        string? maskSource = null;
        if (smask is not null && ((maskSource = ImageSource(smask, maskOnly: true)) is null || smask.Elements.ContainsKey("/Matte"))) return false;

        var jpeg = Render(source, image.Stream!.Value, w, h, quality);
        if (jpeg is null) return false;
        byte[]? mask = null;
        if (smask is not null)
        {
            var rgb = Render(maskSource!, smask.Stream!.Value, w, h, null);
            if (rgb is null || rgb.Length != w * h * 3) return false;
            var gray = new byte[w * h];
            for (var i = 0; i < gray.Length; i++) gray[i] = rgb[3 * i];
            mask = PdfObj.Deflate(gray);
        }
        if (jpeg.Length + (mask?.Length ?? 0) >= image.Stream.Value.Length + (smask?.Stream!.Value.Length ?? 0)) return false;

        Replace(image, jpeg, w, h, "/DeviceRGB", "/DCTDecode");
        if (smask is not null) Replace(smask, mask!, w, h, "/DeviceGray", "/FlateDecode");
        return true;
    }

    private static void Replace(PdfDictionary image, byte[] bytes, int w, int h, string colorSpace, string filter)
    {
        image.Stream!.Value = bytes;
        image.Elements["/Length"] = new PdfInteger(bytes.Length);
        image.Elements["/Width"] = new PdfInteger(w);
        image.Elements["/Height"] = new PdfInteger(h);
        image.Elements["/BitsPerComponent"] = new PdfInteger(8);
        image.Elements["/ColorSpace"] = new PdfName(colorSpace);
        image.Elements["/Filter"] = new PdfName(filter);
        image.Elements.Remove("/DecodeParms");
        image.Elements.Remove("/Decode");
    }

    /// <summary>
    /// The image dictionary to draw it on its own (filters, color space as a device space), or null when it isn't one
    /// this resamples.
    /// </summary>
    private static string? ImageSource(PdfDictionary image, bool maskOnly)
    {
        if (image.Stream is null || PdfObj.Resolve(image.Elements["/ImageMask"]) is PdfBoolean { Value: true }) return null;
        if (image.Elements.ContainsKey("/Mask") || PdfObj.Int(image.Elements["/BitsPerComponent"]) != 8) return null;
        int w = PdfObj.Int(image.Elements["/Width"]) ?? 0, h = PdfObj.Int(image.Elements["/Height"]) ?? 0;
        if (w <= 0 || h <= 0) return null;
        var filters = Filters(image);
        if (filters is null || filters.Any(f => f is "JBIG2Decode" or "JPXDecode" or "CCITTFaxDecode" or "Crypt")) return null;
        var cs = ColorSpace(image.Elements["/ColorSpace"]);
        if (cs is null || (maskOnly && cs != "/DeviceGray")) return null;

        var sb = new StringBuilder($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /BitsPerComponent 8 /ColorSpace {cs}");
        foreach (var key in new[] { "/Filter", "/DecodeParms", "/Decode" })
        {
            if (image.Elements[key] is not { } v) continue;
            if (Plain(v, 0) is not { } text) return null;
            sb.Append($" {key} {text}");
        }
        return sb.ToString();
    }

    /// <summary>A device color space name for the image's color space, or null when it isn't one this handles.</summary>
    private static string? ColorSpace(PdfItem? item)
    {
        switch (PdfObj.Resolve(item))
        {
            case PdfName n when n.Value is "/DeviceRGB" or "/DeviceGray" or "/DeviceCMYK":
                return n.Value;
            case PdfArray a when a.Elements.Count == 2 && PdfObj.Name(a.Elements[0]) == "ICCBased" && PdfObj.Dict(a.Elements[1]) is { } icc:
                return PdfObj.Int(icc.Elements["/N"]) switch { 1 => "/DeviceGray", 3 => "/DeviceRGB", 4 => "/DeviceCMYK", _ => null };
            default:
                return null;
        }
    }

    /// <summary>A direct PDF value written out (names, numbers, booleans, arrays, dictionaries); null for anything else.</summary>
    private static string? Plain(PdfItem item, int depth)
    {
        if (depth > 8) return null;
        switch (PdfObj.Resolve(item))
        {
            case PdfName n: return n.Value;
            case PdfInteger i: return i.Value.ToString(PdfObj.Inv);
            case PdfLongInteger l: return l.Value.ToString(PdfObj.Inv);
            case PdfReal r: return PdfObj.F(r.Value);
            case PdfBoolean b: return b.Value ? "true" : "false";
            case PdfNull: return "null";
            case PdfArray a:
            {
                var parts = new List<string>();
                foreach (var e in a.Elements)
                {
                    if (Plain(e, depth + 1) is not { } s) return null;
                    parts.Add(s);
                }
                return "[" + string.Join(' ', parts) + "]";
            }
            case PdfDictionary { Stream: null } d:
            {
                var sb = new StringBuilder("<<");
                foreach (var key in d.Elements.Keys)
                {
                    if (Plain(d.Elements[key]!, depth + 1) is not { } s) return null;
                    sb.Append($" {key} {s}");
                }
                return sb.Append(" >>").ToString();
            }
            default: return null;
        }
    }

    /// <summary>
    /// The image drawn by MuPDF onto a <paramref name="w"/> x <paramref name="h"/> pixel page: JPEG bytes at
    /// <paramref name="quality"/>, or raw RGB without one. Null when MuPDF can't draw it.
    /// </summary>
    private static byte[]? Render(string imageDict, byte[] stream, int w, int h, int? quality)
    {
        var pdf = OneImagePage(imageDict, stream, w, h);
        using var ctx = new MuPDFContext(64 * 1024 * 1024);
        MuPDFDocument? mu = null;
        try
        {
            mu = new MuPDFDocument(ctx, pdf, InputFileTypes.PDF);
            if (quality is null) return mu.Render(0, 1, PixelFormats.RGB, includeAnnotations: false);
            using var ms = new MemoryStream();
            mu.WriteImageAsJPEG(0, 1, ms, quality.Value, includeAnnotations: false);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is MuPDFException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (mu is not null) PdfEngine.DisposeDocument(mu);
        }
    }

    /// <summary>A PDF whose single w x h point page is filled by the image.</summary>
    private static byte[] OneImagePage(string imageDict, byte[] stream, int w, int h)
    {
        var latin1 = Encoding.Latin1;
        using var ms = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) => ms.Write(latin1.GetBytes(s));
        void Obj(string body, byte[]? data = null)
        {
            offsets.Add(ms.Position);
            Write($"{offsets.Count} 0 obj\n{body}");
            if (data is not null)
            {
                Write("\nstream\n");
                ms.Write(data);
                Write("\nendstream");
            }
            Write("\nendobj\n");
        }
        Write("%PDF-1.7\n");
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im 5 0 R >> >> /Contents 4 0 R >>");
        var content = latin1.GetBytes($"q {w} 0 0 {h} 0 0 cm /Im Do Q");
        Obj($"<< /Length {content.Length} >>", content);
        Obj($"<< {imageDict} /Length {stream.Length} >>", stream);
        var xref = ms.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}

/// <summary>
/// Finds the image XObjects a page's content draws and the transformation each is drawn with (through form XObjects),
/// by following q/Q/cm and Do in the content streams.
/// </summary>
internal static class ContentImages
{
    private const int MaxDepth = 12;

    public static void Scan(PdfPage page, Action<PdfDictionary, double[]> onImage)
    {
        var content = new MemoryStream();
        var item = page.Elements["/Contents"];
        IEnumerable<PdfItem> parts = PdfObj.Array(item) is { } list ? list.Elements : item is null ? [] : [item];
        foreach (var part in parts)
            if (PdfObj.Dict(part) is { Stream: not null } s)
            {
                content.Write(PdfObj.StreamBytes(s));
                content.WriteByte((byte)'\n');
            }
        var visiting = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        Run(content.ToArray(), PdfObj.Dict(page.Elements["/Resources"]), [1, 0, 0, 1, 0, 0], onImage, visiting, 0);
    }

    private static void Run(byte[] ops, PdfDictionary? resources, double[] start, Action<PdfDictionary, double[]> onImage, HashSet<PdfDictionary> visiting, int depth)
    {
        var ctm = start;
        var stack = new Stack<double[]>();
        var operands = new List<object>();
        var xobjects = PdfObj.Dict(resources?.Elements["/XObject"]);
        var i = 0;
        while (i < ops.Length)
        {
            var c = ops[i];
            if (IsWhite(c))
            {
                i++;
                continue;
            }
            switch ((char)c)
            {
                case '%':
                    while (i < ops.Length && ops[i] is not ((byte)'\n' or (byte)'\r')) i++;
                    continue;
                case '(':
                    i = SkipString(ops, i);
                    operands.Add("");
                    continue;
                case '<' when i + 1 < ops.Length && ops[i + 1] == '<':
                    i = SkipDict(ops, i);
                    operands.Add("");
                    continue;
                case '<':
                    while (i < ops.Length && ops[i] != '>') i++;
                    i++;
                    operands.Add("");
                    continue;
                case '[' or ']' or '{' or '}':
                    i++;
                    continue;
                case '/':
                {
                    var s = ++i;
                    while (i < ops.Length && !IsWhite(ops[i]) && !IsDelimiter(ops[i])) i++;
                    operands.Add("/" + Encoding.Latin1.GetString(ops, s, i - s));
                    continue;
                }
            }
            var start2 = i;
            while (i < ops.Length && !IsWhite(ops[i]) && !IsDelimiter(ops[i])) i++;
            if (i == start2)
            {
                i++;
                continue;
            }
            var word = Encoding.Latin1.GetString(ops, start2, i - start2);
            if (word[0] is >= '0' and <= '9' or '-' or '+' or '.')
            {
                operands.Add(double.TryParse(word, System.Globalization.NumberStyles.Float, PdfObj.Inv, out var n) ? n : 0.0);
                continue;
            }
            switch (word)
            {
                case "q":
                    stack.Push(ctm);
                    break;
                case "Q":
                    if (stack.Count > 0) ctm = stack.Pop();
                    break;
                case "cm" when operands.Count >= 6 && operands.TakeLast(6).All(o => o is double):
                {
                    var m = operands.TakeLast(6).Cast<double>().ToArray();
                    ctm = Multiply(m, ctm);
                    break;
                }
                case "Do" when operands.Count >= 1 && operands[^1] is string name && name.StartsWith('/'):
                {
                    if (PdfObj.Dict(xobjects?.Elements[name]) is not { } x) break;
                    var subtype = PdfObj.NameOf(x, "/Subtype");
                    if (subtype == "/Image") onImage(x, ctm);
                    else if (subtype == "/Form" && depth < MaxDepth && x.Stream is not null && visiting.Add(x))
                    {
                        var fm = PdfObj.Array(x.Elements["/Matrix"]) is { Elements.Count: >= 6 } a
                            ? Enumerable.Range(0, 6).Select(k => PdfObj.Num(a, k)).ToArray()
                            : [1, 0, 0, 1, 0, 0];
                        Run(PdfObj.StreamBytes(x), PdfObj.Dict(x.Elements["/Resources"]) ?? resources, Multiply(fm, ctm), onImage, visiting, depth + 1);
                        visiting.Remove(x);
                    }
                    break;
                }
                case "BI":
                    i = SkipInlineImage(ops, i);
                    break;
            }
            operands.Clear();
        }
    }

    /// <summary>m × n for PDF matrices [a b c d e f].</summary>
    private static double[] Multiply(double[] m, double[] n) =>
    [
        m[0] * n[0] + m[1] * n[2], m[0] * n[1] + m[1] * n[3],
        m[2] * n[0] + m[3] * n[2], m[2] * n[1] + m[3] * n[3],
        m[4] * n[0] + m[5] * n[2] + n[4], m[4] * n[1] + m[5] * n[3] + n[5],
    ];

    private static bool IsWhite(byte c) => c is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\f' or 0;

    private static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static int SkipString(byte[] b, int i)
    {
        var depth = 0;
        for (; i < b.Length; i++)
        {
            if (b[i] == '\\')
            {
                i++;
                continue;
            }
            if (b[i] == '(') depth++;
            else if (b[i] == ')' && --depth == 0) return i + 1;
        }
        return i;
    }

    private static int SkipDict(byte[] b, int i)
    {
        var depth = 0;
        while (i < b.Length)
        {
            if (b[i] == '(')
            {
                i = SkipString(b, i);
                continue;
            }
            if (i + 1 < b.Length && b[i] == '<' && b[i + 1] == '<')
            {
                depth++;
                i += 2;
                continue;
            }
            if (i + 1 < b.Length && b[i] == '>' && b[i + 1] == '>')
            {
                i += 2;
                if (--depth == 0) return i;
                continue;
            }
            i++;
        }
        return i;
    }

    /// <summary>Skips an inline image's dictionary and data, to just past its EI.</summary>
    private static int SkipInlineImage(byte[] b, int i)
    {
        // "ID" followed by one white-space byte starts the data; it ends at white space, "EI", then white space or the end.
        for (; i + 2 < b.Length; i++)
            if (b[i] == 'I' && b[i + 1] == 'D' && IsWhite(b[i - 1]) && IsWhite(b[i + 2])) break;
        for (i += 3; i + 1 < b.Length; i++)
            if (IsWhite(b[i - 1]) && b[i] == 'E' && b[i + 1] == 'I' && (i + 2 >= b.Length || IsWhite(b[i + 2]))) return i + 2;
        return b.Length;
    }
}
