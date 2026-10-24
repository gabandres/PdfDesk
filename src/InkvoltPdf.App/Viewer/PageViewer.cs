using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InkvoltPdf.Core;

namespace InkvoltPdf.App.Viewer;

public enum ViewLayout { SinglePage, Continuous, Facing, FacingCover }
public enum ZoomMode { Custom, FitWidth, FitPage }
public enum ViewerTool
{
    Hand, SelectText, Snapshot, AddText, AddCheck, AddCross, AddNote, Redact, PlaceSignature, AddSignature,
    // Pictures, links and stamps (see PageViewer.Signing, PageViewer.Links)
    AddImage, AddLink, AddStamp,
    // Drawing (see DrawTools)
    Pen, Highlighter, Rectangle, Ellipse, Line, Arrow, TextBox, Callout,
}

public sealed record SearchHit(int Page, int Start, int End, IReadOnlyList<RectD> Rects, string Excerpt)
{
    public string PageLabel => $"Page {Page + 1}";
}

/// <summary>A caret on a page: ordered by page, then caret.</summary>
public readonly record struct TextPos(int Page, int Caret) : IComparable<TextPos>
{
    public int CompareTo(TextPos other) => Page != other.Page ? Page.CompareTo(other.Page) : Caret.CompareTo(other.Caret);
}

/// <summary>
/// Scrollable, zoomable page view. Only pages near the viewport are rendered (on a background thread); pages far
/// from it hold no bitmaps. At high zoom a page is rendered once at a capped resolution plus a sharp "detail" tile
/// covering just the visible region, so memory stays bounded at any zoom.
/// </summary>
public sealed partial class PageViewer : ScrollViewer
{
    private const double Pad = 12;
    private const double PtToDip = 96.0 / 72.0;
    private const long MinFullPixels = 4_000_000;   // per-page base render cap at least; sharper detail comes from tiles
    private const long MinCachePixels = 24_000_000; // all cached page bitmaps together at least (~72 MB as BGR)
    private const double MinZoom = 0.05, MaxZoom = 64;
    private static readonly double[] ZoomSteps =
        [0.05, 0.0833, 0.125, 0.25, 0.3333, 0.5, 0.6667, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64];

    /// <summary>
    /// A rendered page or tile, turned by <paramref name="Rotation"/> (the view's rotation when it was made).
    /// <paramref name="PxX"/>/<paramref name="PxY"/> are MuPDF's rounded device origin of its first pixel before turning.
    /// </summary>
    private sealed record PageBitmap(BitmapSource Image, double Scale, RectD Region, int PxX, int PxY, int Rotation);

    /// <summary>A position on a page as shown (points from the turned page's top-left) and where it sat in the viewport.</summary>
    private readonly record struct Anchor(int Page, double Xpt, double Ypt, double ViewX, double ViewY);

    private readonly PageSurface _surface;
    private readonly Dictionary<int, PageBitmap> _full = [];
    private readonly Dictionary<int, PageBitmap> _detail = [];
    private readonly Dictionary<int, PageBitmap> _stale = []; // previous content, shown until the fresh render lands
    private readonly Dictionary<int, IReadOnlyList<PageLink>> _links = [];
    private readonly Dictionary<int, PageText> _texts = [];
    private readonly Stack<Anchor> _back = new();
    private readonly Stack<Anchor> _forward = new();
    private RenderScheduler? _scheduler;
    private PdfEngine? _engine;
    private Rect[] _rects = [];
    private double[] _rowTop = [], _rowBottom = []; // per page: the top and bottom of its row (sorted by Y in every layout)
    private int _singleIndex = -1;                  // single-page layout: the one laid-out page
    private double _zoom = 1;
    private ZoomMode _zoomMode = ZoomMode.FitWidth;
    private ViewLayout _layout = ViewLayout.Continuous;
    private ViewerTool _tool = ViewerTool.Hand;
    private int _currentPage;
    private bool _updateQueued;
    private int _generation;
    private int _viewRotation;                         // 0, 90, 180, 270: the view turned clockwise (the file is unchanged)
    private readonly HashSet<int> _failed = [];       // pages MuPDF couldn't render (until the document changes)
    private readonly HashSet<int> _failedTiles = [];  // pages whose detail tile failed (the base render stays)
    private readonly PaintLayer _layer;
    private bool _layerInTemplate;
    private Vector _paintOrigin;                       // while painting on the layer: the surface point at its top-left

    // Search
    private Dictionary<int, List<SearchHit>> _hitsByPage = [];
    private SearchHit? _activeHit;

    // Mouse state
    private Point _pressViewer;
    private Vector _pressOffset;
    private bool _panning, _selecting, _dragged;
    private PageLink? _pressLink;
    private int _pressLinkPage = -1;
    private TextPos? _selAnchor, _selActive;
    private int _snapPage = -1;
    private Point _snapStartPt, _snapEndPt;

    public event EventHandler? CurrentPageChanged;
    public event EventHandler? ZoomChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler? ToolChanged;
    public event EventHandler<string>? StatusMessage;

