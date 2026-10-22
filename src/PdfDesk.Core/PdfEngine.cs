using MuPDFCore;
using MuPDFCore.StructuredText;

namespace PdfDesk.Core;

public sealed class PasswordRequiredException(bool wrongPassword) : Exception(wrongPassword ? "Incorrect password." : "This document is password protected.")
{
    public bool WrongPassword { get; } = wrongPassword;
}

public sealed record OutlineNode(string Title, int PageIndex, IReadOnlyList<OutlineNode> Children);

/// <summary>A clickable area on a page: either an internal jump (PageIndex) or an external URI.</summary>
public sealed record PageLink(RectD Area, int PageIndex, string? Uri);

/// <summary>Pixels from a render, packed BGR24 (tight stride = Width * 3).</summary>
public sealed record RenderedImage(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 3;
}

/// <summary>
/// One open document in MuPDF. MuPDF is not thread-safe per document, so every call takes a lock; callers may use
/// it from any thread (renders run on a background worker, hit-testing on the UI thread).
/// </summary>
public sealed class PdfEngine : IDisposable
{
    private const int TextCacheSize = 48;

    private readonly Lock _gate = new();
    private readonly MuPDFContext _ctx;
    private readonly MuPDFDocument _doc;
    private readonly PageGeometry[] _pages;
    private readonly Dictionary<int, PageText> _textCache = [];
    private readonly LinkedList<int> _textLru = [];
    private readonly Dictionary<int, PageLink[]> _links = [];
    private IReadOnlyList<OutlineNode>? _outline;
    private bool _disposed;

    public int PageCount => _pages.Length;
    public bool IsEncrypted { get; }

    private PdfEngine(MuPDFContext ctx, MuPDFDocument doc, bool encrypted)
    {
        _ctx = ctx;
        _doc = doc;
        IsEncrypted = encrypted;
        _pages = new PageGeometry[doc.Pages.Count];
        for (var i = 0; i < _pages.Length; i++)
        {
            var b = doc.Pages[i].Bounds;
            _pages[i] = new PageGeometry(b.X0, b.Y0, b.Width, b.Height);
        }
    }

    /// <exception cref="PasswordRequiredException">The document is encrypted and the password is missing or wrong.</exception>
    public static PdfEngine Open(byte[] data, InputFileTypes type, string? password)
    {
        // MuPDF's resource cache (decoded images, fonts) defaults to 256 MB per document; 128 MB keeps re-renders fast
        // without letting several open tabs balloon.
        var ctx = new MuPDFContext(128 * 1024 * 1024);
        MuPDFDocument? doc = null;
        try
        {
            doc = new MuPDFDocument(ctx, data, type);
            var encrypted = doc.EncryptionState != EncryptionState.Unencrypted;
            if (doc.EncryptionState == EncryptionState.Encrypted)
            {
                if (string.IsNullOrEmpty(password)) throw new PasswordRequiredException(false);
                if (!doc.TryUnlock(password)) throw new PasswordRequiredException(true);
            }
            return new PdfEngine(ctx, doc, encrypted);
        }
        catch
        {
            if (doc is not null) DisposeDocument(doc);
            ctx.Dispose();
            throw;
        }
    }

    /// <summary>Opens a file in place (MuPDF reads only what it needs), e.g. for a quick preview. Null if it is encrypted.</summary>
    public static PdfEngine? OpenPreview(string path)
    {
        var ctx = new MuPDFContext(32 * 1024 * 1024);
        MuPDFDocument? doc = null;
        try
        {
            doc = new MuPDFDocument(ctx, path);
            if (doc.EncryptionState == EncryptionState.Encrypted)
            {
                DisposeDocument(doc);
                ctx.Dispose();
                return null;
            }
            return new PdfEngine(ctx, doc, false);
        }
        catch
        {
            if (doc is not null) DisposeDocument(doc);
            ctx.Dispose();
            throw;
        }
    }

