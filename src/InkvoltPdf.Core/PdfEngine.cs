using System.Reflection;
using System.Runtime.InteropServices;
using MuPDFCore;
using MuPDFCore.StructuredText;

namespace InkvoltPdf.Core;

/// <summary>An edit needs the document's permissions (owner) password: it was opened without it, or the one given is wrong.</summary>
public sealed class OwnerPasswordRequiredException(bool wrongPassword, Exception? inner = null)
    : Exception(wrongPassword ? "Incorrect permissions password." : "Editing this PDF needs its permissions (owner) password.", inner)
{
    public bool WrongPassword { get; } = wrongPassword;
}

public sealed class PasswordRequiredException(bool wrongPassword) : Exception(wrongPassword ? "Incorrect password." : "This document is password protected.")
{
    public bool WrongPassword { get; } = wrongPassword;
}

/// <summary>A bookmark. TargetY: where on the page it leads (page space, points from the top), when the destination says.</summary>
public sealed record OutlineNode(string Title, int PageIndex, IReadOnlyList<OutlineNode> Children, double? TargetY = null);

/// <summary>
/// A clickable area on a page: either an internal jump (PageIndex, plus TargetY in the target page's space when the
/// destination names a position) or an external URI.
/// </summary>
public sealed record PageLink(RectD Area, int PageIndex, string? Uri, double? TargetY = null);

/// <summary>Pixels from a render, packed BGR24 (tight stride = Width * 3).</summary>
public sealed record RenderedImage(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 3;

    /// <summary>The image turned clockwise by <paramref name="rotation"/> (0, 90, 180 or 270); itself for 0.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired (checked every few rows).</exception>
    public RenderedImage Rotated(int rotation, CancellationToken cancel = default)
    {
        rotation = RotatedView.Normalize(rotation);
        if (rotation == 0) return this;
        var (w, h) = rotation == 180 ? (Width, Height) : (Height, Width);
        var dst = GC.AllocateUninitializedArray<byte>(Pixels.Length);
        var src = Pixels;
        int sw = Width, sh = Height, dstStride = w * 3;
        for (var y = 0; y < sh; y++)
        {
            if ((y & 63) == 0) cancel.ThrowIfCancellationRequested();
            var s = y * sw * 3;
            for (var x = 0; x < sw; x++, s += 3)
            {
                // Clockwise: 90 sends (x, y) to (h' - 1 - y, x); 180 to (w - 1 - x, h - 1 - y); 270 to (y, w - 1 - x).
                var (dx, dy) = rotation switch
                {
                    90 => (sh - 1 - y, x),
                    180 => (sw - 1 - x, sh - 1 - y),
                    _ => (y, sw - 1 - x),
                };
                var d = dy * dstStride + dx * 3;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
            }
        }
        return new RenderedImage(w, h, dst);
    }
}

/// <summary>
/// One open document in MuPDF. MuPDF is not thread-safe per document, so every call takes a lock; callers may use
/// it from any thread (renders run on a background worker, hit-testing on the UI thread). Text, links and outline
/// come from a second MuPDF instance of the same file with its own lock, so the UI never waits behind a render.
/// Each instance keeps only the most recently used pages loaded (<see cref="LoadedPages"/>), so native memory stays
/// bounded however far a long document is scrolled.
/// </summary>
public sealed class PdfEngine : IDisposable
{
    private const int TextCacheSize = 48;

    /// <summary>MuPDF pages (with their display lists) kept loaded per instance; older ones are freed.</summary>
    internal const int LoadedPagesKept = 32;

    /// <summary>Largest render or image export allowed, in pixels (200 MP is 600 MB of BGR).</summary>
    public const long MaxPixels = 200_000_000;

    // Lock order: _gate before _textGate (only Dispose and the LoadedCounts test hook take both).
    private readonly Lock _gate = new();
    private readonly Lock _textGate = new();
    private readonly MuPDFContext _ctx;
    private readonly MuPDFDocument _doc;
    private readonly LoadedPages _loaded;     // under _gate
    private readonly Func<MuPDFContext, MuPDFDocument> _reopen;
    private readonly string? _password;
    private MuPDFContext? _textCtx;
    private MuPDFDocument? _textDoc;
    private LoadedPages? _textLoaded;         // under _textGate
    private readonly PageGeometry[] _pages;
    private readonly Dictionary<int, PageText> _textCache = [];
    private readonly LinkedList<int> _textLru = [];
    private readonly Dictionary<int, PageLink[]> _links = [];
    private IReadOnlyList<OutlineNode>? _outline;
    private volatile bool _disposed;

