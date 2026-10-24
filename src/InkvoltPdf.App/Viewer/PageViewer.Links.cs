using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InkvoltPdf.Core;

namespace InkvoltPdf.App.Viewer;

/// <summary>A link box drawn with the Link tool (page space).</summary>
public sealed record LinkPlacement(int PageIndex, RectD Rect);

/// <summary>A link moved or resized (<see cref="Rect"/>), or deleted (null).</summary>
public sealed record LinkChange(string Id, int PageIndex, RectD? Rect);

/// <summary>
/// The Link tool on top of the page view (<see cref="ViewerTool.AddLink"/>): drag a box to make a link (a click makes a
/// default-size one); the document's links show as dashed boxes: click one to select it, drag to move it, drag its
/// corner to resize it, double-click or Enter to change where it leads, Delete removes it, Esc lets go. With the other
/// tools links work as usual, and the link under a right-click is <see cref="ContextLink"/> (for Edit / Delete).
/// </summary>
public sealed partial class PageViewer
{
    private const double LinkHandleSize = 9;

    private Dictionary<int, List<LinkInfo>> _linkAnnots = [];
    private string? _selectedLink;
    private (LinkInfo Link, bool Resize, Point Start, RectD Rect)? _linkDrag;
    private int _linkBoxPage = -1;
    private Point _linkBoxStart, _linkBoxEnd;

    /// <summary>A box was drawn with the Link tool: ask where the link leads, then add it.</summary>
    public event EventHandler<LinkPlacement>? LinkDrawn;

    public event EventHandler<LinkChange>? LinkChanged;

    /// <summary>A link was double-clicked (or Enter pressed on it) with the Link tool: change where it leads.</summary>
    public event EventHandler<LinkInfo>? LinkEditRequested;

    /// <summary>The link annotation under the last right-click (any tool), for the context menu; null when there was none.</summary>
    public LinkInfo? ContextLink { get; private set; }

    /// <summary>The link selected with the Link tool.</summary>
    public LinkInfo? SelectedLink => _selectedLink is null ? null : _linkAnnots.Values.SelectMany(l => l).FirstOrDefault(l => l.Id == _selectedLink);

    /// <summary>The document's link annotations (read from the file, with their ids for editing).</summary>
    public void SetLinkAnnotations(IReadOnlyList<LinkInfo> links)
    {
        _linkAnnots = links.GroupBy(l => l.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        if (_selectedLink is not null && !links.Any(l => l.Id == _selectedLink)) _selectedLink = null;
        if (ContextLink is { } c && !links.Any(l => l.Id == c.Id)) ContextLink = null;
        _surface.InvalidateVisual();
    }

    private void ResetLinks()
    {
        _linkAnnots = [];
        _selectedLink = null;
        _linkDrag = null;
        _linkBoxPage = -1;
        ContextLink = null;
    }

    private LinkInfo? LinkAnnotAt(int page, Point pt) =>
        page >= 0 && _linkAnnots.TryGetValue(page, out var list) ? list.LastOrDefault(l => l.Rect.Inflate(2).Contains(pt.X, pt.Y)) : null;

    private bool LinkToolActive => _tool == ViewerTool.AddLink;

    /// <summary>Near the selected link's resize handle (its bottom-right corner on the page; another corner on a turned view).</summary>
    private bool OnLinkHandle(int page, Point sp, LinkInfo link)
    {
        if (link.PageIndex != page) return false;
        var h = ToSurfacePoint(page, link.Rect.Right, link.Rect.Bottom);
        return Math.Abs(sp.X - h.X) <= LinkHandleSize && Math.Abs(sp.Y - h.Y) <= LinkHandleSize;
    }

    /// <summary>The context menu notes the link under the pointer (from the keyboard: the link selected with the Link tool).</summary>
    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        if (e.CursorLeft < 0 && e.CursorTop < 0) ContextLink = SelectedLink;
        else
        {
            var sp = Mouse.GetPosition(_surface);
            var page = HitPage(sp);
            ContextLink = page >= 0 ? LinkAnnotAt(page, ToPagePoint(page, sp)) : null;
            if (ContextLink is { } l && LinkToolActive)
            {
                _selectedLink = l.Id;
                _surface.InvalidateVisual();
            }
        }
        base.OnContextMenuOpening(e);
    }

