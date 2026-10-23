using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfDesk.Core;

/// <summary>Small PDFsharp helpers shared by the form, annotation and document-feature code.</summary>
internal static class PdfObj
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    public static PdfDictionary? Dict(PdfItem? item) => Resolve(item) as PdfDictionary;

    public static PdfArray? Array(PdfItem? item) => Resolve(item) as PdfArray;

    public static double Num(PdfItem? item, double fallback = 0) => Resolve(item) switch
    {
        PdfReal r => r.Value,
        PdfInteger n => n.Value,
        PdfLongInteger l => l.Value,
        _ => fallback,
    };

    public static double Num(PdfArray a, int i) => i < a.Elements.Count ? Num(a.Elements[i]) : 0;

    public static int? Int(PdfItem? item) => Resolve(item) switch
    {
        PdfInteger n => n.Value,
        PdfLongInteger l => (int)l.Value,
        PdfReal r => (int)r.Value,
        _ => null,
    };

    /// <summary>A PDF text string's value (null when the item isn't a string).</summary>
    public static string? Str(PdfItem? item) => Resolve(item) is PdfString s ? s.Value : null;

    /// <summary>A name without its slash.</summary>
    public static string? Name(PdfItem? item) => Resolve(item) is PdfName n ? n.Value.TrimStart('/') : null;

    /// <summary>A dictionary entry's name with its slash ("/Widget"), or "" when it is missing or not a name (never throws).</summary>
    public static string NameOf(PdfDictionary d, string key) => Resolve(d.Elements[key]) is PdfName n ? n.Value : "";

    /// <summary>Text held in a string or a stream (JavaScript actions use either).</summary>
    public static string? Text(PdfItem? item)
    {
        switch (Resolve(item))
        {
            case PdfString s:
                return s.Value;
            case PdfDictionary { Stream: not null } d:
                var bytes = StreamBytes(d);
                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                return Encoding.Latin1.GetString(bytes);
            default:
                return null;
        }
    }

    /// <summary>A stream's decoded bytes (PDFsharp decodes Flate and the other standard filters).</summary>
    public static byte[] StreamBytes(PdfDictionary d) => d.Stream?.UnfilteredValue ?? [];

    public static PdfString MakeString(string value) =>
        value.All(c => c < 128) ? new PdfString(value) : new PdfString(value, PdfStringEncoding.Unicode);

    public static PdfArray Reals(PdfDocument doc, params double[] values) =>
        new(doc, [.. values.Select(v => (PdfItem)new PdfReal(Math.Round(v, 4)))]);

    public static string F(double v) => Math.Round(v, 3).ToString("0.###", Inv);

    public static PdfDocument Open(byte[] data, string? password, PdfDocumentOpenMode mode, string failure = "This PDF can't be read")
    {
        try
        {
            var stream = new MemoryStream(data, writable: false);
            var doc = string.IsNullOrEmpty(password) ? PdfReader.Open(stream, mode) : PdfReader.Open(stream, password, mode);
            // A signed document opened for editing: remember its original objects so the save can be incremental.
            if (mode == PdfDocumentOpenMode.Modify)
            {
                Incremental.Track(doc, data, password);
                KeptProtection.Track(doc, data, password); // encrypted: the edit is saved with the same protection
            }
            return doc;
        }
        catch (PdfReaderException ex)
        {
            throw ReaderError(ex, mode, failure);
        }
    }

    /// <summary>
    /// Maps a PDFsharp open failure. In Modify mode the document already opened for viewing, so a password complaint
    /// means the permissions (owner) password is missing or wrong, which the UI can ask for and retry.
    /// </summary>
    public static Exception ReaderError(PdfReaderException ex, PdfDocumentOpenMode mode, string failure)
    {
        if (mode == PdfDocumentOpenMode.Modify && ex.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase))
            return new OwnerPasswordRequiredException(false, ex);
        if (mode == PdfDocumentOpenMode.Modify && ex.Message.Contains("password is invalid", StringComparison.OrdinalIgnoreCase))
            return new OwnerPasswordRequiredException(true, ex);
        return new InvalidOperationException($"{failure}: {ex.Message}", ex);
    }

    /// <summary>
    /// Saves an edited document. PDFsharp replaces the XMP metadata packet on every save, so the document's own packet
    /// (read just before saving) is written back, updated from the Info dictionary (see <see cref="Xmp"/>).
    /// Every Core path that saves with PDFsharp goes through here (PageEditor has its own Save doing the same).
    /// A signed document opened through <see cref="Open"/> is saved as an incremental update instead, which keeps its
    /// signatures valid (see <see cref="Incremental"/>), unless <paramref name="rewrite"/> asks for a full rewrite
    /// (redaction, which must not leave the removed content in the file).
    /// </summary>
    /// <exception cref="SignaturesWouldBreakException">A signed document whose edit can't be appended (see <see cref="SignedEdits"/>).</exception>
    public static byte[] Save(PdfDocument doc, bool rewrite = false)
    {
        var xmp = Xmp.Read(doc);
        if (!rewrite && Incremental.Save(doc, xmp) is { } appended) return appended;
        KeptProtection.Reapply(doc);
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return Xmp.Patch(ms, doc.Info, xmp);
    }

    /// <summary>Adds an indirect stream object (a form XObject when <paramref name="bbox"/> is given).</summary>
    public static PdfDictionary AddStream(PdfDocument doc, byte[] content, bool compress = true)
    {
        var d = new PdfDictionary(doc);
        if (compress && content.Length > 64)
        {
            d.CreateStream(Deflate(content));
            d.Elements["/Filter"] = new PdfName("/FlateDecode");
        }
        else d.CreateStream(content);
        doc.Internals.AddObject(d);
        return d;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    public static PdfDictionary FormXObject(PdfDocument doc, byte[] content, double x1, double y1, double x2, double y2, PdfDictionary? resources, PdfArray? matrix = null)
    {
        var form = AddStream(doc, content);
        form.Elements["/Type"] = new PdfName("/XObject");
        form.Elements["/Subtype"] = new PdfName("/Form");
        form.Elements["/BBox"] = Reals(doc, x1, y1, x2, y2);
        if (matrix is not null) form.Elements["/Matrix"] = matrix;
        form.Elements["/Resources"] = resources ?? new PdfDictionary(doc);
        return form;
    }

    // ---- Page geometry: page space = points, top-left origin of the displayed (cropped, rotated) page ----

    private static (double Bx1, double By2, double W, double H, int Rot) Frame(PdfPage page)
    {
        var (x1, y1, x2, y2) = VisibleBox(page);
        return (x1, y2, x2 - x1, y2 - y1, ((page.Rotate % 360) + 360) % 360);
    }

    /// <summary>
    /// The page's visible area (x1 &lt; x2, y1 &lt; y2): its CropBox clipped to its MediaBox, as viewers show it (the
    /// MediaBox when there is no CropBox or it lies outside the MediaBox).
    /// </summary>
    public static (double X1, double Y1, double X2, double Y2) VisibleBox(PdfPage page)
    {
        var m = page.MediaBox;
        var media = (Math.Min(m.X1, m.X2), Math.Min(m.Y1, m.Y2), Math.Max(m.X1, m.X2), Math.Max(m.Y1, m.Y2));
        if (!page.Elements.ContainsKey("/CropBox")) return media;
        var c = page.CropBox;
        var x1 = Math.Max(media.Item1, Math.Min(c.X1, c.X2));
        var y1 = Math.Max(media.Item2, Math.Min(c.Y1, c.Y2));
        var x2 = Math.Min(media.Item3, Math.Max(c.X1, c.X2));
        var y2 = Math.Min(media.Item4, Math.Max(c.Y1, c.Y2));
        return x2 > x1 && y2 > y1 ? (x1, y1, x2, y2) : media;
    }

    /// <summary>PDF user space → page space.</summary>
    public static (double X, double Y) ToPage(PdfPage page, double x, double y)
    {
        var (bx1, by2, w, h, rot) = Frame(page);
        double u = x - bx1, v = by2 - y;
        return rot switch
        {
            90 => (h - v, u),
            180 => (w - u, h - v),
            270 => (v, w - u),
            _ => (u, v),
        };
    }

    /// <summary>Page space → PDF user space.</summary>
    public static (double X, double Y) ToPdf(PdfPage page, double px, double py)
    {
        var (bx1, by2, w, h, rot) = Frame(page);
        var (u, v) = rot switch
        {
            90 => (py, h - px),
            180 => (w - px, h - py),
            270 => (w - py, px),
            _ => (px, py),
        };
        return (bx1 + u, by2 - v);
    }

    /// <summary>Displayed page size in points (rotation applied).</summary>
    public static (double W, double H) DisplaySize(PdfPage page)
    {
        var (_, _, w, h, rot) = Frame(page);
        return rot is 90 or 270 ? (h, w) : (w, h);
    }

    public static RectD RectToPage(PdfPage page, PdfArray r)
    {
        if (r.Elements.Count < 4) return default;
        return FormEditor.ToPageSpace(page, Num(r, 0), Num(r, 1), Num(r, 2), Num(r, 3));
    }

    /// <summary>The /Matrix that keeps an appearance upright in the viewer on a page with /Rotate.</summary>
    public static PdfArray? CounterRotation(PdfDocument doc, PdfPage page)
    {
        var rot = ((page.Rotate % 360) + 360) % 360;
        if (rot == 0) return null;
        var (a, b, c, d) = rot switch { 90 => (0, 1, -1, 0), 180 => (-1, 0, 0, -1), _ => (0, -1, 1, 0) };
        return new PdfArray(doc, new PdfInteger(a), new PdfInteger(b), new PdfInteger(c), new PdfInteger(d), new PdfInteger(0), new PdfInteger(0));
    }

    /// <summary>Index of the page an item refers to (a page reference, or a bare page number), or -1.</summary>
    public static int PageIndexOf(PdfDocument doc, PdfItem? item)
    {
        if (item is PdfReference r)
        {
            for (var i = 0; i < doc.PageCount; i++)
                if (doc.Pages[i].Reference is { } pr && pr.ObjectID == r.ObjectID) return i;
            var target = r.Value;
            for (var i = 0; i < doc.PageCount; i++)
                if (ReferenceEquals(doc.Pages[i], target)) return i;
            return -1;
        }
        if (item is PdfDictionary d)
        {
            for (var i = 0; i < doc.PageCount; i++)
                if (ReferenceEquals(doc.Pages[i], d)) return i;
            return -1;
        }
        return Int(item) is { } n && n >= 0 && n < doc.PageCount ? n : -1;
    }

    public static PdfArray Annots(PdfDocument doc, PdfPage page)
    {
        if (Array(page.Elements["/Annots"]) is { } a)
        {
            if (page.Elements["/Annots"] is not PdfReference) return a;
            // Annots held indirectly may be shared between pages: give this page its own copy.
            var copy = new PdfArray(doc);
            foreach (var e in a.Elements) copy.Elements.Add(e);
            page.Elements["/Annots"] = copy;
            return copy;
        }
        var created = new PdfArray(doc);
        page.Elements["/Annots"] = created;
        return created;
    }

    // ---- Name and number trees ----

    public static IEnumerable<(string Key, PdfItem? Value)> NameTree(PdfDictionary? node) =>
        TreeNodes(node).SelectMany(n => Pairs(n, "/Names")).Where(p => Str(p.Key) is not null).Select(p => (Str(p.Key)!, p.Value));

    public static IEnumerable<(int Key, PdfItem? Value)> NumberTree(PdfDictionary? node) =>
        TreeNodes(node).SelectMany(n => Pairs(n, "/Nums")).Where(p => Int(p.Key) is not null).Select(p => (Int(p.Key)!.Value, p.Value));

    private static IEnumerable<(PdfItem Key, PdfItem? Value)> Pairs(PdfDictionary node, string key)
    {
        if (Array(node.Elements[key]) is not { } a) yield break;
        for (var i = 0; i + 1 < a.Elements.Count; i += 2) yield return (a.Elements[i], a.Elements[i + 1]);
    }

    /// <summary>
    /// A name/number tree's nodes in order, each visited once: a hostile file whose /Kids repeat or point back at a node
    /// would otherwise loop forever or blow up exponentially.
    /// </summary>
    private static IEnumerable<PdfDictionary> TreeNodes(PdfDictionary? root)
    {
        if (root is null) yield break;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfDictionary Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > 32 || !seen.Add(node)) continue;
            yield return node;
            if (Array(node.Elements["/Kids"]) is { } kids)
                for (var i = kids.Elements.Count - 1; i >= 0; i--)
                    if (Dict(kids.Elements[i]) is { } kid) stack.Push((kid, depth + 1));
        }
    }

    // ---- Dates ----

    /// <summary>Parses a PDF date ("D:YYYYMMDDHHmmSSOHH'mm'"), tolerating the usual producer variations.</summary>
    public static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 4) return null;
        int Part(int start, int len, int def) => digits.Length >= start + len ? int.Parse(digits.AsSpan(start, len), Inv) : def;
        try
        {
            var year = Part(0, 4, 1);
            var dt = new DateTime(year, Math.Clamp(Part(4, 2, 1), 1, 12), 1);
            dt = dt.AddDays(Math.Clamp(Part(6, 2, 1), 1, DateTime.DaysInMonth(dt.Year, dt.Month)) - 1)
                .AddHours(Math.Clamp(Part(8, 2, 0), 0, 23)).AddMinutes(Math.Clamp(Part(10, 2, 0), 0, 59)).AddSeconds(Math.Clamp(Part(12, 2, 0), 0, 59));
            var rest = s[digits.Length..];
            var offset = TimeSpan.Zero;
            if (rest.Length > 0 && rest[0] is '+' or '-')
            {
                var tz = new string(rest[1..].Where(char.IsDigit).ToArray());
                var hh = tz.Length >= 2 ? int.Parse(tz.AsSpan(0, 2), Inv) : 0;
                var mm = tz.Length >= 4 ? int.Parse(tz.AsSpan(2, 2), Inv) : 0;
                offset = new TimeSpan(Math.Min(hh, 14), Math.Min(mm, 59), 0);
                if (rest[0] == '-') offset = -offset;
            }
            return new DateTimeOffset(dt, offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static PdfString DateString(DateTimeOffset t)
    {
        var o = t.Offset;
        var sign = o < TimeSpan.Zero ? '-' : '+';
        o = o.Duration();
        return new PdfString(string.Create(Inv, $"D:{t.ToString("yyyyMMddHHmmss", Inv)}{sign}{o.Hours:00}'{o.Minutes:00}'"));
    }
}