    public int PageCount => _pages.Length;
    public bool IsEncrypted { get; }

    /// <param name="reopen">Opens the same document again in another context (for the text instance).</param>
    private PdfEngine(MuPDFContext ctx, MuPDFDocument doc, bool encrypted, Func<MuPDFContext, MuPDFDocument> reopen, string? password)
    {
        _ctx = ctx;
        _doc = doc;
        IsEncrypted = encrypted;
        _reopen = reopen;
        _password = password;
        _loaded = new LoadedPages(doc, LoadedPagesKept);
        _pages = new PageGeometry[doc.Pages.Count];
        for (var i = 0; i < _pages.Length; i++)
        {
            _loaded.Touch(i); // reading every page's size loads it: keep only the last few
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
                if (!Unlock(doc, password)) throw new PasswordRequiredException(true);
            }
            return new PdfEngine(ctx, doc, encrypted, c => new MuPDFDocument(c, data, type), password);
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
            return new PdfEngine(ctx, doc, false, c => new MuPDFDocument(c, path), null);
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
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired (MuPDF stops mid-render).</exception>
    /// <exception cref="InvalidOperationException">The image would exceed <see cref="MaxPixels"/>.</exception>
    public RenderedImage Render(int page, double scale, RectD? region = null, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancel.ThrowIfCancellationRequested();
            _loaded.Touch(page);
            var g = _pages[page];
            var r = region ?? new RectD(0, 0, g.Width, g.Height);
            var area = new Rectangle(g.OriginX + r.X, g.OriginY + r.Y, g.OriginX + r.Right, g.OriginY + r.Bottom);
            var px = area.Round(scale);
            CheckSize(px.Width, px.Height);
            var bytes = MuPDFDocument.GetRenderedSize(area, scale, PixelFormats.BGR);
            if (bytes != px.Width * px.Height * 3)
                throw new InvalidOperationException($"Unexpected render buffer size {bytes} for {px.Width}x{px.Height}.");
            var pixels = GC.AllocateUninitializedArray<byte>(bytes);
            unsafe
            {
                fixed (byte* p = pixels)
                {
                    if (!cancel.CanBeCanceled || !AbortableRender.TryRender(_ctx, _doc, page, area, scale, (IntPtr)p, cancel))
                        _doc.Render(page, area, scale, PixelFormats.BGR, (IntPtr)p, includeAnnotations: true);
                    cancel.ThrowIfCancellationRequested();
                }
            }
            return new RenderedImage(px.Width, px.Height, pixels);
        }
    }