    /// <summary>Link tool presses: select, drag, resize, or draw a new box. True if consumed.</summary>
    private bool HandleLinkDown(MouseButtonEventArgs e, int page, Point sp)
    {
        if (!LinkToolActive || e.ChangedButton != MouseButton.Left || page < 0) return false;
        var pt = ToPagePoint(page, sp);
        var at = LinkAnnotAt(page, pt);
        var hit = SelectedLink is { } sel && OnLinkHandle(page, sp, sel) ? sel : at;
        if (hit is not null)
        {
            _selectedLink = hit.Id;
            if (e.ClickCount == 2)
            {
                _surface.InvalidateVisual();
                LinkEditRequested?.Invoke(this, hit);
                e.Handled = true;
                return true;
            }
            _linkDrag = (hit, OnLinkHandle(page, sp, hit), pt, hit.Rect);
        }
        else
        {
            _selectedLink = null;
            _linkBoxPage = page;
            _linkBoxStart = _linkBoxEnd = pt;
        }
        Focus();
        _surface.CaptureMouse();
        _surface.InvalidateVisual();
        e.Handled = true;
        return true;
    }

    /// <summary>A link drag in progress (a new box, a move, a resize). True if it was one.</summary>
    private bool MoveLinkDrag(Point sp)
    {
        if (_linkBoxPage >= 0)
        {
            var g = _engine!.GetPage(_linkBoxPage);
            var pt = ToPagePoint(_linkBoxPage, sp);
            _linkBoxEnd = new Point(Math.Clamp(pt.X, 0, g.Width), Math.Clamp(pt.Y, 0, g.Height));
            _surface.InvalidateVisual();
            return true;
        }
        if (_linkDrag is not { } d) return false;
        if (!_dragged) return true;
        var page = d.Link.PageIndex;
        var gp = _engine!.GetPage(page);
        var p = ToPagePoint(page, sp);
        RectD r;
        if (d.Resize)
        {
            // Any shape: a link covers text of any length.
            r = RectD.FromEdges(d.Rect.X, d.Rect.Y, Math.Clamp(p.X, d.Rect.X + 6, Math.Max(d.Rect.X + 6, gp.Width)), Math.Clamp(p.Y, d.Rect.Y + 6, Math.Max(d.Rect.Y + 6, gp.Height)));
        }
        else
        {
            var x = Math.Clamp(d.Rect.X + p.X - d.Start.X, 0, Math.Max(0, gp.Width - d.Rect.Width));
            var y = Math.Clamp(d.Rect.Y + p.Y - d.Start.Y, 0, Math.Max(0, gp.Height - d.Rect.Height));
            r = d.Rect with { X = x, Y = y };
        }
        _linkDrag = d with { Link = d.Link with { Rect = r } };
        _surface.InvalidateVisual();
        return true;
    }

