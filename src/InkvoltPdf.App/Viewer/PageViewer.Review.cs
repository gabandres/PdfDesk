using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InkvoltPdf.Core;

namespace InkvoltPdf.App.Viewer;

/// <summary>An area marked for redaction (page space), not yet applied.</summary>
public sealed record RedactMark(int PageIndex, RectD Rect);

/// <summary>Where the note tool was clicked (page space).</summary>
public sealed record NotePlacement(int PageIndex, double X, double Y);

/// <summary>Comments (hover text, flashing, the note tool) and redaction marks on top of the page view.</summary>
public sealed partial class PageViewer
{
    private static readonly Brush RedactFill = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xE0, 0x20, 0x20)));
    private static readonly Pen RedactPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xD0, 0x10, 0x10)), 1.5));
    private static readonly Pen RedactDragPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xD0, 0x10, 0x10)), 1) { DashStyle = DashStyles.Dash });

    private Dictionary<int, List<AnnotationInfo>> _annotsByPage = [];
    private readonly List<RedactMark> _redactions = [];
    private int _markPage = -1;
    private Point _markStartPt, _markEndPt;
    private (int Page, RectD Rect)? _flash;
    private bool _flashOn;
    private int _flashTicks;
    private DispatcherTimer? _flashTimer;
    private AnnotationInfo? _tipAnnot;

    /// <summary>The note tool was clicked on an empty spot: the host asks for the text and adds the note.</summary>
    public event EventHandler<NotePlacement>? NoteRequested;

    /// <summary>A comment was double-clicked (or clicked with the note tool): the host opens its editor.</summary>
    public event EventHandler<AnnotationInfo>? AnnotationActivated;

    /// <summary>Redaction marks were added, removed or cleared.</summary>
    public event EventHandler? RedactionsChanged;

    /// <summary>Comments shown as hover text (and hit-tested for double-clicks); replaced after every edit.</summary>
    public void SetAnnotations(IReadOnlyList<AnnotationInfo> annotations)
    {
        _annotsByPage = annotations.Where(a => !a.Hidden).GroupBy(a => a.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        if (_tipAnnot is not null) HideLinkTip();
        OnDrawingsReloaded();
    }

    public IReadOnlyList<RedactMark> Redactions => _redactions;

    public void AddRedactions(IEnumerable<RedactMark> marks)
    {
        var added = false;
        foreach (var m in marks)
        {
            if (m.PageIndex < 0 || m.PageIndex >= PageCount || m.Rect.IsEmpty || _redactions.Contains(m)) continue;
            _redactions.Add(m);
            added = true;
        }
        if (!added) return;
        _surface.InvalidateVisual();
        RedactionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearRedactions()
    {
        if (_redactions.Count == 0) return;
        _redactions.Clear();
        _surface.InvalidateVisual();
        RedactionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The selected text's line boxes per page (page space), for text markup and "redact selected text". Pages whose
    /// text isn't loaded yet are read on a background thread (a selection can span thousands of pages); reports
    /// (pages done, total pages).
    /// </summary>
    /// <exception cref="ObjectDisposedException">The document was edited or closed meanwhile.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired.</exception>
    public async Task<List<(int Page, List<RectD> Rects)>> GetSelectionLineRectsAsync(IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        var (from, to) = OrderedSelection();
        if (from is not { } a || to is not { } b || _engine is null) return [];
        var engine = _engine;
        var last = Math.Min(b.Page, PageCount - 1);
        var cached = new Dictionary<int, PageText>();
        for (var p = a.Page; p <= last; p++)
            if (_texts.TryGetValue(p, out var t)) cached[p] = t;
        var total = last - a.Page + 1;
        return await Task.Run(() =>
        {
            var result = new List<(int, List<RectD>)>();
            for (var p = a.Page; p <= last; p++)
            {
                cancel.ThrowIfCancellationRequested();
                var text = cached.TryGetValue(p, out var t) ? t : engine.GetText(p, cache: total <= 8, cancel);
                var start = p == a.Page ? a.Caret : 0;
                var end = p == b.Page ? Math.Min(b.Caret, text.Text.Length) : text.Text.Length;
                if (end > start && text.GetRects(start, end).Where(r => !r.IsEmpty).ToList() is { Count: > 0 } rects) result.Add((p, rects));
                progress?.Report((p - a.Page + 1, total));
            }
            return result;
        }, cancel);
    }

    /// <summary>Dev/test entry: selects the text between two page points, like a drag with the Select text tool.</summary>
    internal bool DevSelect(int page, double x1, double y1, double x2, double y2)
    {
        if (_engine is null || page < 0 || page >= PageCount) return false;
        var text = _texts.TryGetValue(page, out var t) ? t : _texts[page] = _engine.GetText(page);
        if (_tool != ViewerTool.SelectText) Tool = ViewerTool.SelectText;
        _selAnchor = new TextPos(page, text.HitTest(x1, y1));
        _selActive = new TextPos(page, text.HitTest(x2, y2));
        _surface.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return HasSelection;
    }

    /// <summary>Dev/test entry: where a page point (points, top-left origin) is in <paramref name="root"/>'s coordinates, scrolled into view first.</summary>
    internal Point? DevPointIn(Visual root, int page, double x, double y)
    {
        if (_engine is null || page < 0 || page >= PageCount || page >= _rects.Length) return null;
        var sp = _rects[page].IsEmpty ? new Point(double.NaN, double.NaN) : ToSurfacePoint(page, x, y);
        var vp = Viewport;
        vp.Inflate(-4, -4);
        if (!vp.Contains(sp))
        {
            EnsureVisible(page, new RectD(x - 2, y - 2, 4, 4));
            UpdateLayout();
            sp = ToSurfacePoint(page, x, y);
        }
        return _surface.TransformToAncestor(root).Transform(sp);
    }

    /// <summary>Dev/test entry: the hover tip's text while it is open.</summary>
    internal string? DevTipText => _linkTip is { IsOpen: true } tip
        ? tip.Content as string ?? string.Join(" / ", (tip.Content as Panel)?.Children.OfType<TextBlock>().Select(t => t.Text) ?? [])
        : null;

    /// <summary>Scrolls a page area into view and blinks an outline around it (e.g. the comment picked in the list).</summary>
    public void Flash(int page, RectD rect)
    {
        if (page < 0 || page >= PageCount) return;
        EnsureVisible(page, rect);
        _flash = (page, rect);
        _flashOn = true;
        _flashTicks = 0;
        _flashTimer ??= CreateFlashTimer();
        _flashTimer.Stop();
        _flashTimer.Start();
        _surface.InvalidateVisual();
    }

    private DispatcherTimer CreateFlashTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) =>
        {
            _flashOn = !_flashOn;
            if (++_flashTicks >= 7)
            {
                timer.Stop();
                _flash = null;
            }
            _surface.InvalidateVisual();
        };
        return timer;
    }

    private AnnotationInfo? AnnotAt(int page, Point pt)
    {
        if (page < 0 || !_annotsByPage.TryGetValue(page, out var list)) return null;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var a = list[i];
            if (a.QuadRects.Count > 0 ? a.QuadRects.Any(q => q.Inflate(1).Contains(pt.X, pt.Y)) : a.Rect.Inflate(1).Contains(pt.X, pt.Y)) return a;
        }
        return null;
    }

    private void PaintReview(DrawingContext dc, int page)
    {
        foreach (var m in _redactions)
            if (m.PageIndex == page) dc.DrawRectangle(RedactFill, RedactPen, ToSurfaceRect(page, m.Rect));
        if (_markPage == page)
            dc.DrawRectangle(RedactFill, RedactDragPen, ToSurfaceRect(page, RectD.FromEdges(_markStartPt.X, _markStartPt.Y, _markEndPt.X, _markEndPt.Y)));
        if (_flash is { } f && f.Page == page && _flashOn)
        {
            var r = ToSurfaceRect(page, f.Rect);
            r.Inflate(3, 3);
            dc.DrawRectangle(null, _palette.FocusPen, r);
        }
    }

    /// <summary>Note tool clicks and comment double-clicks. True if the press was consumed.</summary>
    private bool HandleReviewDown(MouseButtonEventArgs e, int page, Point sp)
    {
        if (page < 0 || e.ChangedButton != MouseButton.Left) return false;
        var pt = ToPagePoint(page, sp);
        var hit = AnnotAt(page, pt);
        if (hit is { IsInkvoltPdfStamp: false } && (_tool == ViewerTool.AddNote || (e.ClickCount == 2 && _tool == ViewerTool.Hand)))
        {
            HideLinkTip();
            AnnotationActivated?.Invoke(this, hit);
            e.Handled = true;
            return true;
        }
        if (_tool == ViewerTool.AddNote)
        {
            NoteRequested?.Invoke(this, new NotePlacement(page, pt.X, pt.Y));
            e.Handled = true;
            return true;
        }
        return false;
    }

    /// <summary>Redact tool: a click (no drag) on a mark removes it; a drag adds one.</summary>
    private void EndRedactDrag(int page, bool dragged)
    {
        if (page < 0) return;
        if (!dragged)
        {
            var at = _markStartPt;
            var index = _redactions.FindLastIndex(m => m.PageIndex == page && m.Rect.Contains(at.X, at.Y));
            if (index < 0) return;
            _redactions.RemoveAt(index);
            _surface.InvalidateVisual();
            RedactionsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var rect = RectD.FromEdges(_markStartPt.X, _markStartPt.Y, _markEndPt.X, _markEndPt.Y);
        if (rect.Width >= 2 && rect.Height >= 2) AddRedactions([new RedactMark(page, rect)]);
    }

    /// <summary>Hover text for a comment under the pointer. True if one is shown.</summary>
    private bool ShowAnnotTip(int page, Point sp)
    {
        if (page < 0 || AnnotAt(page, ToPagePoint(page, sp)) is not { IsInkvoltPdfStamp: false } a || a.Contents.Trim().Length == 0) return false;
        if (_linkTip is { IsOpen: true } && ReferenceEquals(_tipAnnot, a)) return true;
        _linkTip ??= new ToolTip();
        _linkTip.IsOpen = false;
        _tipLink = null;
        _tipAnnot = a;
        var panel = new StackPanel { MaxWidth = 360 };
        var head = string.Join(" · ", new[] { a.Author, a.Modified?.LocalDateTime.ToString("g") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (head.Length > 0) panel.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var body = a.Contents.Length > 800 ? a.Contents[..800] + "…" : a.Contents;
        panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        _linkTip.Content = panel;
        _linkTip.PlacementTarget = _surface;
        _linkTip.Placement = PlacementMode.Mouse;
        _linkTip.PlacementRectangle = Rect.Empty;
        _linkTip.IsOpen = true;
        return true;
    }

    /// <summary>Marks and comments refer to pages: a structural edit (or undo) drops them until they're re-read.</summary>
    private void ResetReview()
    {
        _annotsByPage = [];
        _flash = null;
        _flashTimer?.Stop();
        _markPage = -1;
        ResetSigning();
        ResetLinks();
        ResetDrawing();
        if (_redactions.Count == 0) return;
        _redactions.Clear();
        RedactionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