    public static InputFileTypes TypeFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xps" or ".oxps" => InputFileTypes.XPS,
        ".epub" => InputFileTypes.EPUB,
        ".cbz" => InputFileTypes.CBZ,
        ".fb2" => InputFileTypes.FB2,
        ".mobi" => InputFileTypes.MOBI,
        ".png" => InputFileTypes.PNG,
        ".jpg" or ".jpeg" => InputFileTypes.JPEG,
        ".bmp" => InputFileTypes.BMP,
        ".gif" => InputFileTypes.GIF,
        ".tif" or ".tiff" => InputFileTypes.TIFF,
        _ => InputFileTypes.PDF,
    };

    public PageGeometry GetPage(int index) => _pages[index];

    /// <summary>Renders a page (or a region of it, in page points) at <paramref name="scale"/> pixels per point.</summary>
    public RenderedImage Render(int page, double scale, RectD? region = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var g = _pages[page];
            var r = region ?? new RectD(0, 0, g.Width, g.Height);
            var area = new Rectangle(g.OriginX + r.X, g.OriginY + r.Y, g.OriginX + r.Right, g.OriginY + r.Bottom);
            var px = area.Round(scale);
            var bytes = MuPDFDocument.GetRenderedSize(area, scale, PixelFormats.BGR);
            if (bytes != px.Width * px.Height * 3)
                throw new InvalidOperationException($"Unexpected render buffer size {bytes} for {px.Width}x{px.Height}.");
            var pixels = GC.AllocateUninitializedArray<byte>(bytes);
            unsafe
            {
                fixed (byte* p = pixels)
                    _doc.Render(page, area, scale, PixelFormats.BGR, (IntPtr)p, includeAnnotations: true);
            }
            return new RenderedImage(px.Width, px.Height, pixels);
        }
    }

    /// <summary>Renders a page straight to a PNG or JPEG file at the given resolution.</summary>
    public void SaveImage(int page, double dpi, string path, bool jpeg)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var scale = dpi / 72.0;
            if (jpeg) _doc.SaveImageAsJPEG(page, scale, path, 90, includeAnnotations: true);
            else _doc.SaveImage(page, scale, PixelFormats.RGB, path, RasterOutputFileTypes.PNG, includeAnnotations: true);
        }
    }

    /// <summary>The page's text layer (cached; cheap to call repeatedly from the UI).</summary>
    public PageText GetText(int page, bool cache = true)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_textCache.TryGetValue(page, out var hit))
            {
                _textLru.Remove(page);
                _textLru.AddFirst(page);
                return hit;
            }

            var text = ExtractText(page);
            if (cache)
            {
                _textCache[page] = text;
                _textLru.AddFirst(page);
                while (_textLru.Count > TextCacheSize)
                {
                    _textCache.Remove(_textLru.Last!.Value);
                    _textLru.RemoveLast();
                }
            }
            return text;
        }
    }

    private PageText ExtractText(int page)
    {
        var g = _pages[page];
        var lines = new List<List<(string, RectD)>>();
        using var st = _doc.GetStructuredTextPage(page, includeAnnotations: false);
        foreach (var block in st)
        {
            if (block is not MuPDFTextStructuredTextBlock tb) continue;
            foreach (var line in tb)
            {
                var chars = new List<(string, RectD)>();
                foreach (var ch in line)
                {
                    var q = ch.BoundingQuad;
                    var l = Math.Min(Math.Min(q.UpperLeft.X, q.LowerLeft.X), Math.Min(q.UpperRight.X, q.LowerRight.X));
                    var r = Math.Max(Math.Max(q.UpperLeft.X, q.LowerLeft.X), Math.Max(q.UpperRight.X, q.LowerRight.X));
                    var t = Math.Min(Math.Min(q.UpperLeft.Y, q.LowerLeft.Y), Math.Min(q.UpperRight.Y, q.LowerRight.Y));
                    var b = Math.Max(Math.Max(q.UpperLeft.Y, q.LowerLeft.Y), Math.Max(q.UpperRight.Y, q.LowerRight.Y));
                    chars.Add((ch.Character, RectD.FromEdges(l - g.OriginX, t - g.OriginY, r - g.OriginX, b - g.OriginY)));
                }
                lines.Add(chars);
            }
        }
        return new PageText(lines);
    }

    public IReadOnlyList<PageLink> GetLinks(int page)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_links.TryGetValue(page, out var cached)) return cached;

            var g = _pages[page];
            var list = new List<PageLink>();
            foreach (var link in _doc.Pages[page].Links)
            {
                var a = link.ActiveArea;
                var area = RectD.FromEdges(a.X0 - g.OriginX, a.Y0 - g.OriginY, a.X1 - g.OriginX, a.Y1 - g.OriginY);
                switch (link.Destination)
                {
                    case MuPDFInternalLinkDestination d when d.PageNumber >= 0 && d.PageNumber < _pages.Length:
                        list.Add(new PageLink(area, d.PageNumber, null));
                        break;
                    case MuPDFExternalLinkDestination e when !string.IsNullOrWhiteSpace(e.Uri):
                        list.Add(new PageLink(area, -1, e.Uri));
                        break;
                }
            }
            return _links[page] = [.. list];
        }
    }

    public IReadOnlyList<OutlineNode> GetOutline()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_outline is not null) return _outline;
            try
            {
                _outline = Convert(_doc.Outline);
            }
            catch (Exception ex) when (ex is MuPDFException or InvalidOperationException or NullReferenceException)
            {
                // A broken outline must not stop the document from opening.
                _outline = [];
            }
            return _outline;
        }

        static List<OutlineNode> Convert(IEnumerable<MuPDFOutlineItem>? items)
        {
            var list = new List<OutlineNode>();
            if (items is null) return list;
            foreach (var item in items)
                list.Add(new OutlineNode(item.Title ?? "", item.PageNumber, Convert(item.Children)));
            return list;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeDocument(_doc);
            _ctx.Dispose();
        }
    }

    /// <summary>
    /// MuPDFCore throws from a finalizer (crashing the process) if a page outlives its context, so pages must be
    /// disposed explicitly, before the document and the context.
    /// </summary>
    private static void DisposeDocument(MuPDFDocument doc)
    {
        doc.Pages.Dispose();
        doc.Dispose();
    }
}