    public PageViewer()
    {
        _surface = new PageSurface(this);
        _layer = new PaintLayer(this);
        // The surface is the scrolled content (sized to the whole layout) and takes the mouse; inline editors (form
        // fields, Fill & Sign text) live on a canvas above it. Pages are painted by a viewport-sized layer under the
        // scrolled content (see OnApplyTemplate), so drawing never uses the huge coordinates of a long document.
        var host = new Grid();
        host.Children.Add(_surface);
        host.Children.Add(_overlay);
        Content = host;
        Focusable = true;
        FocusVisualStyle = null;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        CanContentScroll = false;
        RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_layer, BitmapScalingMode.HighQuality);
        _overlay.LayoutUpdated += (_, _) => TurnEditor();
        ApplyContrast();
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            ApplyContrast();
        };
        Unloaded += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _surface.MouseLeave += (_, _) => HideLinkTip();

        IsManipulationEnabled = true; // hand tool; switched off for the other tools so touch acts as a mouse
        ManipulationStarting += (_, e) =>
        {
            e.ManipulationContainer = this;
            e.Mode = ManipulationModes.Scale | ManipulationModes.Translate;
            e.Handled = true;
        };
        ManipulationDelta += (_, e) =>
        {
            var d = e.DeltaManipulation;
            var scale = (d.Scale.X + d.Scale.Y) / 2;
            if (Math.Abs(scale - 1) > 0.001) SetZoom(_zoom * scale, e.ManipulationOrigin);
            ScrollToHorizontalOffset(HorizontalOffset - d.Translation.X);
            ScrollToVerticalOffset(VerticalOffset - d.Translation.Y);
            e.Handled = true;
        };
        ManipulationCompleted += (_, e) =>
        {
            // A tap (no movement) clicks: follow a link or fill a field.
            if (e.TotalManipulation.Translation.Length > 6 || Math.Abs(e.TotalManipulation.Scale.X - 1) > 0.02) return;
            var sp = TranslatePoint(e.ManipulationOrigin, _surface);
            var page = HitPage(sp);
            if (page < 0) return;
            var pt = ToPagePoint(page, sp);
            if (HandleFormClick(page, pt, 1)) return;
            if (LinkAt(page, pt) is { } link) ActivateLink(link);
        };
        ManipulationInertiaStarting += (_, e) =>
        {
            e.TranslationBehavior.DesiredDeceleration = 0.003;
            e.Handled = true;
        };

        _surface.MouseLeftButtonDown += OnSurfaceDown;
        _surface.MouseMove += OnSurfaceMove;
        _surface.MouseLeftButtonUp += OnSurfaceUp;
        _surface.LostMouseCapture += (_, _) => EndDrag();
        InitDrawing();
        ScrollChanged += (_, e) =>
        {
            // ScrollChanged bubbles: an inline editor's own text scroller (a TextBox growing or turning) isn't the view.
            if (!ReferenceEquals(e.OriginalSource, this)) return;
            // Viewport size changes (window resize, nav pane, scrollbars appearing) re-fit and re-center the pages.
            if (Math.Abs(e.ViewportWidthChange) > 0.5 || Math.Abs(e.ViewportHeightChange) > 0.5) QueueRelayout();
            if (Math.Abs(e.VerticalChange) > 0.5 || Math.Abs(e.HorizontalChange) > 0.5) HideLinkTip();
            UpdateCurrentPage();
            _surface.InvalidateVisual();
            QueueUpdate();
        };
    }

    public PdfEngine? Engine => _engine;

    private bool _invert;

    /// <summary>Night reading: pages are shown with inverted colors (white paper becomes black).</summary>
    public bool InvertColors
    {
        get => _invert;
        set
        {
            if (_invert == value) return;
            var was = EffectiveInvert;
            _invert = value;
            if (was != EffectiveInvert) DropPageBitmaps();
        }
    }

    /// <summary>Night reading is off in Windows high-contrast mode (the system colors decide there).</summary>
    private bool EffectiveInvert => _invert && !_highContrast;

    private void DropPageBitmaps()
    {
        _generation++;
        _full.Clear();
        _detail.Clear();
        _stale.Clear();
        _scheduler?.Clear(k => k[0] is 'p' or 'd');
        _surface.InvalidateVisual();
        QueueUpdate();
    }
    public int PageCount => _engine?.PageCount ?? 0;
    public int CurrentPage => _currentPage;
    public double Zoom => _zoom;
    public ZoomMode ZoomMode => _zoomMode;
    public ViewLayout Layout => _layout;
    public bool CanGoBack => _back.Count > 0;

    internal string Diagnostics => $"vp={ViewportWidth:0}x{ViewportHeight:0} actual={ActualWidth:0}x{ActualHeight:0} extent={ExtentWidth:0}x{ExtentHeight:0} " +
        $"off={HorizontalOffset:0},{VerticalOffset:0} rect={(_currentPage < _rects.Length ? $"{_rects[_currentPage].X:0},{_rects[_currentPage].Y:0} {_rects[_currentPage].Width:0}x{_rects[_currentPage].Height:0}" : "-")}";
    public bool CanGoForward => _forward.Count > 0;

    public ViewerTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            CommitEditor();
            _tool = value;
            IsManipulationEnabled = value == ViewerTool.Hand;
            if (value != ViewerTool.SelectText && !_caretBrowsing) ClearSelection();
            OnDrawToolChanged();
            if (value != ViewerTool.AddLink) _selectedLink = null;
            UpdateCursor(Mouse.GetPosition(_surface));
            ToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Attaches a (new) engine. With <paramref name="keepPosition"/> the view stays on the same page and zoom.
    /// <paramref name="contentOnly"/>: the edit only changed form field values or Fill &amp; Sign items (pages, their
    /// order, sizes and text are unchanged), so the open field editor, fields, links, text selection and history stay.
    /// Otherwise fields and Fill &amp; Sign items are dropped until <see cref="SetFormWidgets"/> / <see cref="SetStamps"/>
    /// deliver the new ones, so clicks can't land on a field that belonged to a moved or deleted page.
    /// </summary>
    public void SetEngine(PdfEngine engine, RenderScheduler scheduler, bool keepPosition, bool contentOnly = false)
    {
        var anchor = keepPosition && _engine is not null ? CaptureAnchor() : (Anchor?)null;
        contentOnly &= anchor is not null && _rects.Length == engine.PageCount;
        if (!contentOnly)
        {
            // Save what the editor holds (by field name, so it lands on the right field wherever it moved) and close it.
            CommitEditor();
            CloseEditor();
            ClearFormState();
            ResetReview();
        }
        _engine = engine;
        _scheduler = scheduler;
        _generation++;
        scheduler.Clear(k => !k.StartsWith('t'));
        HideLinkTip();
        _stale.Clear();
        if (contentOnly)
            foreach (var (page, bmp) in _full) _stale[page] = bmp; // same pages: show the old look until the re-render lands
        _full.Clear();
        _detail.Clear();
        _failed.Clear();
        _failedTiles.Clear();
        if (!contentOnly)
        {
            _links.Clear();
            _texts.Clear();
            _selAnchor = _selActive = null;
            _back.Clear();
            _forward.Clear();
        }
        _currentPage = Math.Clamp(anchor?.Page ?? 0, 0, Math.Max(0, engine.PageCount - 1));
        if (anchor is { } a)
            Relayout(a with { Page = _currentPage });
        else
        {
            Relayout(null);
            ScrollToVerticalOffset(0);
        }
        RaiseCurrentPageChanged();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── Layout ─────────────────────────────

    public void SetLayout(ViewLayout layout)
    {
        if (_layout == layout) return;
        var anchor = CurrentPageAnchor();
        _layout = layout;
        Relayout(anchor);
    }

    /// <summary>How far the view is turned clockwise: 0, 90, 180 or 270 (the file is unchanged; thumbnails aren't turned).</summary>
    public int ViewRotation => _viewRotation;

    /// <summary>Turns the view (any multiple of 90°), keeping the spot at the viewport's center in place.</summary>
    public void SetViewRotation(int degrees)
    {
        degrees = RotatedView.Normalize(degrees);
        if (degrees == _viewRotation) return;
        HideLinkTip();
        // The page point at the viewport's center, then where it is on the page turned the new way.
        Anchor? anchor = null;
        var center = new Point(ViewW / 2, ViewH / 2);
        var sp = new Point(center.X + HorizontalOffset, center.Y + VerticalOffset);
        var page = _engine is null || _rects.Length == 0 ? -1 : HitPage(sp);
        if (page < 0 && _engine is not null && _rects.Length > 0) page = NearestPage(sp);
        if (page >= 0 && !_rects[page].IsEmpty)
        {
            var g = _engine!.GetPage(page);
            var pt = ToPagePoint(page, sp);
            var (vx, vy) = RotatedView.ToView(Math.Clamp(pt.X, 0, g.Width), Math.Clamp(pt.Y, 0, g.Height), g.Width, g.Height, degrees);
            anchor = new Anchor(page, vx, vy, center.X, center.Y);
        }
        _viewRotation = degrees;
        DropPageBitmaps(); // renders are turned pixel by pixel: the old ones no longer fit
        Relayout(anchor ?? CurrentPageAnchor());
    }

    /// <summary>Turns the view a quarter turn clockwise (<paramref name="clockwise"/>) or counterclockwise.</summary>
    public void RotateView(bool clockwise) => SetViewRotation(_viewRotation + (clockwise ? 90 : -90));

    public void SetZoomMode(ZoomMode mode)
    {
        _zoomMode = mode;
        Relayout(CurrentPageAnchor());
        if (mode == ZoomMode.FitPage) GoToPage(_currentPage, record: false);
    }

    public void SetZoom(double zoom, Point? viewportPoint = null)
    {
        _zoomMode = ZoomMode.Custom;
        var anchor = CaptureAnchor(viewportPoint);
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Relayout(anchor);
    }

    public void ZoomStep(int direction, Point? viewportPoint = null)
    {
        var next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > _zoom * 1.01, MaxZoom)
            : ZoomSteps.LastOrDefault(z => z < _zoom / 1.01, MinZoom);
        SetZoom(next, viewportPoint);
    }

    private double ViewW => ViewportWidth > 0 ? ViewportWidth : Math.Max(0, ActualWidth - SystemParameters.VerticalScrollBarWidth);
    private double ViewH => ViewportHeight > 0 ? ViewportHeight : ActualHeight;
    private bool IsFacing => _layout is ViewLayout.Facing or ViewLayout.FacingCover;

    /// <summary>Pages sharing a row with <paramref name="page"/> (facing layouts pair them).</summary>
    private (int First, int Last) RowOf(int page)
    {
        if (!IsFacing) return (page, page);
        if (_layout == ViewLayout.FacingCover)
        {
            if (page == 0) return (0, 0);
            var first = page % 2 == 1 ? page : page - 1;
            return (first, Math.Min(first + 1, PageCount - 1));
        }
        var f = page - page % 2;
        return (f, Math.Min(f + 1, PageCount - 1));
    }

    private double FitZoom()
    {
        if (_engine is null || PageCount == 0) return 1;
        var (first, last) = RowOf(Math.Clamp(_currentPage, 0, PageCount - 1));
        double w = 0, h = 0;
        for (var i = first; i <= last; i++)
        {
            var g = Shown(i);
            w = IsFacing ? Math.Max(w, g.Width) : g.Width;
            h = Math.Max(h, g.Height);
        }
        var rowW = IsFacing ? 2 * w * PtToDip : w * PtToDip;
        var availW = ViewW - 2 * Pad - (IsFacing ? Pad : 0);
        var zw = availW / Math.Max(1, rowW);
        if (_zoomMode == ZoomMode.FitWidth) return zw;
        var zh = (ViewH - 2 * Pad) / Math.Max(1, h * PtToDip);
        return Math.Min(zw, zh);
    }

    private void Relayout(Anchor? anchor)
    {
        if (_engine is null) return;
        if (_zoomMode != ZoomMode.Custom && ViewW > 0)
            _zoom = Math.Clamp(FitZoom(), MinZoom, MaxZoom);

        var n = PageCount;
        if (n == 0)
        {
            _rects = [];
            _rowTop = _rowBottom = [];
            _singleIndex = -1;
            _surface.SetExtent(ViewW, ViewH);
            _surface.InvalidateVisual();
            return;
        }
        if (_layout == ViewLayout.SinglePage && anchor is { } sa && sa.Page != _currentPage && sa.Page >= 0 && sa.Page < n)
        {
            _currentPage = sa.Page;
            RaiseCurrentPageChanged();
        }
        var k = _zoom * PtToDip;
        var dpi = DpiScale;
        double Snap(double v) => Math.Round(v * dpi) / dpi;

        _rects = new Rect[n];
        for (var i = 0; i < n; i++) _rects[i] = Rect.Empty;

        double extentW, extentH;
        if (_layout == ViewLayout.SinglePage)
        {
            var p = Math.Clamp(_currentPage, 0, n - 1);
            var g = Shown(p);
            var w = g.Width * k;
            var h = g.Height * k;
            extentW = Math.Max(ViewW, w + 2 * Pad);
            extentH = Math.Max(ViewH, h + 2 * Pad);
            _rects[p] = new Rect(Snap((extentW - w) / 2), Snap(Math.Max(Pad, (extentH - h) / 2)), w, h);
        }
        else if (!IsFacing)
        {
            var maxW = 0.0;
            for (var i = 0; i < n; i++) maxW = Math.Max(maxW, Shown(i).Width * k);
            extentW = Math.Max(ViewW, maxW + 2 * Pad);
            var y = Pad;
            for (var i = 0; i < n; i++)
            {
                var g = Shown(i);
                var w = g.Width * k;
                var h = g.Height * k;
                _rects[i] = new Rect(Snap((extentW - w) / 2), Snap(y), w, h);
                y += h + Pad;
            }
            extentH = Math.Max(ViewH, y);
        }
        else
        {
            var half = 0.0;
            for (var i = 0; i < n; i++) half = Math.Max(half, Shown(i).Width * k);
            extentW = Math.Max(ViewW, 2 * half + 3 * Pad);
            var cx = extentW / 2;
            var y = Pad;
            for (var i = 0; i < n;)
            {
                var (first, last) = RowOf(i);
                var rowH = 0.0;
                for (var j = first; j <= last; j++) rowH = Math.Max(rowH, Shown(j).Height * k);
                for (var j = first; j <= last; j++)
                {
                    var g = Shown(j);
                    var w = g.Width * k;
                    var h = g.Height * k;
                    // Cover page and odd-numbered pages sit on the right, like a printed book.
                    var right = _layout == ViewLayout.FacingCover ? j % 2 == 0 : j % 2 == 1;
                    if (first == last && _layout == ViewLayout.Facing) right = false;
                    var x = right ? cx + Pad / 2 : cx - Pad / 2 - w;
                    _rects[j] = new Rect(Snap(x), Snap(y + (rowH - h) / 2), w, h);
                }
                y += rowH + Pad;
                i = last + 1;
            }
            extentH = Math.Max(ViewH, y);
        }

        _singleIndex = _layout == ViewLayout.SinglePage ? Math.Clamp(_currentPage, 0, n - 1) : -1;
        _rowTop = new double[n];
        _rowBottom = new double[n];
        for (var i = 0; i < n;)
        {
            var (first, last) = RowOf(i);
            double top = double.PositiveInfinity, bottom = double.NegativeInfinity;
            for (var j = first; j <= last; j++)
                if (!_rects[j].IsEmpty)
                {
                    top = Math.Min(top, _rects[j].Top);
                    bottom = Math.Max(bottom, _rects[j].Bottom);
                }
            for (var j = first; j <= last; j++)
            {
                _rowTop[j] = top;
                _rowBottom[j] = bottom;
            }
            i = last + 1;
        }

        _surface.SetExtent(extentW, extentH);
        UpdateLayout();
        if (anchor is { } a) RestoreAnchor(a);
        CenterCurrentRow();
        UpdateLayout(); // apply the scroll now, so the current page is computed from the new offsets
        UpdateCurrentPage();
        PositionEditor();
        _surface.InvalidateVisual();
        QueueUpdate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool _relayoutQueued;

    private void QueueRelayout()
    {
        if (_relayoutQueued) return;
        _relayoutQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _relayoutQueued = false;
            Relayout(CaptureAnchor());
        });
    }

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Relayout(CaptureAnchor());
    }

    /// <summary>When the current page (or facing pair) is narrower than the window, center it horizontally.</summary>
    private void CenterCurrentRow()
    {
        if (_rects.Length == 0) return;
        var (first, last) = RowOf(Math.Clamp(_currentPage, 0, _rects.Length - 1));
        var row = Rect.Empty;
        for (var i = first; i <= last; i++) row.Union(_rects[i]);
        if (row.IsEmpty || row.Width + 2 * Pad > ViewW) return;
        ScrollToHorizontalOffset(row.X + row.Width / 2 - ViewW / 2);
    }

    /// <summary>The current page's position relative to the viewport's top edge (for layout and fit changes).</summary>
    private Anchor CurrentPageAnchor()
    {
        var page = Math.Clamp(_currentPage, 0, Math.Max(0, PageCount - 1));
        if (_engine is null || page >= _rects.Length || _rects[page].IsEmpty) return new Anchor(page, 0, 0, 0, Pad);
        var r = _rects[page];
        var k = _zoom * PtToDip;
        var top = Math.Max(0, (VerticalOffset - r.Y) / k); // keep the part of the page that was at the top in view
        return new Anchor(page, 0, top, r.X - HorizontalOffset, top > 0 ? 0 : r.Y - VerticalOffset);
    }

    private Anchor CaptureAnchor(Point? viewportPoint = null)
    {
        var vp = viewportPoint ?? new Point(ViewW / 2, 0);
        if (_engine is null || _rects.Length == 0) return new Anchor(0, 0, 0, vp.X, vp.Y);
        var sp = new Point(vp.X + HorizontalOffset, vp.Y + VerticalOffset);
        var page = HitPage(sp);
        if (page < 0) page = NearestPage(sp);
        if (page < 0) return new Anchor(_currentPage, 0, 0, vp.X, vp.Y);
        var r = _rects[page];
        var k = _zoom * PtToDip;
        return new Anchor(page, (sp.X - r.X) / k, (sp.Y - r.Y) / k, vp.X, vp.Y);
    }

    private void RestoreAnchor(Anchor a)
    {
        if (a.Page < 0 || a.Page >= _rects.Length) return;
        if (_rects[a.Page].IsEmpty)
        {
            _currentPage = a.Page;
            return;
        }
        var r = _rects[a.Page];
        var k = _zoom * PtToDip;
        ScrollToHorizontalOffset(r.X + a.Xpt * k - a.ViewX);
        ScrollToVerticalOffset(r.Y + a.Ypt * k - a.ViewY);
    }

    // ───────────────────────────── Navigation ─────────────────────────────

    /// <summary>Scrolls to a page (optionally to a y position in points). Records history for Back/Forward.</summary>
    public void GoToPage(int page, double? yPt = null, bool record = true)
    {
        if (_engine is null || PageCount == 0) return;
        page = Math.Clamp(page, 0, PageCount - 1);
        if (record) PushHistory();

        if (_layout == ViewLayout.SinglePage && page != _currentPage)
        {
            _currentPage = page;
            Relayout(null);
            RaiseCurrentPageChanged();
        }
        else if (_zoomMode == ZoomMode.FitPage && RowOf(page) != RowOf(_currentPage))
        {
            _currentPage = page;
            Relayout(null);
        }

        var r = _rects[page];
        if (r.IsEmpty) return;
        var (first, last) = RowOf(page);
        var rowTop = r.Y;
        for (var i = first; i <= last; i++) rowTop = Math.Min(rowTop, _rects[i].Y);
        // On a turned view the text below a target y isn't below it on screen: show the page from its top instead.
        ScrollToVerticalOffset(yPt is { } y && _viewRotation == 0 ? ToSurfacePoint(page, 0, y).Y - Pad : rowTop - Pad);
        if (r.Width + 2 * Pad > ViewW) ScrollToHorizontalOffset(r.X - Pad);
        UpdateLayout();
        if (_currentPage != page)
        {
            _currentPage = page;
            RaiseCurrentPageChanged();
        }
    }

    public void NextPage() => GoToPage(IsFacing ? RowOf(_currentPage).Last + 1 : _currentPage + 1, record: false);
    public void PreviousPage() => GoToPage(IsFacing ? RowOf(Math.Max(0, RowOf(_currentPage).First - 1)).First : _currentPage - 1, record: false);

    private void PushHistory()
    {
        if (_engine is null) return;
        _back.Push(CaptureAnchor(new Point(0, 0)));
        _forward.Clear();
    }

    public void GoBack() => Travel(_back, _forward);
    public void GoForward() => Travel(_forward, _back);

    private void Travel(Stack<Anchor> from, Stack<Anchor> to)
    {
        if (from.Count == 0) return;
        to.Push(CaptureAnchor(new Point(0, 0)));
        var a = from.Pop();
        if (a.Page >= PageCount) return;
        GoToPage(a.Page, record: false);
        RestoreAnchor(a);
    }

    private void UpdateCurrentPage()
    {
        if (_engine is null || _layout == ViewLayout.SinglePage) return;
        var vp = Viewport;
        var best = -1;
        var bestArea = 0.0;
        foreach (var i in VisiblePages(vp))
        {
            var inter = Rect.Intersect(vp, _rects[i]);
            var area = inter.IsEmpty ? 0 : inter.Width * inter.Height;
            if (area > bestArea + 1)
            {
                bestArea = area;
                best = i;
            }
        }
        // Keep the current page while it is (one of) the most visible, so facing pairs don't flip-flop.
        if (best >= 0 && best != _currentPage && _currentPage < _rects.Length && !_rects[_currentPage].IsEmpty)
        {
            var cur = Rect.Intersect(vp, _rects[_currentPage]);
            if (!cur.IsEmpty && cur.Width * cur.Height >= bestArea - 1) return;
        }
        if (best >= 0 && best != _currentPage)
        {
            _currentPage = best;
            RaiseCurrentPageChanged();
        }
    }

    private Rect Viewport => new(HorizontalOffset, VerticalOffset, ViewW, ViewH);

    private IEnumerable<int> VisiblePages(Rect vp)
    {
        var n = Math.Min(_rects.Length, _rowTop.Length);
        if (_singleIndex >= 0)
        {
            if (_singleIndex < _rects.Length && !_rects[_singleIndex].IsEmpty && _rects[_singleIndex].IntersectsWith(vp)) yield return _singleIndex;
            yield break;
        }
        // Rows are sorted by Y in every layout (pages within a facing row may differ in height, so search by row bounds).
        var lo = 0;
        var hi = n;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_rowBottom[mid] < vp.Top) lo = mid + 1;
            else hi = mid;
        }
        for (var i = lo; i < n; i++)
        {
            if (_rowTop[i] > vp.Bottom) break;
            var r = _rects[i];
            if (!r.IsEmpty && r.IntersectsWith(vp)) yield return i;
        }
    }

    private int HitPage(Point sp)
    {
        foreach (var i in VisiblePages(new Rect(sp.X, sp.Y, 1, 1)))
            if (_rects[i].Contains(sp)) return i;
        return -1;
    }

    private int NearestPage(Point sp)
    {
        var best = -1;
        var bestD = double.MaxValue;
        var vp = Viewport;
        vp.Inflate(vp.Width, vp.Height);
        foreach (var i in VisiblePages(vp))
        {
            var r = _rects[i];
            var dx = sp.X < r.Left ? r.Left - sp.X : sp.X > r.Right ? sp.X - r.Right : 0;
            var dy = sp.Y < r.Top ? r.Top - sp.Y : sp.Y > r.Bottom ? sp.Y - r.Bottom : 0;
            var d = dx * dx + dy * dy;
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>A page's size as shown: turned by the view rotation (points).</summary>
    private PageGeometry Shown(int page)
    {
        var g = _engine!.GetPage(page);
        return _viewRotation is 90 or 270 ? g with { Width = g.Height, Height = g.Width } : g;
    }

    // Page space ↔ surface. Everything that hit-tests or draws on a page goes through these, so the view rotation
    // applies to all of it. While the paint layer paints, surface results are relative to its top-left (_paintOrigin).

    /// <summary>A surface point in the page's own space (points from its unturned top-left).</summary>
    private Point ToPagePoint(int page, Point sp)
    {
        var r = _rects[page];
        var k = _zoom * PtToDip;
        var g = _engine!.GetPage(page);
        var (x, y) = RotatedView.FromView((sp.X - r.X) / k, (sp.Y - r.Y) / k, g.Width, g.Height, _viewRotation);
        return new Point(x, y);
    }

    private Point ToSurfacePoint(int page, double x, double y)
    {
        var r = _rects[page];
        var k = _zoom * PtToDip;
        var g = _engine!.GetPage(page);
        var (vx, vy) = RotatedView.ToView(x, y, g.Width, g.Height, _viewRotation);
        return new Point(r.X + vx * k - _paintOrigin.X, r.Y + vy * k - _paintOrigin.Y);
    }

    private Rect ToSurfaceRect(int page, RectD pt)
    {
        var r = _rects[page];
        var k = _zoom * PtToDip;
        var g = _engine!.GetPage(page);
        var v = RotatedView.ToView(pt, g.Width, g.Height, _viewRotation);
        return new Rect(r.X + v.X * k - _paintOrigin.X, r.Y + v.Y * k - _paintOrigin.Y, v.Width * k, v.Height * k);
    }

    /// <summary>The page-space area under a surface rectangle (clipped to the page).</summary>
    private RectD ToPageRect(int page, Rect sr)
    {
        var a = ToPagePoint(page, sr.TopLeft);
        var b = ToPagePoint(page, sr.BottomRight);
        var g = _engine!.GetPage(page);
        return RectD.FromEdges(a.X, a.Y, b.X, b.Y).Intersect(new RectD(0, 0, g.Width, g.Height));
    }

    // ───────────────────────────── Rendering ─────────────────────────────

    private void QueueUpdate()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _updateQueued = false;
            UpdateRenders();
        });
    }

    /// <summary>
    /// Base render cap per page: twice the screen's pixels (so a page filling the screen is sharp at once, even on a
    /// high-DPI display), at least 4 MP. Sharper detail at higher zoom comes from tiles of the visible part.
    /// </summary>
    private long MaxFullPixels
    {
        get
        {
            var dpi = DpiScale;
            var screen = Math.Max(SystemParameters.PrimaryScreenWidth * SystemParameters.PrimaryScreenHeight, ViewW * ViewH) * dpi * dpi;
            return Math.Max(MinFullPixels, (long)(2 * screen));
        }
    }

    private void UpdateRenders()
    {
        if (_engine is null || _scheduler is null || _rects.Length == 0) return;
        var engine = _engine;
        var gen = _generation;
        var invert = EffectiveInvert;
        var rotation = _viewRotation;
        var vp = Viewport;
        var visible = VisiblePages(vp).ToList();
        if (visible.Count == 0) return;

        var wanted = new List<int>(visible);
        if (visible[0] > 0) wanted.Add(visible[0] - 1);
        if (visible[^1] < PageCount - 1) wanted.Add(visible[^1] + 1);

        var scale = _zoom * PtToDip * DpiScale; // device pixels per point
        var center = vp.Top + vp.Height / 2;
        var maxFull = MaxFullPixels;
        var cacheBudget = Math.Max(MinCachePixels, 3 * maxFull); // the visible pages and their neighbours at the cap

        foreach (var page in wanted)
        {
            var g = engine.GetPage(page);
            var isVisible = visible.Contains(page);
            var dist = Math.Abs((_rects[page].Top + _rects[page].Bottom) / 2 - center) / Math.Max(1, vp.Height);
            var priority = (isVisible ? 0 : 50) + Math.Min(dist, 40); // always on the scheduler's view lane

            var fullScale = scale;
            var px = g.Width * g.Height * scale * scale;
            if (px > maxFull) fullScale = Math.Sqrt(maxFull / (g.Width * g.Height));

            if (_failed.Contains(page))
            {
                // MuPDF couldn't render it: the placeholder stays until the document changes (no retry on every scroll).
            }
            else if (!_full.TryGetValue(page, out var have) || Math.Abs(have.Scale - fullScale) > 1e-6 || have.Rotation != rotation)
            {
                var p = page;
                var s = fullScale;
                var whole = new RectD(0, 0, g.Width, g.Height);
                var (ox, oy) = PixelOrigin(g, whole, s);
                _scheduler.Enqueue($"p{page}", priority, ct => ToBitmap(engine.Render(p, s, null, ct).Rotated(rotation, ct), invert, ct), result =>
                {
                    if (gen != _generation || result is not BitmapSource bmp) return;
                    _full[p] = new PageBitmap(bmp, s, whole, ox, oy, rotation);
                    _stale.Remove(p);
                    _surface.InvalidateVisual();
                }, ex =>
                {
                    if (gen != _generation || ex is ObjectDisposedException) return; // the document changed meanwhile
                    _failed.Add(p);
                    _stale.Remove(p);
                    _surface.InvalidateVisual();
                }, tag: (s, gen));
            }

            if (fullScale < scale && isVisible && !_failed.Contains(page) && !_failedTiles.Contains(page))
            {
                // Sharp tile for the visible part of the page (plus a margin so small scrolls stay sharp).
                var r = _rects[page];
                var margin = new Rect(vp.X - vp.Width / 4, vp.Y - vp.Height / 4, vp.Width * 1.5, vp.Height * 1.5);
                var inter = Rect.Intersect(margin, r);
                var vis = Rect.Intersect(vp, r);
                if (!inter.IsEmpty && !vis.IsEmpty)
                {
                    var region = ToPageRect(page, inter);
                    var visPt = ToPageRect(page, vis);
                    var ok = _detail.TryGetValue(page, out var d) && Math.Abs(d.Scale - scale) < 1e-6 && d.Rotation == rotation
                             && d.Region.Inflate(0.5).Contains(visPt.X, visPt.Y) && d.Region.Inflate(0.5).Contains(visPt.Right, visPt.Bottom);
                    if (!ok && !region.IsEmpty && !TileCovers(page, scale, visPt))
                    {
                        var p = page;
                        var s = scale;
                        var (ox, oy) = PixelOrigin(g, region, s);
                        _pendingTiles[p] = (s, region, gen);
                        _scheduler.Enqueue($"d{page}", priority + 0.5, ct => ToBitmap(engine.Render(p, s, region, ct).Rotated(rotation, ct), invert, ct), result =>
                        {
                            if (gen != _generation || result is not BitmapSource bmp) return;
                            _pendingTiles.Remove(p);
                            _detail[p] = new PageBitmap(bmp, s, region, ox, oy, rotation);
                            _surface.InvalidateVisual();
                        }, ex =>
                        {
                            _pendingTiles.Remove(p);
                            if (gen == _generation && ex is not ObjectDisposedException) _failedTiles.Add(p); // keep the base render; don't retry
                        }, tag: (s, region, gen));
                    }
                }
            }
            else
            {
                _detail.Remove(page);
                _pendingTiles.Remove(page);
            }

            // Links and text come on the background lane, after the renders: the picture never waits for them.
            if (!_links.ContainsKey(page)) RequestLinks(page, isVisible ? 0.5 + dist : 19.5 + dist);
            if (!_texts.ContainsKey(page)) RequestText(page, isVisible ? 1 + dist : 20 + dist);
        }

        // Drop bitmaps far from the viewport, then the farthest ones until the cache fits its pixel budget.
        var keepFrom = visible[0] - 4;
        var keepTo = visible[^1] + 4;
        foreach (var p in _full.Keys.Where(p => p < keepFrom || p > keepTo).ToList()) _full.Remove(p);
        foreach (var p in _stale.Keys.Where(p => p < keepFrom || p > keepTo).ToList()) _stale.Remove(p);
        var total = _full.Values.Sum(b => (long)b.Image.PixelWidth * b.Image.PixelHeight);
        foreach (var p in _full.Keys.Where(p => !visible.Contains(p)).OrderByDescending(p => Math.Abs((_rects[p].Top + _rects[p].Bottom) / 2 - center)).ToList())
        {
            if (total <= cacheBudget) break;
            total -= (long)_full[p].Image.PixelWidth * _full[p].Image.PixelHeight;
            _full.Remove(p);
        }
        foreach (var p in _detail.Keys.Where(p => !visible.Contains(p)).ToList()) _detail.Remove(p);
        foreach (var p in _pendingTiles.Keys.Where(p => !visible.Contains(p)).ToList()) _pendingTiles.Remove(p);
        // Text layers are small but add up over a long scroll; the engine keeps its own LRU for re-use.
        foreach (var p in _texts.Keys.Where(p => p < keepFrom - 8 || p > keepTo + 8).ToList()) _texts.Remove(p);
        // Cancel (even mid-render) pages that scrolled out of reach and tiles of pages no longer in view.
        _scheduler.Clear(key => int.TryParse(key.AsSpan(1), out var p) && key[0] switch
        {
            'p' => !wanted.Contains(p),
            'd' => !visible.Contains(p),
            'x' or 'l' => p < keepFrom - 8 || p > keepTo + 8,
            _ => false,
        });
    }

    /// <summary>
    /// Loads a page's links on the background lane (lower <paramref name="priority"/> = sooner). Kept out of the render
    /// job: links come from the text instance, which may be busy reading another page's text.
    /// </summary>
    private void RequestLinks(int page, double priority)
    {
        if (_engine is null || _scheduler is null || page < 0 || page >= PageCount || _links.ContainsKey(page)) return;
        var engine = _engine;
        var gen = _generation;
        _scheduler.Enqueue($"l{page}", RenderScheduler.BackgroundPriority + priority, ct => engine.GetLinks(page, ct), result =>
        {
            if (gen != _generation || result is not IReadOnlyList<PageLink> links) return;
            _links[page] = links;
            if (_surface.IsMouseOver && !_surface.IsMouseCaptured) UpdateCursor(Mouse.GetPosition(_surface));
        }, tag: gen);
    }

    private readonly Dictionary<int, (double Scale, RectD Region, int Generation)> _pendingTiles = [];

    /// <summary>True when the tile being made for <paramref name="page"/> will cover the visible part at this scale.</summary>
    private bool TileCovers(int page, double scale, RectD visPt) =>
        _pendingTiles.TryGetValue(page, out var t) && t.Generation == _generation && Math.Abs(t.Scale - scale) < 1e-6
        && t.Region.Inflate(0.5).Contains(visPt.X, visPt.Y) && t.Region.Inflate(0.5).Contains(visPt.Right, visPt.Bottom);

    /// <summary>MuPDF's device-pixel origin of a render of <paramref name="region"/> (it rounds the area outward).</summary>
    private static (int X, int Y) PixelOrigin(PageGeometry g, RectD region, double scale)
    {
        var r = new MuPDFCore.Rectangle(g.OriginX + region.X, g.OriginY + region.Y, g.OriginX + region.Right, g.OriginY + region.Bottom).Round(scale);
        return (r.X0, r.Y0);
    }

    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired while applying night colors.</exception>
    public static BitmapSource ToBitmap(RenderedImage img, bool invert, CancellationToken cancel = default)
    {
        if (invert) NightColors(img.Pixels, cancel);
        return ToBitmap(img);
    }

    /// <summary>Night colors by lightness: <c>Low + v * Span / 255</c> for every v in 0..255 (see <see cref="NightColors"/>).</summary>
    private static readonly byte[] NightLevels = [.. Enumerable.Range(0, 256).Select(v => (byte)(26 + v * 200 / 255))];

    /// <summary>
    /// Night reading: flips lightness but keeps hue (red stays red, blue stays blue), then softens the range so white
    /// paper becomes dark gray and black text becomes light gray rather than harsh pure white on black. Each channel
    /// is shifted by 255 - max - min (which keeps it within 0..255), then mapped through a table.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired (checked every 64K pixels).</exception>
    private static void NightColors(byte[] px, CancellationToken cancel)
    {
        const int Band = 3 * 65536;
        var levels = NightLevels;
        for (var start = 0; start < px.Length; start += Band)
        {
            cancel.ThrowIfCancellationRequested();
            var end = Math.Min(px.Length - 2, start + Band);
            for (var i = start; i < end; i += 3)
            {
                int b = px[i], g = px[i + 1], r = px[i + 2];
                int max = b > g ? b : g, min = b > g ? g : b;
                if (r > max) max = r;
                else if (r < min) min = r;
                var shift = 255 - max - min;
                px[i] = levels[b + shift];
                px[i + 1] = levels[g + shift];
                px[i + 2] = levels[r + shift];
            }
        }
    }

    public static BitmapSource ToBitmap(RenderedImage img)
    {
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgr24, null, img.Pixels, img.Stride);
        bmp.Freeze();
        return bmp;
    }

    private static readonly Brush PageBrush = Frozen(Brushes.White);
    private static readonly Brush DarkPageBrush = Frozen(new SolidColorBrush(Color.FromRgb(26, 26, 26)));

    private static T Frozen<T>(T f) where T : Freezable
    {
        if (f.CanFreeze) f.Freeze();
        return f;
    }

    /// <summary>A visual's top-left in device pixels relative to the window (for snapping drawing to pixels).</summary>
    private static Point DeviceOrigin(Visual target, double dpi)
    {
        if (PresentationSource.FromVisual(target)?.RootVisual is not Visual root || !target.IsDescendantOf(root)) return default;
        var p = target.TransformToAncestor(root).Transform(default);
        return new Point(p.X * dpi, p.Y * dpi);
    }

    /// <summary>
    /// The page layer: a viewport-sized element in the ScrollViewer's template, right under the scrolled content, so
    /// pages are drawn relative to the viewport's top-left. WPF composes in single precision: far down a long document
    /// (a 10,000-page file is ~18 million DIPs tall) scrolled coordinates lose whole pixels, so the bitmaps would no
    /// longer land on device pixels. Without the layer (an unexpected template), the surface paints itself as before.
    /// </summary>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (VisualTreeHelper.GetParent(_layer) is Panel old) old.Children.Remove(_layer);
        _layerInTemplate = false;
        if (GetTemplateChild("PART_ScrollContentPresenter") is FrameworkElement presenter && VisualTreeHelper.GetParent(presenter) is Panel panel)
        {
            Grid.SetRow(_layer, Grid.GetRow(presenter));
            Grid.SetColumn(_layer, Grid.GetColumn(presenter));
            Grid.SetRowSpan(_layer, Grid.GetRowSpan(presenter));
            Grid.SetColumnSpan(_layer, Grid.GetColumnSpan(presenter));
            _layer.Margin = presenter.Margin;
            panel.Children.Insert(panel.Children.IndexOf(presenter), _layer);
            _layerInTemplate = true;
        }
        _surface.InvalidateVisual();
    }

    /// <summary>
    /// Paints the pages in view. <paramref name="target"/> is the page layer (drawing relative to the viewport) or the
    /// surface (drawing in its own, scrolled, coordinates).
    /// </summary>
    private void Paint(DrawingContext dc, FrameworkElement target)
    {
        var vp = Viewport;
        var onLayer = ReferenceEquals(target, _layer);
        if (!onLayer) dc.DrawRectangle(Brushes.Transparent, null, vp); // hit-testable background for mouse input
        if (_engine is null) return;

        // The surface point at the target's top-left: where the scrolled content really is (layout may round it).
        var content = Content as Visual;
        _paintOrigin = onLayer && content is not null ? -VisualTreeHelper.GetOffset(content) - VisualTreeHelper.GetOffset(_surface) : default;
        try
        {
            PaintPages(dc, target, vp);
        }
        finally
        {
            _paintOrigin = default;
        }
    }

    private void PaintPages(DrawingContext dc, FrameworkElement target, Rect vp)
    {
        var engine = _engine!;
        var dpi = DpiScale;
        var deviceScale = _zoom * PtToDip * dpi;
        var (selFrom, selTo) = OrderedSelection();
        var pal = _palette;
        // Bitmaps must start on a device pixel to stay sharp, whatever the (fractional) scroll offset. Floor(v + 0.5)
        // rather than Round: a page and its tile sit a whole number of pixels apart and must round the same way.
        var origin = DeviceOrigin(target, dpi);
        double SnapX(double x) => (Math.Floor(origin.X + x * dpi + 0.5) - origin.X) / dpi;
        double SnapY(double y) => (Math.Floor(origin.Y + y * dpi + 0.5) - origin.Y) / dpi;
        var focused = IsKeyboardFocusWithin;
        var po = _paintOrigin;

        foreach (var i in VisiblePages(vp))
        {
            var r = _rects[i];
            r.Offset(-po.X, -po.Y);
            var pr = new Rect(new Point(SnapX(r.Left), SnapY(r.Top)), new Point(SnapX(r.Right), SnapY(r.Bottom)));
            dc.DrawRectangle(pal.Shadow, null, new Rect(pr.X + 2, pr.Y + 2, pr.Width, pr.Height));
            dc.DrawRectangle(EffectiveInvert ? DarkPageBrush : PageBrush, null, pr);

            dc.PushClip(new RectangleGeometry(pr));
            if (_failed.Contains(i))
                PaintFailed(dc, pr, dpi);
            else
            {
                if ((_full.TryGetValue(i, out var full) || _stale.TryGetValue(i, out full)) && full.Rotation == _viewRotation)
                    dc.DrawImage(full.Image, Place(i, full));
                if (_detail.TryGetValue(i, out var det) && Math.Abs(det.Scale - deviceScale) < 1e-6 && det.Rotation == _viewRotation)
                    dc.DrawImage(det.Image, Place(i, det));
            }
            dc.Pop();

            PaintForms(dc, i);
            PaintReview(dc, i);
            PaintSigning(dc, i);
            PaintLinks(dc, i);
            PaintDrawing(dc, i);
            PaintSpeech(dc, i);

            if (_hitsByPage.TryGetValue(i, out var hits))
                foreach (var hit in hits)
                {
                    var active = ReferenceEquals(hit, _activeHit);
                    foreach (var hr in hit.Rects)
                        dc.DrawRectangle(active ? pal.ActiveHit : pal.Hit, active ? pal.ActiveHitPen : null, ToSurfaceRect(i, hr.Inflate(0.5)));
                }

            if (_texts.TryGetValue(i, out var text))
            {
                if (selFrom is { } a && selTo is { } b && i >= a.Page && i <= b.Page)
                {
                    var start = i == a.Page ? a.Caret : 0;
                    var end = i == b.Page ? b.Caret : text.Text.Length;
                    foreach (var sr in text.GetRects(start, end)) dc.DrawRectangle(pal.Selection, null, ToSurfaceRect(i, sr));
                }
                if (_caretBrowsing && focused && _selActive is { } c && c.Page == i && CaretRect(text, c.Caret) is { } cr)
                {
                    // A zero-width box in page space: on a view turned a quarter it lies across the screen.
                    var cs = ToSurfaceRect(i, cr);
                    var thick = Math.Max(1.5, 2 / dpi);
                    dc.DrawRectangle(pal.Caret, null, cs.Width <= cs.Height
                        ? new Rect(SnapX(cs.X), cs.Y, thick, cs.Height)
                        : new Rect(cs.X, SnapY(cs.Y), cs.Width, thick));
                }
            }

            if (_snapPage == i)
            {
                var sr = RectD.FromEdges(_snapStartPt.X, _snapStartPt.Y, _snapEndPt.X, _snapEndPt.Y);
                dc.DrawRectangle(pal.SnapFill, pal.SnapPen, ToSurfaceRect(i, sr));
            }

            if (focused && _focusLink is { } fl && fl.Page == i)
            {
                var fr = ToSurfaceRect(i, fl.Link.Area);
                fr.Inflate(2, 2);
                dc.DrawRectangle(null, pal.FocusPen, fr);
            }
        }

        // Where a bitmap goes: the area MuPDF actually rendered (it rounds outward to whole pixels, so it can start a
        // fraction of a pixel before the page or region), turned with the view. At the current scale it is drawn pixel
        // for pixel from a snapped corner; otherwise (a capped or older render) stretched over exactly that area.
        Rect Place(int page, PageBitmap b)
        {
            var g = engine.GetPage(page);
            var (w, h) = b.Rotation is 90 or 270 ? (b.Image.PixelHeight, b.Image.PixelWidth) : (b.Image.PixelWidth, b.Image.PixelHeight);
            var sr = ToSurfaceRect(page, new RectD(b.PxX / b.Scale - g.OriginX, b.PxY / b.Scale - g.OriginY, w / b.Scale, h / b.Scale));
            return Math.Abs(b.Scale - deviceScale) < 1e-6 ? new Rect(SnapX(sr.X), SnapY(sr.Y), b.Image.PixelWidth / dpi, b.Image.PixelHeight / dpi) : sr;
        }
    }

    /// <summary>
    /// With the view turned, places the inline editor (form field or Fill &amp; Sign text) the way the page shows:
    /// turned with it, over its field. PageViewer.Forms.cs positions editors for an unturned view; this runs after every
    /// layout pass and only changes what differs, so it settles at once (and does nothing on an unturned view).
    /// </summary>
    private void TurnEditor()
    {
        if (_viewRotation == 0 || _editor is not { Visibility: Visibility.Visible } editor || _rects.Length == 0 || _engine is null) return;
        double left, top;
        int angle;
        if (_editorWidget is { } w && w.PageIndex < _rects.Length && !_rects[w.PageIndex].IsEmpty)
        {
            var r = ToSurfaceRect(w.PageIndex, w.Rect);
            angle = (RotationOf(w.PageIndex) + _viewRotation) % 360;
            var (length, thickness) = angle is 90 or 270 ? (r.Height, r.Width) : (r.Width, r.Height);
            Set(editor, WidthProperty, Math.Max(length, 40));
            Set(editor, HeightProperty, Math.Max(thickness, 16));
            (left, top) = (r.X, r.Y);
        }
        else if ((_editorStamp is { } s ? (s.Page, s.Pt) : _editorDrawing is { } d ? (d.Page, new Point(d.Box.X, d.Box.Y)) : (-1, default)) is var (sPage, sPt)
                 && sPage >= 0 && sPage < _rects.Length && !_rects[sPage].IsEmpty)
        {
            // The text's top-left corner stays where it is on the page; the editor hangs from it, turned.
            var p = ToSurfacePoint(sPage, sPt.X, sPt.Y);
            angle = _viewRotation;
            double ew = editor.ActualWidth, eh = editor.ActualHeight;
            (left, top) = angle switch
            {
                90 => (p.X - eh, p.Y),
                180 => (p.X - ew, p.Y - eh),
                _ => (p.X, p.Y - ew),
            };
        }
        else
        {
            return;
        }
        Set(editor, Canvas.LeftProperty, left);
        Set(editor, Canvas.TopProperty, top);
        var current = editor.LayoutTransform is RotateTransform rt ? rt.Angle : 0;
        if (Math.Abs(current - angle) > 0.01) editor.LayoutTransform = angle == 0 ? Transform.Identity : Frozen(new RotateTransform(angle));

        static void Set(FrameworkElement e, DependencyProperty dp, double v)
        {
            if (e.GetValue(dp) is not double old || double.IsNaN(old) || Math.Abs(old - v) > 0.01) e.SetValue(dp, v);
        }
    }

    private static readonly Brush FailedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));

    /// <summary>A page MuPDF couldn't render: says so instead of staying blank.</summary>
    private static void PaintFailed(DrawingContext dc, Rect pr, double dpi)
    {
        var size = Math.Clamp(pr.Width / 24, 9, 16);
        var ft = new FormattedText("This page can't be displayed", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, FailedBrush, dpi) { MaxTextWidth = Math.Max(10, pr.Width - 16), TextAlignment = TextAlignment.Center };
        dc.DrawText(ft, new Point(pr.X + 8, pr.Y + Math.Max(8, (pr.Height - ft.Height) / 2)));
    }


    // ───────────────────────────── Search highlights ─────────────────────────────

    public void SetSearchHits(IEnumerable<SearchHit> hits)
    {
        _hitsByPage = hits.GroupBy(h => h.Page).ToDictionary(g => g.Key, g => g.ToList());
        if (_activeHit is not null && !_hitsByPage.ContainsKey(_activeHit.Page)) _activeHit = null;
        _surface.InvalidateVisual();
    }

    public void ShowHit(SearchHit? hit)
    {
        _activeHit = hit;
        if (hit is not null && hit.Page < PageCount)
        {
            if (_layout == ViewLayout.SinglePage || (_zoomMode == ZoomMode.FitPage && RowOf(hit.Page) != RowOf(_currentPage)))
                GoToPage(hit.Page, record: false);
            var bounds = hit.Rects.Aggregate(default(RectD), (acc, r) => acc.Union(r));
            var sr = ToSurfaceRect(hit.Page, bounds);
            var vp = Viewport;
            if (!vp.Contains(sr))
            {
                ScrollToVerticalOffset(sr.Y - vp.Height / 3);
                if (sr.Left < vp.Left || sr.Right > vp.Right) ScrollToHorizontalOffset(sr.X - vp.Width / 3);
            }
        }
        _surface.InvalidateVisual();
    }

    // ───────────────────────────── Mouse & keyboard ─────────────────────────────

    private PageLink? LinkAt(int page, Point pt)
    {
        if (page < 0 || !_links.TryGetValue(page, out var links)) return null;
        foreach (var l in links)
            if (l.Area.Contains(pt.X, pt.Y)) return l;
        return null;
    }

    private void OnSurfaceDown(object sender, MouseButtonEventArgs e)
    {
        var hadTextEditor = _editorDrawing is not null; // a click that only closes a text box's editor doesn't start another
        Focus();
        if (_engine is null) return;
        var sp = e.GetPosition(_surface);
        var page = HitPage(sp);
        _pressViewer = e.GetPosition(this);
        _pressOffset = new Vector(HorizontalOffset, VerticalOffset);
        _dragged = false;
        _pressLink = page >= 0 ? LinkAt(page, ToPagePoint(page, sp)) : null;
        _pressLinkPage = page;
        HideLinkTip();
        ClearFocusLink();
        if (e.ChangedButton == MouseButton.Left && HandleFormMouseDown(e, page, sp)) return;
        CommitEditor();
        if (HandleLinkDown(e, page, sp)) return;
        if (HandleSigningDown(e, page, sp)) return;
        if (HandleDrawingDown(e, page, sp, hadTextEditor)) return;
        if (HandleReviewDown(e, page, sp)) return;

        switch (_tool)
        {
            case ViewerTool.Hand:
                _panning = true;
                break;
            case ViewerTool.SelectText:
            {
                var p = page >= 0 ? page : NearestPage(sp);
                if (p < 0) break;
                if (TryGetPageText(p) is not { } text) break; // still loading (the cursor stays an arrow until it arrives)
                var pt = ToPagePoint(p, sp);
                var caret = text.HitTest(pt.X, pt.Y);
                if (e.ClickCount == 2 || e.ClickCount == 3)
                {
                    var (s, en) = e.ClickCount == 2 ? text.WordAt(caret) : text.LineAt(caret);
                    _selAnchor = new TextPos(p, s);
                    _selActive = new TextPos(p, en);
                    _selecting = false;
                    _surface.InvalidateVisual();
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;
                }
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _selAnchor is not null)
                    _selActive = new TextPos(p, caret);
                else
                    _selAnchor = _selActive = new TextPos(p, caret);
                _selecting = true;
                _surface.InvalidateVisual();
                break;
            }
            case ViewerTool.Redact when page >= 0:
                _markPage = page;
                _markStartPt = _markEndPt = ToPagePoint(page, sp);
                break;
            case ViewerTool.Snapshot when page >= 0:
                _snapPage = page;
                _snapStartPt = _snapEndPt = ToPagePoint(page, sp);
                break;
        }
        _surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnSurfaceMove(object sender, MouseEventArgs e)
    {
        var sp = e.GetPosition(_surface);
        var vpPos = e.GetPosition(this);
        if (e.LeftButton == MouseButtonState.Pressed && _surface.IsMouseCaptured)
        {
            if ((vpPos - _pressViewer).Length > 3) _dragged = true;

            if (_panning)
            {
                var d = vpPos - _pressViewer;
                ScrollToHorizontalOffset(_pressOffset.X - d.X);
                ScrollToVerticalOffset(_pressOffset.Y - d.Y);
                Cursor = Cursors.ScrollAll;
                return;
            }

            AutoScroll(vpPos);
            if (MoveSigningDrag(sp) || MoveLinkDrag(sp)) return;
            if (MoveDrawing(e, sp)) return;
            if (_selecting && _dragged)
            {
                var p = HitPage(sp);
                if (p < 0) p = NearestPage(sp);
                if (p >= 0)
                {
                    var pt = ToPagePoint(p, sp);
                    if (TryGetPageText(p) is { } t) _selActive = new TextPos(p, t.HitTest(pt.X, pt.Y));
                    _surface.InvalidateVisual();
                }
            }
            else if (_markPage >= 0)
            {
                var pt = ToPagePoint(_markPage, sp);
                var g = _engine!.GetPage(_markPage);
                _markEndPt = new Point(Math.Clamp(pt.X, 0, g.Width), Math.Clamp(pt.Y, 0, g.Height));
                _surface.InvalidateVisual();
            }
            else if (_snapPage >= 0)
            {
                var pt = ToPagePoint(_snapPage, sp);
                var g = _engine!.GetPage(_snapPage);
                _snapEndPt = new Point(Math.Clamp(pt.X, 0, g.Width), Math.Clamp(pt.Y, 0, g.Height));
                _surface.InvalidateVisual();
            }
            return;
        }
        UpdateCursor(sp);
    }

    private void AutoScroll(Point vpPos)
    {
        const double edge = 24;
        if (vpPos.Y < edge) ScrollToVerticalOffset(VerticalOffset - (edge - vpPos.Y));
        else if (vpPos.Y > ViewH - edge) ScrollToVerticalOffset(VerticalOffset + (vpPos.Y - ViewH + edge));
        if (vpPos.X < edge) ScrollToHorizontalOffset(HorizontalOffset - (edge - vpPos.X));
        else if (vpPos.X > ViewW - edge) ScrollToHorizontalOffset(HorizontalOffset + (vpPos.X - ViewW + edge));
    }

    private void UpdateCursor(Point sp)
    {
        if (_engine is null)
        {
            _surface.Cursor = null;
            return;
        }
        var page = HitPage(sp);
        Cursor = null;
        if (FormCursor(page, sp) is { } formCursor)
        {
            HideLinkTip();
            _surface.Cursor = formCursor;
            return;
        }
        if ((SigningCursor(page, sp) ?? LinkToolCursor(page, sp)) is { } signCursor)
        {
            HideLinkTip();
            _surface.Cursor = signCursor;
            return;
        }
        if (DrawingCursor(page, sp) is { } drawCursor)
        {
            if (DrawTools.IsDrawTool(_tool) || !ShowAnnotTip(page, sp)) HideLinkTip(); // a drawing's comment shows on hover too
            _surface.Cursor = drawCursor;
            return;
        }
        if (page >= 0 && LinkAt(page, ToPagePoint(page, sp)) is { } link)
        {
            ShowLinkTip(page, link, keyboard: false);
            _surface.Cursor = Cursors.Hand;
            return;
        }
        if (!ShowAnnotTip(page, sp)) HideLinkTip();
        if (_tool == ViewerTool.SelectText && page >= 0 && !_texts.ContainsKey(page)) RequestText(page, 0);
        _surface.Cursor = _tool switch
        {
            ViewerTool.SelectText when page >= 0 && _texts.TryGetValue(page, out var t) && IsOverText(t, ToPagePoint(page, sp)) => Cursors.IBeam,
            ViewerTool.Snapshot or ViewerTool.AddNote or ViewerTool.Redact => Cursors.Cross,
            _ => Cursors.Arrow,
        };

        static bool IsOverText(PageText t, Point pt) => t.IsOverText(pt.X, pt.Y);
    }

    private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
    {
        var sp = e.GetPosition(_surface);
        if (EndSigningDrag() || EndLinkDrag() || EndDrawingDrag(sp))
        {
            EndDrag();
            UpdateCursor(sp);
            return;
        }
        var wasSnap = _snapPage;
        var wasMark = _markPage;
        var link = _pressLink;
        var linkPage = _pressLinkPage;
        var dragged = _dragged;
        var wasSelecting = _selecting;
        EndDrag();

        if (!dragged && link is not null && HitPage(sp) == linkPage && LinkAt(linkPage, ToPagePoint(linkPage, sp)) == link)
        {
            if (wasSelecting) ClearSelection();
            ActivateLink(link);
            return;
        }

        if (wasSelecting)
        {
            if (_selAnchor == _selActive && !_caretBrowsing) _selAnchor = _selActive = null; // caret browsing: the click placed the caret
            _surface.InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        if (wasMark >= 0) EndRedactDrag(wasMark, dragged);
        if (wasSnap >= 0 && dragged) CopySnapshot(wasSnap, RectD.FromEdges(_snapStartPt.X, _snapStartPt.Y, _snapEndPt.X, _snapEndPt.Y));
        UpdateCursor(sp);
    }

    private void EndDrag()
    {
        _panning = _selecting = false;
        CancelSigningDrag();
        CancelLinkDrag();
        CancelDrawingDrag();
        if (_markPage >= 0)
        {
            _markPage = -1;
            _surface.InvalidateVisual();
        }
        if (_snapPage >= 0)
        {
            _snapPage = -1;
            _surface.InvalidateVisual();
        }
        Cursor = null;
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
    }

    private void ActivateLink(PageLink link)
    {
        HideLinkTip();
        if (link.Uri is null)
        {
            GoToPage(link.PageIndex, link.TargetY);
            return;
        }

        var uri = link.Uri;
        var safe = Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "mailto";
        if (!safe)
        {
            if (!Prompt.Confirm(Window.GetWindow(this), "Open this link?",
                $"The document wants to open {uri}. Only continue if you trust the document.", "Open link", warning: true)) return;
        }
        Shell.Open(uri);
    }

    private void CopySnapshot(int page, RectD region)
    {
        if (_engine is null || _scheduler is null || region.Width < 2 || region.Height < 2) return;
        var engine = _engine;
        var rotation = _viewRotation; // copied the way it is shown
        var scale = Math.Max(2, _zoom * PtToDip * DpiScale);
        while (region.Width * region.Height * scale * scale > 40_000_000) scale *= 0.8;
        _scheduler.Enqueue("snapshot", -1, ct => ToBitmap(engine.Render(page, scale, region, ct).Rotated(rotation, ct)), result =>
        {
            if (result is not BitmapSource bmp) return;
            try
            {
                Clipboard.SetImage(bmp);
                StatusMessage?.Invoke(this, $"Snapshot copied ({bmp.PixelWidth} × {bmp.PixelHeight} px)");
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                StatusMessage?.Invoke(this, $"Couldn't copy: {ex.Message}");
            }
        }, ex => StatusMessage?.Invoke(this, ex is ObjectDisposedException
            ? "Couldn't copy the snapshot: the document changed meanwhile. Try again."
            : $"Couldn't copy the snapshot: {ex.Message}"));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (Math.Abs(e.Delta) >= 120 && e.Delta % 120 == 0) ZoomStep(e.Delta > 0 ? 1 : -1, e.GetPosition(this));
            else SetZoom(_zoom * Math.Pow(1.2, e.Delta / 120.0), e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            ScrollToHorizontalOffset(HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }
        if (_layout == ViewLayout.SinglePage && _engine is not null)
        {
            if (e.Delta < 0 && VerticalOffset >= ScrollableHeight - 0.5 && _currentPage < PageCount - 1)
            {
                GoToPage(_currentPage + 1, record: false);
                ScrollToVerticalOffset(0);
                e.Handled = true;
                return;
            }
            if (e.Delta > 0 && VerticalOffset <= 0.5 && _currentPage > 0)
            {
                GoToPage(_currentPage - 1, record: false);
                UpdateLayout();
                ScrollToVerticalOffset(ScrollableHeight);
                e.Handled = true;
                return;
            }
        }
        base.OnMouseWheel(e);
    }

    /// <summary>
    /// PageUp/PageDown never reach <see cref="OnKeyDown"/>: ScrollViewer's own key bindings turn them into its scroll
    /// commands first. So the page turns at a page's edge (single page, fit page) are decided here.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || _engine is null || e.Key is not (Key.PageUp or Key.PageDown) || Keyboard.Modifiers != ModifierKeys.None || IsEditorKey(e)) return;
        if (_layout != ViewLayout.SinglePage && _zoomMode != ZoomMode.FitPage) return;
        if (e.Key == Key.PageDown && VerticalOffset >= ScrollableHeight - 0.5) NextPage();
        else if (e.Key == Key.PageUp && VerticalOffset <= 0.5) PreviousPageFromTop();
        else return;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Keys typed into a field or Fill & Sign editor bubble up here. Leave them alone: handling KeyDown makes WPF
        // drop the character, so viewer shortcuts (H, A, T, Space…) would eat letters from the user's text.
        if (IsEditorKey(e)) return;
        if (_engine is not null && (HandleFormKey(e) || HandleSigningKey(e) || HandleLinkKey(e) || HandleDrawingKey(e) || HandleCaretKey(e)))
        {
            e.Handled = true;
            return;
        }
        var pageMode = _layout == ViewLayout.SinglePage || _zoomMode == ZoomMode.FitPage;
        if (_engine is not null && e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            // Shift+Space: one screen back (Space's opposite).
            if (pageMode && VerticalOffset <= 0.5) PreviousPageFromTop();
            else PageUp();
            e.Handled = true;
            return;
        }
        if (_engine is null || Keyboard.Modifiers != ModifierKeys.None)
        {
            base.OnKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Space when pageMode && VerticalOffset >= ScrollableHeight - 0.5:
            case Key.Right when pageMode && ScrollableWidth < 1:
                NextPage();
                break;
            case Key.Left when pageMode && ScrollableWidth < 1:
                PreviousPage();
                break;
            case Key.Space:
                PageDown();
                break;
            case Key.Home:
                GoToPage(0);
                break;
            case Key.End:
                // The end of the document: the last page's bottom, not its top.
                GoToPage(PageCount - 1);
                UpdateLayout();
                ScrollToVerticalOffset(ScrollableHeight);
                break;
            case Key.H:
                Tool = ViewerTool.Hand;
                break;
            case Key.T:
                Tool = ViewerTool.SelectText;
                break;
            case Key.G:
                Tool = ViewerTool.Snapshot;
                break;
            case Key.A:
                Tool = ViewerTool.AddText;
                break;
            case Key.K:
                Tool = ViewerTool.AddCheck;
                break;
            case Key.X:
                Tool = ViewerTool.AddCross;
                break;
            case var k when CanAnnotate && DrawTools.ToolForKey(k) is { } drawTool:
                Tool = drawTool;
                break;
            case Key.I when CanAnnotate:
                Tool = ViewerTool.AddImage; // the host asks for the picture first
                break;
            case Key.N when CanAnnotate:
                Tool = ViewerTool.AddLink;
                break;
            case Key.S when CanAnnotate:
                Tool = ViewerTool.AddStamp; // the host arms the stamp used last
                break;
            case Key.Escape when DrawTools.IsDrawTool(_tool):
                Tool = ViewerTool.Hand;
                break;
            case Key.Escape when HasSelection:
                ClearSelection();
                break;
            case Key.Escape when _linkTip?.IsOpen == true:
                HideLinkTip();
                break;
            default:
                base.OnKeyDown(e);
                return;
        }
        e.Handled = true;
    }

    /// <summary>Paging back from the top of a page: the previous page, shown from its bottom (reading backwards).</summary>
    private void PreviousPageFromTop()
    {
        var was = _currentPage;
        PreviousPage();
        if (_layout != ViewLayout.SinglePage || _currentPage == was) return;
        UpdateLayout();
        ScrollToVerticalOffset(ScrollableHeight);
    }

    /// <summary>
    /// The scroll content: sized to the full layout and takes the mouse. Painting happens on the page layer (it asks
    /// the layer to repaint whenever it is invalidated, so every overlay's InvalidateVisual reaches the pages too).
    /// </summary>
    private sealed class PageSurface(PageViewer owner) : FrameworkElement
    {
        private Size _extent;

        public void SetExtent(double w, double h)
        {
            var size = new Size(w, h);
            if (size == _extent) return;
            _extent = size;
            InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size availableSize) => _extent;

        protected override void OnRender(DrawingContext dc)
        {
            if (!owner._layerInTemplate)
            {
                owner.Paint(dc, this);
                return;
            }
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(_extent)); // hit-testable background for mouse input
            owner._layer.InvalidateVisual();
        }
    }

    /// <summary>The viewport-sized page layer (see <see cref="OnApplyTemplate"/>): never hit-tested, clipped to the viewport.</summary>
    private sealed class PaintLayer : FrameworkElement
    {
        private readonly PageViewer _owner;

        public PaintLayer(PageViewer owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
            Focusable = false;
            ClipToBounds = true;
        }

        protected override void OnRender(DrawingContext dc) => _owner.Paint(dc, this);
    }
}