    /// <summary>Ends a link drag. True if there was one.</summary>
    private bool EndLinkDrag()
    {
        if (_linkBoxPage >= 0)
        {
            var page = _linkBoxPage;
            _linkBoxPage = -1;
            _surface.InvalidateVisual();
            var rect = RectD.FromEdges(_linkBoxStart.X, _linkBoxStart.Y, _linkBoxEnd.X, _linkBoxEnd.Y);
            if (!_dragged || rect.Width < 6 || rect.Height < 6)
            {
                // A click: a box the size of a line of text, starting at the pointer.
                var g = _engine!.GetPage(page);
                rect = new RectD(Math.Clamp(_linkBoxStart.X, 0, Math.Max(0, g.Width - 120)), Math.Clamp(_linkBoxStart.Y - 10, 0, Math.Max(0, g.Height - 20)), 120, 20);
            }
            LinkDrawn?.Invoke(this, new LinkPlacement(page, rect));
            return true;
        }
        if (_linkDrag is not { } d) return false;
        _linkDrag = null;
        _surface.InvalidateVisual();
        if (_dragged && d.Link.Rect != d.Rect)
        {
            // Show it at the new place right away; the document catches up when the edit lands.
            if (_linkAnnots.TryGetValue(d.Link.PageIndex, out var list) && list.FindIndex(l => l.Id == d.Link.Id) is >= 0 and var i) list[i] = d.Link;
            LinkChanged?.Invoke(this, new LinkChange(d.Link.Id, d.Link.PageIndex, d.Link.Rect));
        }
        return true;
    }

    private void CancelLinkDrag()
    {
        if (_linkBoxPage < 0 && _linkDrag is null) return;
        _linkBoxPage = -1;
        _linkDrag = null;
        _surface.InvalidateVisual();
    }

    private Cursor? LinkToolCursor(int page, Point sp)
    {
        if (!LinkToolActive || page < 0) return null;
        if (SelectedLink is { } sel && OnLinkHandle(page, sp, sel)) return Cursors.SizeNWSE;
        return LinkAnnotAt(page, ToPagePoint(page, sp)) is not null ? Cursors.SizeAll : Cursors.Cross;
    }

    private bool HandleLinkKey(KeyEventArgs e)
    {
        if (!LinkToolActive || Keyboard.Modifiers != ModifierKeys.None) return false;
        switch (e.Key)
        {
            case Key.Delete or Key.Back when SelectedLink is { } l:
                _selectedLink = null;
                LinkChanged?.Invoke(this, new LinkChange(l.Id, l.PageIndex, null));
                return true;
            case Key.Enter when SelectedLink is { } l:
                LinkEditRequested?.Invoke(this, l);
                return true;
            case Key.Escape when _selectedLink is not null:
                _selectedLink = null;
                _surface.InvalidateVisual();
                return true;
            case Key.Escape:
                Tool = ViewerTool.Hand;
                return true;
            default:
                return false;
        }
    }

    private void PaintLinks(DrawingContext dc, int page)
    {
        if (_linkBoxPage == page)
            dc.DrawRectangle(SigBoxFill, SigBoxPen, ToSurfaceRect(page, RectD.FromEdges(_linkBoxStart.X, _linkBoxStart.Y, _linkBoxEnd.X, _linkBoxEnd.Y)));
        if (!LinkToolActive || !_linkAnnots.TryGetValue(page, out var list)) return;
        foreach (var l in list)
        {
            var shown = _linkDrag is { } d && d.Link.Id == l.Id ? d.Link.Rect : l.Rect;
            var selected = l.Id == _selectedLink;
            dc.DrawRectangle(selected ? SigBoxFill : null, selected ? SigSelectPen : SigBoxPen, ToSurfaceRect(page, shown));
            if (!selected) continue;
            var h = ToSurfacePoint(page, shown.Right, shown.Bottom);
            dc.DrawRectangle(Brushes.White, SigSelectPen, new Rect(h.X - LinkHandleSize / 2, h.Y - LinkHandleSize / 2, LinkHandleSize, LinkHandleSize));
        }
    }

    /// <summary>Dev/test entry: draw a link box (page points) as if with the Link tool.</summary>
    internal void DevDrawLink(int page, RectD rect) => LinkDrawn?.Invoke(this, new LinkPlacement(page, rect));

    internal void DevChangeLink(string id, int page, RectD? rect) => LinkChanged?.Invoke(this, new LinkChange(id, page, rect));

    internal void DevSelectLink(string? id)
    {
        _selectedLink = id;
        _surface.InvalidateVisual();
    }

    internal IReadOnlyList<LinkInfo> DevLinks => [.. _linkAnnots.Values.SelectMany(l => l)];
}