    /// <summary>Renders a page straight to a PNG or JPEG file at the given resolution.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired before the render started.</exception>
    /// <exception cref="InvalidOperationException">The image would exceed <see cref="MaxPixels"/>.</exception>
    public void SaveImage(int page, double dpi, string path, bool jpeg, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancel.ThrowIfCancellationRequested();
            var scale = dpi / 72.0;
            var g = _pages[page];
            CheckSize(Math.Ceiling(g.Width * scale), Math.Ceiling(g.Height * scale));
            _loaded.Touch(page);
            if (jpeg) _doc.SaveImageAsJPEG(page, scale, path, 90, includeAnnotations: true);
            else _doc.SaveImage(page, scale, PixelFormats.RGB, path, RasterOutputFileTypes.PNG, includeAnnotations: true);
        }
    }

    /// <summary>
    /// MuPDF renders stop early when their "cookie" says abort, but MuPDFCore's public Render passes no cookie (and the
    /// Abort of its multi-threaded renderer blocks until the render is done). This calls the native entry point
    /// MuPDFDocument.Render uses, the same way, with a cookie. Pinned to MuPDFCore 2.0.1's internals: if they differ,
    /// TryRender returns false and the caller renders without mid-render abort.
    /// </summary>
    private static class AbortableRender
    {
        private delegate int RenderSubDisplayList(IntPtr ctx, IntPtr list, float x0, float y0, float x1, float y1, float zoom, int format, IntPtr pixels, IntPtr cookie);

        private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly RenderSubDisplayList? Native;
        private static readonly FieldInfo? DisplayLists, NativeList, NativeContext, XRes, YRes;
        private static readonly ConstructorInfo? NewList;

        static AbortableRender()
        {
            try
            {
                var asm = typeof(MuPDFDocument).Assembly;
                var listType = asm.GetType("MuPDFCore.MuPDFDisplayList");
                var native = asm.GetType("MuPDFCore.NativeMethods")?.GetMethod("RenderSubDisplayList", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                DisplayLists = typeof(MuPDFDocument).GetField("DisplayLists", Internal);
                XRes = typeof(MuPDFDocument).GetField("ImageXRes", Internal);
                YRes = typeof(MuPDFDocument).GetField("ImageYRes", Internal);
                NativeContext = typeof(MuPDFContext).GetField("NativeContext", Internal);
                NativeList = listType?.GetField("NativeDisplayList", Internal);
                NewList = listType?.GetConstructor(Internal, [typeof(MuPDFContext), typeof(MuPDFPage), typeof(bool)]);
                if (native is null || DisplayLists?.FieldType.GetElementType() != listType || XRes?.FieldType != typeof(double) || YRes?.FieldType != typeof(double) ||
                    NativeContext?.FieldType != typeof(IntPtr) || NativeList?.FieldType != typeof(IntPtr) || NewList is null)
                    return;
                Native = native.CreateDelegate<RenderSubDisplayList>();
            }
            catch (Exception ex) when (ex is ArgumentException or MemberAccessException or TypeLoadException)
            {
                Native = null;
            }
        }

        /// <summary>Renders like MuPDFDocument.Render (BGR, with annotations); false if it can't (then nothing was drawn).</summary>
        /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired; the pixels are incomplete.</exception>
        public static bool TryRender(MuPDFContext ctx, MuPDFDocument doc, int page, Rectangle area, double scale, IntPtr pixels, CancellationToken cancel)
        {
            if (Native is null || (double)XRes!.GetValue(doc)! != 72 || (double)YRes!.GetValue(doc)! != 72) return false;
            // Regions reaching outside the page get clipped by MuPDFCore after rendering: leave those to it.
            var b = doc.Pages[page].Bounds;
            if (area.X0 < b.X0 - 0.01 || area.Y0 < b.Y0 - 0.01 || area.X1 > b.X1 + 0.01 || area.Y1 > b.Y1 + 0.01) return false;
            if (DisplayLists!.GetValue(doc) is not Array lists) return false;
            var list = lists.GetValue(page);
            if (list is null)
            {
                list = NewList!.Invoke([ctx, doc.Pages[page], true]);
                lists.SetValue(list, page);
            }

            var cookie = Marshal.AllocHGlobal(64); // fz_cookie is 24 bytes on 64-bit
            try
            {
                for (var i = 0; i < 64; i += 4) Marshal.WriteInt32(cookie, i, 0);
                int result;
                using (cancel.Register(() => Marshal.WriteInt32(cookie, 0, 1)))
                {
                    result = Native((IntPtr)NativeContext!.GetValue(ctx)!, (IntPtr)NativeList!.GetValue(list)!,
                        area.X0, area.Y0, area.X1, area.Y1, (float)scale, (int)PixelFormats.BGR, pixels, cookie);
                }
                cancel.ThrowIfCancellationRequested();
                if (result != 0) throw new InvalidOperationException($"MuPDF could not render page {page + 1} (error {result}).");
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(cookie);
            }
        }
    }

    /// <summary>
    /// Unlocks an encrypted document. MuPDFCore leaves a document unlocked with its owner (permissions) password marked
    /// as encrypted, so every page access would still throw; MuPDF itself has accepted the password by then.
    /// </summary>
    private static bool Unlock(MuPDFDocument doc, string password)
    {
        if (!doc.TryUnlock(password, out var kind)) return false;
        if (doc.EncryptionState == EncryptionState.Encrypted && kind.HasFlag(PasswordTypes.Owner))
            typeof(MuPDFDocument).GetProperty(nameof(MuPDFDocument.EncryptionState))?.SetMethod?.Invoke(doc, [EncryptionState.Unlocked]);
        return doc.EncryptionState != EncryptionState.Encrypted;
    }

    private static void CheckSize(double width, double height)
    {
        if (width * height > MaxPixels)
            throw new InvalidOperationException($"The image would be too large ({width:0} x {height:0} pixels). Use a lower zoom or resolution.");
    }

    /// <summary>The text instance of the document, opened on first use. Call under _textGate.</summary>
    private MuPDFDocument TextDoc()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_textDoc is not null) return _textDoc;
        var ctx = new MuPDFContext(32 * 1024 * 1024);
        MuPDFDocument? doc = null;
        try
        {
            doc = _reopen(ctx);
            if (doc.EncryptionState == EncryptionState.Encrypted && !Unlock(doc, _password ?? ""))
                throw new PasswordRequiredException(true);
            if (doc.Pages.Count != _pages.Length) throw new InvalidOperationException("The document changed while open.");
        }
        catch
        {
            if (doc is not null) DisposeDocument(doc);
            ctx.Dispose();
            throw;
        }
        _textCtx = ctx;
        _textLoaded = new LoadedPages(doc, LoadedPagesKept);
        return _textDoc = doc;
    }

    /// <summary>The text instance with <paramref name="page"/> marked as just used. Call under _textGate.</summary>
    private MuPDFDocument TextDoc(int page)
    {
        var doc = TextDoc();
        _textLoaded!.Touch(page);
        return doc;
    }

    /// <summary>
    /// Takes the text lock. While another page's text is being read it waits, but gives up as soon as
    /// <paramref name="cancel"/> fires (a cancelled job doesn't queue up behind a long extraction).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired.</exception>
    private void EnterText(CancellationToken cancel)
    {
        if (!cancel.CanBeCanceled)
        {
            _textGate.Enter();
            return;
        }
        while (!_textGate.TryEnter(20)) cancel.ThrowIfCancellationRequested();
        if (!cancel.IsCancellationRequested) return;
        _textGate.Exit();
        cancel.ThrowIfCancellationRequested();
    }

    /// <summary>The page's text layer (cached; cheap to call repeatedly from the UI).</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired before the page was read.</exception>
    public PageText GetText(int page, bool cache = true, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        EnterText(cancel);
        try
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
        finally
        {
            _textGate.Exit();
        }
    }

    private PageText ExtractText(int page)
    {
        var g = _pages[page];
        var lines = new List<List<(string, RectD)>>();
        var directions = new List<(double, double)>();
        using var st = TextDoc(page).GetStructuredTextPage(page, includeAnnotations: false);
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
                directions.Add((line.Direction.X, line.Direction.Y)); // the baseline's direction (sideways text: vertical)
            }
        }
        return new PageText(lines, directions);
    }

    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired before the links were read.</exception>
    public IReadOnlyList<PageLink> GetLinks(int page, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        EnterText(cancel);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_links.TryGetValue(page, out var cached)) return cached;

            var g = _pages[page];
            var list = new List<PageLink>();
            foreach (var link in TextDoc(page).Pages[page].Links)
            {
                var a = link.ActiveArea;
                var area = RectD.FromEdges(a.X0 - g.OriginX, a.Y0 - g.OriginY, a.X1 - g.OriginX, a.Y1 - g.OriginY);
                switch (link.Destination)
                {
                    case MuPDFInternalLinkDestination d when d.PageNumber >= 0 && d.PageNumber < _pages.Length:
                        list.Add(new PageLink(area, d.PageNumber, null, TargetY(d.PageNumber, d.Y)));
                        break;
                    case MuPDFExternalLinkDestination e when !string.IsNullOrWhiteSpace(e.Uri):
                        list.Add(new PageLink(area, -1, e.Uri));
                        break;
                }
            }
            return _links[page] = [.. list];
        }
        finally
        {
            _textGate.Exit();
        }
    }

    /// <summary>
    /// MuPDFCore keeps every page it loads, and the display list made to render or read it, until the document is
    /// closed: scrolling through a 10,000-page file would hold them all in native memory. This keeps the most recently
    /// used pages of one MuPDF instance and frees the others (they load again when needed). Use under that instance's
    /// lock. Pinned to MuPDFCore 2.0.1's internals (the page and display list arrays); if they differ, it falls back to
    /// the public <see cref="MuPDFDocument.ClearCache"/> (display lists only) every <c>capacity</c> pages.
    /// </summary>
    internal sealed class LoadedPages(MuPDFDocument doc, int capacity)
    {
        private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly FieldInfo? PageArray = typeof(MuPDFPageCollection).GetField("Pages", Internal);
        private static readonly FieldInfo? ListArray = typeof(MuPDFDocument).GetField("DisplayLists", Internal);
        private readonly LinkedList<int> _lru = [];
        private readonly Dictionary<int, LinkedListNode<int>> _nodes = [];
        private int _evicted;

        public int Count => _lru.Count;

        /// <summary>Marks a page as just used; frees the least recently used one beyond the capacity.</summary>
        public void Touch(int page)
        {
            if (_nodes.TryGetValue(page, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return;
            }
            _nodes[page] = _lru.AddFirst(page);
            while (_lru.Count > capacity)
            {
                var old = _lru.Last!.Value;
                _lru.RemoveLast();
                _nodes.Remove(old);
                Free(old);
            }
        }

        private void Free(int page)
        {
            if (ListArray?.GetValue(doc) is Array lists && PageArray?.GetValue(doc.Pages) is Array pages
                && page < lists.Length && page < pages.Length)
            {
                (lists.GetValue(page) as IDisposable)?.Dispose();
                lists.SetValue(null, page);
                (pages.GetValue(page) as IDisposable)?.Dispose();
                pages.SetValue(null, page);
                return;
            }
            if (++_evicted % capacity == 0) doc.ClearCache();
        }

        /// <summary>Test hook: how many pages and display lists the instance holds right now (-1 if unknown).</summary>
        internal (int Pages, int Lists) Held()
        {
            if (ListArray?.GetValue(doc) is not Array lists || PageArray?.GetValue(doc.Pages) is not Array pages) return (-1, -1);
            int p = 0, l = 0;
            foreach (var x in pages) if (x is not null) p++;
            foreach (var x in lists) if (x is not null) l++;
            return (p, l);
        }
    }

    /// <summary>Test hook: MuPDF pages and display lists held by the render and text instances.</summary>
    internal ((int Pages, int Lists) Render, (int Pages, int Lists) Text) LoadedCounts()
    {
        lock (_gate)
        {
            lock (_textGate) return (_loaded.Held(), _textLoaded?.Held() ?? (0, 0));
        }
    }


    public IReadOnlyList<OutlineNode> GetOutline()
    {
        lock (_textGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_outline is not null) return _outline;
            try
            {
                _outline = Convert(TextDoc().Outline);
            }
            catch (Exception ex) when (ex is MuPDFException or InvalidOperationException or NullReferenceException)
            {
                // A broken outline must not stop the document from opening.
                _outline = [];
            }
            return _outline;
        }

        List<OutlineNode> Convert(IEnumerable<MuPDFOutlineItem>? items)
        {
            var list = new List<OutlineNode>();
            if (items is null) return list;
            foreach (var item in items)
                list.Add(new OutlineNode(item.Title ?? "", item.PageNumber, Convert(item.Children), TargetY(item.PageNumber, item.Location.Y)));
            return list;
        }
    }

    /// <summary>A destination's y (MuPDF page space) in our page space, or null when the destination names no position.</summary>
    private double? TargetY(int page, float y) =>
        page >= 0 && page < _pages.Length && float.IsFinite(y) ? Math.Clamp(y - _pages[page].OriginY, 0, _pages[page].Height) : null;

    public void Dispose()
    {
        lock (_gate)
        {
            lock (_textGate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_textDoc is not null) DisposeDocument(_textDoc);
                _textCtx?.Dispose();
                _textDoc = null;
                _textCtx = null;
            }
            DisposeDocument(_doc);
            _ctx.Dispose();
        }
    }

    /// <summary>
    /// MuPDFCore throws from a finalizer (crashing the process) if a page outlives its context, so pages must be
    /// disposed explicitly, before the document and the context.
    /// </summary>
    internal static void DisposeDocument(MuPDFDocument doc)
    {
        doc.Pages.Dispose();
        doc.Dispose();
    }
}
