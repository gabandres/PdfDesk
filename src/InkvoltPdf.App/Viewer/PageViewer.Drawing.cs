using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InkvoltPdf.Core;

namespace InkvoltPdf.App.Viewer;

/// <summary>A drawing tool finished a stroke or shape (page space): the host adds it with id <see cref="Id"/>.</summary>
public sealed record DrawingRequest(string Id, int PageIndex, ViewerTool Tool, AnnotGeometry Geometry, AnnotStyle Style);

public enum DrawingEdit { Move, Resize, Reshape, Delete }

/// <summary>A drawing annotation was moved, resized or reshaped (<see cref="Geometry"/>), or deleted (null).</summary>
public sealed record DrawingChange(AnnotationInfo Annotation, DrawingEdit Edit, AnnotGeometry? Geometry);

/// <summary>
/// A text box or callout editor closed. <see cref="Id"/> null: a new one (add it as <see cref="NewId"/>; never empty);
/// else an existing one (empty <see cref="Text"/>: remove it). The flags say what differs from before.
/// </summary>
public sealed record DrawingTextCommit(int PageIndex, string? Id, string NewId, ViewerTool Tool, string Text, AnnotGeometry Geometry, AnnotStyle Style,
    bool TextChanged, bool GeometryChanged, bool StyleChanged);

/// <summary>The open text box / callout editor: where the box is (page space) and how it looks.</summary>
internal sealed record DrawingTextEdit(int Page, RectD Box, bool AutoWidth, string? Id, ViewerTool Tool, AnnotStyle Style, IReadOnlyList<PointD> Callout,
    string Original, AnnotStyle OriginalStyle, RectD OriginalBox);

/// <summary>
/// Drawing on the page (pen, highlighter, rectangle, ellipse, line, arrow, text box, callout) with a live preview, and
/// selecting, moving, resizing, nudging and deleting drawing annotations with the Hand or Select text tool. Everything
/// is worked out in page space through the page ↔ surface mapping, so a turned view and turned pages just work; the
/// host turns each finished gesture into one undoable edit.
/// </summary>
public sealed partial class PageViewer
{
    private const double HandlePx = 8;   // a selection handle's size on screen
    private const double HitPx = 4;      // how near a stroke the pointer must be

    // A drawing in progress
    private int _drawPage = -1;
    private ViewerTool _drawTool;
    private AnnotStyle _drawStyle = new();
    private readonly List<PointD> _ink = [];
    private PointD _drawStart, _drawEnd;
    private bool _drawShift;

    // The selected drawing annotation and its edits
    private enum DragKind { Move, Resize, Endpoint, Tip }
    private sealed record DrawDrag(DragKind Kind, AnnotationInfo Annot, int Handle, PointD Press, Vector Offset, AnnotGeometry Geometry);
    private string? _selDrawing;
    private DrawDrag? _drawDrag;
    private Vector _nudge;              // arrow-key moves not committed yet (page points)
    private string? _nudgeId;
    private DispatcherTimer? _nudgeTimer;
    private DrawingTextEdit? _editorDrawing;

    // Shown until the document catches up (the page bitmap still shows the old state)
    private readonly Dictionary<string, (RectD Before, AnnotGeometry After)> _pendingGeometry = [];
    private readonly List<(string Id, int Page, string Subtype, AnnotGeometry Geometry, AnnotStyle Style)> _pendingNew = [];

    /// <summary>The look each drawing tool draws with (the host remembers it per tool).</summary>
    public Func<ViewerTool, AnnotStyle>? DrawStyleFor { get; set; }

    /// <summary>The document takes annotations (a PDF): the drawing tools' keys work.</summary>
    public bool CanAnnotate { get; set; } = true;

    /// <summary>A panel (the style bar) that may take the focus while a text box is open without closing it.</summary>
    public FrameworkElement? EditorCompanion { get; set; }

    public event EventHandler<DrawingRequest>? DrawingRequested;
    public event EventHandler<DrawingChange>? DrawingChanged;
    public event EventHandler<DrawingTextCommit>? DrawingTextCommitted;

    /// <summary>The selected drawing annotation changed, or a text box editor opened or closed.</summary>
    public event EventHandler? DrawingSelectionChanged;

    public AnnotationInfo? SelectedDrawing => _selDrawing is null ? null : LoadedDrawing(_selDrawing);

    /// <summary>The open text box editor's tool and look (null when none is open).</summary>
    public (ViewerTool Tool, AnnotStyle Style, bool Existing)? EditingText =>
        _editorDrawing is { } d ? (d.Tool, d.Style, d.Id is not null) : null;

    private void InitDrawing()
    {
        // A pen reports far more points than the mouse events promoted from it: take them all for smooth ink.
        _surface.StylusMove += (_, e) =>
        {
            if (_drawPage < 0 || !DrawTools.IsInk(_drawTool)) return;
            foreach (var p in e.GetStylusPoints(_surface)) AddInkPoint(ClampToPage(_drawPage, ToPagePoint(_drawPage, new Point(p.X, p.Y))));
            _surface.InvalidateVisual();
        };
    }

    private AnnotStyle StyleFor(ViewerTool tool) => DrawStyleFor?.Invoke(tool) ?? DrawTools.DefaultStyle(tool);

    private void OnDrawToolChanged()
    {
        CancelDrawingDrag();
        var drawing = DrawTools.IsDrawTool(_tool);
        // A pen draws: no press-and-hold right click or flicks.
        Stylus.SetIsPressAndHoldEnabled(this, !drawing);
        Stylus.SetIsFlicksEnabled(this, !drawing);
        if (drawing || _tool is not (ViewerTool.Hand or ViewerTool.SelectText)) SelectDrawing(null);
        _surface.InvalidateVisual();
    }

    /// <summary>Selects a drawing annotation (null: none). A drawing tool gives way to the hand tool.</summary>
    public void SelectDrawing(string? id)
    {
        if (id == _selDrawing) return;
        FlushNudge();
        if (id is not null && DrawTools.IsDrawTool(_tool)) Tool = ViewerTool.Hand;
        _selDrawing = id;
        _surface.InvalidateVisual();
        DrawingSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The host couldn't make the edit: stop showing its preview.</summary>
    public void ForgetPendingDrawing(string id)
    {
        _pendingNew.RemoveAll(p => p.Id == id);
        _pendingGeometry.Remove(id);
        _surface.InvalidateVisual();
    }

    /// <summary>The open text box editor takes this look (from the style bar).</summary>
    public void SetEditingTextStyle(AnnotStyle style)
    {
        if (_editorDrawing is not { } d || _editor is not TextBox tb) return;
        _editorDrawing = d with { Style = style };
        ApplyEditorLook(tb, style);
        PositionEditor();
        _surface.InvalidateVisual();
    }

    /// <summary>Gives the keyboard focus back to the open editor (after the style bar had it).</summary>
    public void FocusEditor() => _editor?.Focus();

    /// <summary>The drawing tool's look changed: the preview follows.</summary>
    public void RefreshDrawPreview() => _surface.InvalidateVisual();

    private AnnotationInfo? LoadedDrawing(string id)
    {
        foreach (var list in _annotsByPage.Values)
            foreach (var a in list)
                if (a.Id == id && DrawTools.IsDrawing(a)) return a;
        return null;
    }

    private IEnumerable<AnnotationInfo> DrawingsOn(int page) =>
        page >= 0 && _annotsByPage.TryGetValue(page, out var list) ? list.Where(DrawTools.IsDrawing) : [];

    /// <summary>Where a drawing is shown now: while dragged, nudged or waiting for its edit, not where the page draws it.</summary>
    private AnnotGeometry ShownGeometry(AnnotationInfo a)
    {
        if (_drawDrag is { } d && d.Annot.Id == a.Id) return d.Geometry;
        var g = _pendingGeometry.TryGetValue(a.Id, out var pending) ? pending.After : a.Geometry!;
        if (_nudgeId == a.Id && _nudge != default) g = DrawingGeometry.Translate(g, _nudge.X, _nudge.Y);
        return g;
    }

    private AnnotationInfo? DrawingAt(int page, Point pt)
    {
        var p = new PointD(pt.X, pt.Y);
        var tolerance = HitPx / (_zoom * PtToDip);
        AnnotationInfo? hit = null;
        foreach (var a in DrawingsOn(page))
            if (DrawingGeometry.Hit(a.Subtype, ShownGeometry(a), a.Style, p, tolerance)) hit = a; // the last one is on top
        return hit;
    }

    private Point ClampToPage(int page, Point pt)
    {
        var g = _engine!.GetPage(page);
        return new Point(Math.Clamp(pt.X, 0, g.Width), Math.Clamp(pt.Y, 0, g.Height));
    }

    private void AddInkPoint(Point pt)
    {
        var p = new PointD(pt.X, pt.Y);
        if (_ink.Count > 0 && Math.Abs(_ink[^1].X - p.X) < 0.05 && Math.Abs(_ink[^1].Y - p.Y) < 0.05) return;
        _ink.Add(p);
    }

    /// <summary>A surface point in the turned page's own space (points from its top-left as shown).</summary>
    private PointD ToViewPoint(int page, Point sp)
    {
        var r = _rects[page];
        var k = _zoom * PtToDip;
        return new PointD((sp.X - r.X) / k, (sp.Y - r.Y) / k);
    }

    /// <summary>The selection frame in the turned page's own space (points), where resizing happens.</summary>
    private RectD ViewFrame(AnnotationInfo a, AnnotGeometry g)
    {
        var pg = _engine!.GetPage(a.PageIndex);
        return RotatedView.ToView(DrawingGeometry.Frame(a.Subtype, g), pg.Width, pg.Height, _viewRotation);
    }

    /// <summary>The selection frame as painted (surface): around the stroke, a few pixels out.</summary>
    private Rect FrameRect(AnnotationInfo a, AnnotGeometry g)
    {
        var k = _zoom * PtToDip;
        var r = ToSurfaceRect(a.PageIndex, DrawingGeometry.Frame(a.Subtype, g));
        var stroke = a.Subtype == "FreeText" ? 0 : a.Style is { Color: not null } s ? s.Width : 0;
        r.Inflate(stroke * k / 2 + 3, stroke * k / 2 + 3);
        return r;
    }

    private (DragKind Kind, int Handle)? HandleAt(AnnotationInfo a, Point sp)
    {
        var g = ShownGeometry(a);
        var page = a.PageIndex;
        var reach = HandlePx / 2 + 3;
        bool Near(Point p) => Math.Abs(p.X - sp.X) <= reach && Math.Abs(p.Y - sp.Y) <= reach;
        if (a.Subtype == "Line" && g.Paths is [{ Count: 2 } seg])
        {
            for (var i = 0; i < 2; i++)
                if (Near(ToSurfacePoint(page, seg[i].X, seg[i].Y))) return (DragKind.Endpoint, i);
            return null;
        }
        if (a.Subtype == "FreeText" && g.Callout.Count > 0 && Near(ToSurfacePoint(page, g.Callout[0].X, g.Callout[0].Y))) return (DragKind.Tip, 0);
        var fr = FrameRect(a, g);
        var frame = new RectD(fr.X, fr.Y, fr.Width, fr.Height);
        for (var i = 0; i < 8; i++)
        {
            var hp = DrawingGeometry.HandlePoint(frame, i);
            if (Near(new Point(hp.X, hp.Y))) return (DragKind.Resize, i);
        }
        return null;
    }

    // ───────────────────────────── Mouse ─────────────────────────────

    /// <summary>Drawing tools, and selecting / dragging drawings with the hand or select tool. True if consumed.</summary>
    private bool HandleDrawingDown(MouseButtonEventArgs e, int page, Point sp, bool hadTextEditor)
    {
        if (e.ChangedButton != MouseButton.Left || _engine is null) return false;
        FlushNudge();
        if (DrawTools.IsDrawTool(_tool))
        {
            e.Handled = true;
            if (page < 0 || hadTextEditor && DrawTools.IsText(_tool)) return true; // that click only closed the text box
            var pt = ClampToPage(page, ToPagePoint(page, sp));
            if (DrawTools.IsText(_tool) && DrawingAt(page, pt) is { Subtype: "FreeText" } existing)
            {
                OpenDrawingText(existing);
                return true;
            }
            _drawPage = page;
            _drawTool = _tool;
            _drawStyle = StyleFor(_tool);
            _drawStart = _drawEnd = new PointD(pt.X, pt.Y);
            _drawShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            _ink.Clear();
            _ink.Add(_drawStart);
            _surface.CaptureMouse();
            e.StylusDevice?.Capture(_surface);
            _surface.InvalidateVisual();
            return true;
        }
        if (_tool is not (ViewerTool.Hand or ViewerTool.SelectText) || page < 0) return false;

        if (SelectedDrawing is { } sel && sel.PageIndex == page && HandleAt(sel, sp) is { } h)
        {
            StartDrawingDrag(h.Kind, sel, h.Handle, sp, e);
            return true;
        }
        var at = ToPagePoint(page, sp);
        var hit = DrawingAt(page, at);
        if (hit is null)
        {
            if (_selDrawing is not null) SelectDrawing(null);
            return false;
        }
        // The select text tool selects text where there is text (unless the drawing is already selected).
        if (_tool == ViewerTool.SelectText && hit.Id != _selDrawing && TryGetPageText(page) is { } text && text.IsOverText(at.X, at.Y)) return false;
        SelectDrawing(hit.Id);
        if (e.ClickCount >= 2)
        {
            if (hit.Subtype != "FreeText") return false; // the comment layer opens its text
            OpenDrawingText(hit);
            e.Handled = true;
            return true;
        }
        StartDrawingDrag(DragKind.Move, hit, -1, sp, e);
        return true;
    }

    private void StartDrawingDrag(DragKind kind, AnnotationInfo a, int handle, Point sp, MouseButtonEventArgs e)
    {
        var g = ShownGeometry(a);
        var press = ToPagePoint(a.PageIndex, sp);
        var offset = new Vector();
        if (kind == DragKind.Resize)
        {
            // The handle sits a little outside the frame: keep that offset, so the frame doesn't jump to the pointer.
            var hp = DrawingGeometry.HandlePoint(ViewFrame(a, g), handle);
            var vp = ToViewPoint(a.PageIndex, sp);
            offset = new Vector(hp.X - vp.X, hp.Y - vp.Y);
        }
        _drawDrag = new DrawDrag(kind, a with { Geometry = g }, handle, new PointD(press.X, press.Y), offset, g);
        Focus();
        _surface.CaptureMouse();
        e.Handled = true;
        _surface.InvalidateVisual();
    }

    private bool MoveDrawing(MouseEventArgs e, Point sp)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (_drawPage >= 0)
        {
            var pt = ClampToPage(_drawPage, ToPagePoint(_drawPage, sp));
            if (DrawTools.IsInk(_drawTool) && e.StylusDevice is null) AddInkPoint(pt); // a pen's points come from StylusMove
            _drawEnd = new PointD(pt.X, pt.Y);
            _drawShift = shift;
            _surface.InvalidateVisual();
            return true;
        }
        if (_drawDrag is null) return false;
        if (_dragged) UpdateDrawDrag(sp, shift);
        return true;
    }

    /// <summary>The dragged drawing's new geometry for the pointer at <paramref name="sp"/>.</summary>
    private void UpdateDrawDrag(Point sp, bool shift)
    {
        if (_drawDrag is not { } d) return;
        var a = d.Annot;
        var page = a.PageIndex;
        var g0 = a.Geometry!;
        var pg = _engine!.GetPage(page);
        AnnotGeometry g;
        switch (d.Kind)
        {
            case DragKind.Move:
            {
                var pt = ToPagePoint(page, sp);
                double dx = pt.X - d.Press.X, dy = pt.Y - d.Press.Y;
                if (shift)
                {
                    if (Math.Abs(dx) > Math.Abs(dy)) dy = 0;
                    else dx = 0;
                }
                (dx, dy) = DrawingGeometry.ClampMove(g0.Bounds, dx, dy, pg.Width, pg.Height);
                g = DrawingGeometry.Translate(g0, dx, dy);
                break;
            }
            case DragKind.Resize:
            {
                var vp = ToViewPoint(page, sp);
                vp = new PointD(vp.X + d.Offset.X, vp.Y + d.Offset.Y);
                var view = DrawingGeometry.Resize(ViewFrame(a, g0), d.Handle, vp, a.Subtype == "FreeText" ? 12 : 4, keepAspect: shift);
                g = DrawingGeometry.WithFrame(a.Subtype, g0, RotatedView.FromView(view, pg.Width, pg.Height, _viewRotation));
                break;
            }
            case DragKind.Endpoint:
            {
                var pt = ClampToPage(page, ToPagePoint(page, sp));
                var p = new PointD(pt.X, pt.Y);
                var other = g0.Paths[0][1 - d.Handle];
                if (shift) p = DrawingGeometry.SnapAngle(other, p);
                IReadOnlyList<PointD> segment = d.Handle == 0 ? [p, other] : [other, p];
                g = new AnnotGeometry { Paths = [segment] };
                break;
            }
            default:
            {
                var pt = ClampToPage(page, ToPagePoint(page, sp));
                g = new AnnotGeometry { Box = g0.Box, Paths = g0.Paths, Callout = [new PointD(pt.X, pt.Y), .. g0.Callout.Skip(1)] };
                break;
            }
        }
        _drawDrag = d with { Geometry = g };
        _surface.InvalidateVisual();
    }

    /// <summary>Ends a drawing gesture or a drag of a drawing. True if there was one.</summary>
    private bool EndDrawingDrag(Point sp)
    {
        if (_drawPage >= 0)
        {
            var page = _drawPage;
            // Where the button came up is the end (move events can lag behind or be merged).
            var end = ClampToPage(page, ToPagePoint(page, sp));
            if (DrawTools.IsInk(_drawTool)) AddInkPoint(end);
            if (_dragged) _drawEnd = new PointD(end.X, end.Y);
            _drawShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            _drawPage = -1;
            FinishDrawing(page);
            _ink.Clear();
            _surface.InvalidateVisual();
            return true;
        }
        if (_drawDrag is null) return false;
        if (_dragged) UpdateDrawDrag(sp, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        var d = _drawDrag;
        _drawDrag = null;
        _surface.InvalidateVisual();
        if (!_dragged || LoadedDrawing(d.Annot.Id) is not { } loaded) return true;
        var g = d.Geometry;
        if (loaded.Subtype == "FreeText" && d.Kind == DragKind.Resize)
        {
            // A narrower box wraps the text onto more lines: grow it so nothing is cut off.
            var need = Annotations.FreeTextHeight(loaded.Contents, g.Box.Width, loaded.Style!);
            if (g.Box.Height < need) g = new AnnotGeometry { Box = g.Box with { Height = need }, Callout = g.Callout };
        }
        if (g.Bounds == d.Annot.Geometry!.Bounds) return true;
        _pendingGeometry[loaded.Id] = (loaded.Geometry!.Bounds, g);
        DrawingChanged?.Invoke(this, new DrawingChange(loaded, d.Kind switch
        {
            DragKind.Move => DrawingEdit.Move,
            DragKind.Resize => DrawingEdit.Resize,
            _ => DrawingEdit.Reshape,
        }, g));
        return true;
    }

    private void CancelDrawingDrag()
    {
        if (_drawPage < 0 && _drawDrag is null) return;
        _drawPage = -1;
        _drawDrag = null;
        _ink.Clear();
        _surface.InvalidateVisual();
    }

    /// <summary>A drawing tool's gesture ended: hand the shape to the host, or open a text box.</summary>
    private void FinishDrawing(int page)
    {
        var tool = _drawTool;
        var s = _drawStyle;
        var k = _zoom * PtToDip;
        var pg = _engine!.GetPage(page);
        AnnotGeometry g;
        switch (tool)
        {
            case ViewerTool.Pen or ViewerTool.Highlighter:
            {
                IReadOnlyList<PointD> pts = DrawingGeometry.Simplify(_ink, 0.5 / k);
                if (pts.Count < 2 || pts.All(p => p == pts[0])) pts = [_ink[0], new PointD(_ink[0].X + 0.5, _ink[0].Y)]; // a dot
                g = new AnnotGeometry { Paths = [pts] };
                break;
            }
            case ViewerTool.Rectangle or ViewerTool.Ellipse:
            {
                var box = DrawingGeometry.DragBox(_drawStart, _drawEnd, _drawShift);
                if (!_dragged || box.Width < 2 || box.Height < 2) return;
                g = new AnnotGeometry { Box = box };
                break;
            }
            case ViewerTool.Line or ViewerTool.Arrow:
            {
                var end = _drawShift ? DrawingGeometry.SnapAngle(_drawStart, _drawEnd) : _drawEnd;
                if (!_dragged || Math.Abs(end.X - _drawStart.X) + Math.Abs(end.Y - _drawStart.Y) < 2) return;
                g = new AnnotGeometry { Paths = [[_drawStart, end]] };
                break;
            }
            case ViewerTool.TextBox:
            {
                var box = DrawingGeometry.DragBox(_drawStart, _drawEnd, false);
                if (_dragged && box.Width >= 8)
                    OpenNewText(page, tool, s, box with { Height = Math.Max(box.Height, 4) }, autoWidth: false, []);
                else // a click: a typewriter line whose baseline sits about where the user clicked
                    OpenNewText(page, tool, s, new RectD(_drawStart.X, Math.Max(0, _drawStart.Y - s.FontSize * 0.8), 0, s.FontSize * 1.2), autoWidth: true, []);
                return;
            }
            default: // Callout: pressed at what it points to, released where the text goes
            {
                const double width = 144;
                var tip = _drawStart;
                var height = Annotations.FreeTextHeight("Xg", width, s);
                double x, y;
                if (_dragged && Math.Abs(_drawEnd.X - tip.X) + Math.Abs(_drawEnd.Y - tip.Y) > 8)
                {
                    x = _drawEnd.X >= tip.X ? _drawEnd.X : _drawEnd.X - width;
                    y = _drawEnd.Y - height / 2;
                }
                else
                {
                    x = tip.X + 36;
                    y = tip.Y - 36 - height;
                }
                x = Math.Clamp(x, 0, Math.Max(0, pg.Width - width));
                y = Math.Clamp(y, 0, Math.Max(0, pg.Height - height));
                OpenNewText(page, tool, s, new RectD(x, y, width, height), autoWidth: false, [tip]);
                return;
            }
        }
        var id = Annotations.NewId();
        _pendingNew.Add((id, page, DrawTools.Subtype(tool), g, s));
        DrawingRequested?.Invoke(this, new DrawingRequest(id, page, tool, g, s));
    }

    private Cursor? DrawingCursor(int page, Point sp)
    {
        if (DrawTools.IsDrawTool(_tool))
        {
            if (page < 0) return null;
            if (DrawTools.IsText(_tool)) return DrawingAt(page, ToPagePoint(page, sp)) is { Subtype: "FreeText" } ? Cursors.IBeam : Cursors.Cross;
            return DrawTools.IsInk(_tool) ? Cursors.Pen : Cursors.Cross;
        }
        if (_tool is not (ViewerTool.Hand or ViewerTool.SelectText) || page < 0) return null;
        if (SelectedDrawing is { } sel && sel.PageIndex == page && HandleAt(sel, sp) is { } h)
            return h.Kind != DragKind.Resize ? Cursors.Cross : h.Handle switch
            {
                DrawingGeometry.TopLeft or DrawingGeometry.BottomRight => Cursors.SizeNWSE,
                DrawingGeometry.TopRight or DrawingGeometry.BottomLeft => Cursors.SizeNESW,
                DrawingGeometry.Top or DrawingGeometry.Bottom => Cursors.SizeNS,
                _ => Cursors.SizeWE,
            };
        var pt = ToPagePoint(page, sp);
        if (DrawingAt(page, pt) is not { } a) return null;
        if (_tool == ViewerTool.SelectText && a.Id != _selDrawing && _texts.TryGetValue(page, out var t) && t.IsOverText(pt.X, pt.Y)) return null;
        return Cursors.SizeAll;
    }

    // ───────────────────────────── Keyboard ─────────────────────────────

    /// <summary>Esc cancels a gesture; the selected drawing: Delete, arrows nudge (Shift: 10 pt), Enter edits, Esc deselects.</summary>
    private bool HandleDrawingKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_drawPage >= 0 || _drawDrag is not null))
        {
            CancelDrawingDrag();
            EndDrag();
            return true;
        }
        if (SelectedDrawing is not { } a) return false;
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Delete or Key.Back when mods == ModifierKeys.None:
                DiscardNudge();
                _selDrawing = null;
                _surface.InvalidateVisual();
                DrawingSelectionChanged?.Invoke(this, EventArgs.Empty);
                DrawingChanged?.Invoke(this, new DrawingChange(a, DrawingEdit.Delete, null));
                return true;
            case Key.Escape when mods == ModifierKeys.None:
                SelectDrawing(null);
                return true;
            case Key.Enter when mods == ModifierKeys.None:
                if (a.Subtype == "FreeText") OpenDrawingText(a);
                else AnnotationActivated?.Invoke(this, a);
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when mods is ModifierKeys.None or ModifierKeys.Shift:
                Nudge(a, e.Key, mods == ModifierKeys.Shift ? 10 : 1);
                return true;
        }
        return false;
    }

    /// <summary>Arrow keys move the selection the way the page is shown; the move is written once the keys rest.</summary>
    private void Nudge(AnnotationInfo a, Key key, double step)
    {
        if (_nudgeId != a.Id)
        {
            FlushNudge();
            _nudgeId = a.Id;
            _nudge = default;
        }
        var (vx, vy) = key switch { Key.Left => (-step, 0.0), Key.Right => (step, 0.0), Key.Up => (0.0, -step), _ => (0.0, step) };
        var pg = _engine!.GetPage(a.PageIndex);
        var (x0, y0) = RotatedView.FromView(0, 0, pg.Width, pg.Height, _viewRotation);
        var (x1, y1) = RotatedView.FromView(vx, vy, pg.Width, pg.Height, _viewRotation);
        var start = _pendingGeometry.TryGetValue(a.Id, out var pending) ? pending.After : a.Geometry!;
        var (dx, dy) = DrawingGeometry.ClampMove(start.Bounds, _nudge.X + x1 - x0, _nudge.Y + y1 - y0, pg.Width, pg.Height);
        _nudge = new Vector(dx, dy);
        if (_nudgeTimer is null)
        {
            _nudgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _nudgeTimer.Tick += (_, _) => FlushNudge();
        }
        _nudgeTimer.Stop();
        _nudgeTimer.Start();
        _surface.InvalidateVisual();
    }

    private void FlushNudge()
    {
        _nudgeTimer?.Stop();
        if (_nudgeId is not { } id || _nudge == default)
        {
            DiscardNudge();
            return;
        }
        var v = _nudge;
        DiscardNudge();
        if (LoadedDrawing(id) is not { } a) return;
        var start = _pendingGeometry.TryGetValue(id, out var pending) ? pending.After : a.Geometry!;
        var g = DrawingGeometry.Translate(start, v.X, v.Y);
        _pendingGeometry[id] = (a.Geometry!.Bounds, g);
        _surface.InvalidateVisual();
        DrawingChanged?.Invoke(this, new DrawingChange(a, DrawingEdit.Move, g));
    }

    private void DiscardNudge()
    {
        _nudgeTimer?.Stop();
        _nudgeId = null;
        _nudge = default;
    }

    // ───────────────────────────── Text boxes ─────────────────────────────

    private static double TextPad(AnnotStyle s) => (s.Color is null ? 0 : s.Width) + 2;

    private void OpenNewText(int page, ViewerTool tool, AnnotStyle style, RectD box, bool autoWidth, IReadOnlyList<PointD> callout) =>
        OpenDrawingTextEditor(new DrawingTextEdit(page, box, autoWidth, null, tool, style, callout, "", style, box), "");

    private void OpenDrawingText(AnnotationInfo a)
    {
        var g = ShownGeometry(a);
        var tool = DrawTools.ToolOf(a) ?? ViewerTool.TextBox;
        OpenDrawingTextEditor(new DrawingTextEdit(a.PageIndex, g.Box, false, a.Id, tool, a.Style!, g.Callout, a.Contents, a.Style!, g.Box), a.Contents);
    }

    private void OpenDrawingTextEditor(DrawingTextEdit d, string text)
    {
        var box = new TextBox
        {
            Text = text,
            FontFamily = new FontFamily("Arial"),
            AcceptsReturn = true,
            TextWrapping = d.AutoWidth ? TextWrapping.NoWrap : TextWrapping.Wrap,
            MinHeight = 0,
            MinWidth = 0,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            ToolTip = "Type, then click elsewhere or press Esc. Enter adds a line; an empty box is removed.",
        };
        box.SetResourceReference(BorderBrushProperty, "AccentFillColorDefaultBrush");
        AutomationProperties.SetName(box, d.Callout.Count > 0 ? "Callout text" : "Text box text");
        ApplyEditorLook(box, d.Style);
        ShowEditor(box, null, null, d);
        box.CaretIndex = box.Text.Length;
        DrawingSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ApplyEditorLook(TextBox box, AnnotStyle s)
    {
        var text = Frozen(new SolidColorBrush(ToColor(s.TextColor, 1)));
        box.Foreground = text;
        box.CaretBrush = text;
        box.Background = Frozen(new SolidColorBrush(s.Fill is { } f ? ToColor(f, 0.95) : Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)));
    }

    private void PositionDrawingEditor(DrawingTextEdit d, double k)
    {
        if (_editor is not TextBox tb) return;
        var tl = ToSurfacePoint(d.Page, d.Box.X, d.Box.Y); // a turned view re-places it (TurnEditor)
        Canvas.SetLeft(tb, tl.X - 1);
        Canvas.SetTop(tb, tl.Y - 1);
        tb.FontSize = Math.Max(4, d.Style.FontSize * k);
        var pad = Math.Max(0, TextPad(d.Style) * k - 1);
        tb.Padding = new Thickness(pad);
        if (d.AutoWidth)
        {
            tb.Width = double.NaN;
            tb.MinWidth = Math.Max(24, 40 * k);
            tb.MinHeight = 0;
        }
        else
        {
            tb.Width = d.Box.Width * k + 2;
            tb.MinHeight = d.Box.Height * k + 2;
        }
    }

    private void CommitDrawingText(DrawingTextEdit d, string raw)
    {
        var text = raw.Replace("\r\n", "\n").TrimEnd();
        if (d.Id is null && text.Trim().Length == 0) return; // nothing typed: nothing created
        var style = d.Style;
        RectD box;
        if (text.Length == 0)
        {
            box = d.Box;
        }
        else if (d.AutoWidth)
        {
            var width = MeasureText(text, style.FontSize) + 2 * TextPad(style) + style.FontSize * 0.5;
            var pg = _engine!.GetPage(d.Page);
            box = new RectD(Math.Clamp(d.Box.X, 0, Math.Max(0, pg.Width - width)), Math.Max(0, d.Box.Y), width, Annotations.FreeTextHeight(text, width, style));
        }
        else
        {
            box = d.Box with { Height = Math.Max(d.Box.Height, Annotations.FreeTextHeight(text, d.Box.Width, style)) };
        }
        var g = new AnnotGeometry { Box = box, Callout = d.Callout };
        DrawingTextCommitted?.Invoke(this, new DrawingTextCommit(d.Page, d.Id, d.Id ?? Annotations.NewId(), d.Tool, text, g, style,
            text != d.Original, box != d.OriginalBox, style != d.OriginalStyle));
    }

    private static double MeasureText(string text, double size)
    {
        var typeface = new Typeface("Arial");
        var width = 0.0;
        foreach (var line in text.Split('\n'))
        {
            var ft = new FormattedText(line.Length == 0 ? " " : line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1.0);
            width = Math.Max(width, ft.WidthIncludingTrailingWhitespace);
        }
        return width;
    }

    // ───────────────────────────── Reloads ─────────────────────────────

    /// <summary>The annotations were re-read: drop previews the document now shows, and a selection that is gone.</summary>
    private void OnDrawingsReloaded()
    {
        foreach (var id in _pendingGeometry.Keys.ToList())
            if (LoadedDrawing(id) is not { } a || a.Geometry!.Bounds != _pendingGeometry[id].Before) _pendingGeometry.Remove(id);
        _pendingNew.RemoveAll(p => LoadedDrawing(p.Id) is not null);
        if (_selDrawing is not null && SelectedDrawing is null) _selDrawing = null;
        _surface.InvalidateVisual();
        DrawingSelectionChanged?.Invoke(this, EventArgs.Empty); // its look may have changed
    }

    private void ResetDrawing()
    {
        CancelDrawingDrag();
        DiscardNudge();
        _pendingNew.Clear();
        _pendingGeometry.Clear();
    }

    // ───────────────────────────── Painting ─────────────────────────────

    private static Color ToColor(AnnotColor c, double alpha) => Color.FromArgb(
        (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(c.R, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.G, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.B, 0, 1) * 255));

    private void PaintDrawing(DrawingContext dc, int page)
    {
        foreach (var p in _pendingNew)
            if (p.Page == page) DrawShape(dc, page, p.Subtype, p.Geometry, p.Style, 1);
        if (_drawPage == page) PaintDrawingPreview(dc, page);
        foreach (var a in DrawingsOn(page))
        {
            // Moved or resized: shown at the new place until the page is redrawn with it there.
            var moved = _drawDrag is { } d && d.Annot.Id == a.Id && _dragged || _pendingGeometry.ContainsKey(a.Id) || _nudgeId == a.Id && _nudge != default;
            if (moved) DrawShape(dc, page, a.Subtype, ShownGeometry(a), a.Style!, 0.8);
        }
        if (_editorDrawing is { Callout.Count: > 0 } ed && ed.Page == page)
        {
            var tip = ed.Callout[0];
            var to = DrawingGeometry.NearestOnRect(ed.Box, tip);
            var pen = Frozen(new Pen(new SolidColorBrush(ToColor(ed.Style.Color ?? ed.Style.TextColor, 1)), Math.Max(1, ed.Style.Width * _zoom * PtToDip)));
            dc.DrawLine(pen, ToSurfacePoint(page, tip.X, tip.Y), ToSurfacePoint(page, to.X, to.Y));
        }
        if (SelectedDrawing is { } sel && sel.PageIndex == page && _editorDrawing?.Id != sel.Id) PaintSelection(dc, sel);
    }

    private void PaintDrawingPreview(DrawingContext dc, int page)
    {
        var s = _drawStyle;
        switch (_drawTool)
        {
            case ViewerTool.Pen or ViewerTool.Highlighter:
                DrawShape(dc, page, "Ink", new AnnotGeometry { Paths = [[.. _ink]] }, s, 1);
                break;
            case ViewerTool.Rectangle or ViewerTool.Ellipse:
                DrawShape(dc, page, DrawTools.Subtype(_drawTool), new AnnotGeometry { Box = DrawingGeometry.DragBox(_drawStart, _drawEnd, _drawShift) }, s, 1);
                break;
            case ViewerTool.Line or ViewerTool.Arrow:
                DrawShape(dc, page, "Line", new AnnotGeometry { Paths = [[_drawStart, _drawShift ? DrawingGeometry.SnapAngle(_drawStart, _drawEnd) : _drawEnd]] }, s, 1);
                break;
            case ViewerTool.TextBox when _dragged:
                dc.DrawRectangle(_palette.SnapFill, _palette.SnapPen, ToSurfaceRect(page, DrawingGeometry.DragBox(_drawStart, _drawEnd, false)));
                break;
            case ViewerTool.Callout when _dragged:
            {
                var pen = Frozen(new Pen(new SolidColorBrush(ToColor(s.Color ?? s.TextColor, 1)), Math.Max(1, s.Width * _zoom * PtToDip)));
                dc.DrawLine(pen, ToSurfacePoint(page, _drawStart.X, _drawStart.Y), ToSurfacePoint(page, _drawEnd.X, _drawEnd.Y));
                break;
            }
        }
    }

    /// <summary>Draws a drawing annotation's shape the way the PDF will (preview: no blend modes, no text).</summary>
    private void DrawShape(DrawingContext dc, int page, string subtype, AnnotGeometry g, AnnotStyle s, double alpha)
    {
        var k = _zoom * PtToDip;
        var opacity = Math.Clamp(s.Opacity, 0, 1) * alpha * (s.Highlighter ? 0.45 : 1);
        Brush? Solid(AnnotColor? c) => c is { } col ? Frozen(new SolidColorBrush(ToColor(col, opacity))) : null;
        var strokeBrush = s.Width > 0 ? Solid(s.Color) : null;
        var width = Math.Max(1, s.Width * k);
        Pen? pen = null;
        if (strokeBrush is not null)
        {
            pen = new Pen(strokeBrush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            if (s.Dash is { Count: > 0 } dash && s.Width > 0) pen.DashStyle = new DashStyle(dash.Select(v => v / s.Width), 0);
            pen.Freeze();
        }
        var fill = Solid(s.Fill);
        Point P(PointD p) => ToSurfacePoint(page, p.X, p.Y);
        switch (subtype)
        {
            case "Ink" or "PolyLine" or "Polygon":
            {
                var closed = subtype == "Polygon";
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                    foreach (var path in g.Paths.Where(p => p.Count > 0))
                    {
                        ctx.BeginFigure(P(path[0]), closed && fill is not null, closed);
                        ctx.PolyLineTo(path.Count > 1 ? [.. path.Skip(1).Select(P)] : new List<Point> { P(path[0]) }, true, true);
                    }
                geo.Freeze();
                dc.DrawGeometry(closed ? fill : null, pen, geo);
                break;
            }
            case "Line" when g.Paths is [{ Count: 2 } seg] && pen is not null:
            {
                Point a = P(seg[0]), b = P(seg[1]);
                dc.DrawLine(pen, a, b);
                Ending(dc, pen, fill, s.StartEnding, a, b);
                Ending(dc, pen, fill, s.EndEnding, b, a);
                break;
            }
            case "Square":
            {
                var r = ToSurfaceRect(page, g.Box);
                if (pen is not null && r.Width > width && r.Height > width) r.Inflate(-width / 2, -width / 2);
                dc.DrawRectangle(fill, pen, r);
                break;
            }
            case "Circle":
            {
                var r = ToSurfaceRect(page, g.Box);
                if (pen is not null && r.Width > width && r.Height > width) r.Inflate(-width / 2, -width / 2);
                dc.DrawEllipse(fill, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                break;
            }
            case "FreeText":
            {
                dc.DrawRectangle(fill, pen ?? _palette.StampPen, ToSurfaceRect(page, g.Box));
                if (g.Callout.Count > 0)
                {
                    var leader = Frozen(new Pen(Solid(s.Color ?? s.TextColor), width));
                    var pts = g.Callout.Append(DrawingGeometry.NearestOnRect(g.Box, g.Callout[^1])).Select(P).ToList();
                    for (var i = 1; i < pts.Count; i++) dc.DrawLine(leader, pts[i - 1], pts[i]);
                }
                break;
            }
        }
    }

    /// <summary>A line ending at <paramref name="at"/>, the line coming from <paramref name="from"/> (surface space).</summary>
    private void Ending(DrawingContext dc, Pen pen, Brush? fill, LineEnding ending, Point at, Point from)
    {
        if (ending == LineEnding.None) return;
        var d = at - from;
        if (d.Length < 0.01) return;
        d.Normalize();
        var size = Math.Max(6, pen.Thickness / (_zoom * PtToDip) * 4 + 2) * _zoom * PtToDip;
        Vector Turn(Vector v, double deg)
        {
            var r = deg * Math.PI / 180;
            return new Vector(v.X * Math.Cos(r) - v.Y * Math.Sin(r), v.X * Math.Sin(r) + v.Y * Math.Cos(r));
        }
        switch (ending)
        {
            case LineEnding.OpenArrow or LineEnding.ClosedArrow:
            {
                var w1 = at - Turn(d, 30) * size;
                var w2 = at - Turn(d, -30) * size;
                if (ending == LineEnding.OpenArrow)
                {
                    dc.DrawLine(pen, at, w1);
                    dc.DrawLine(pen, at, w2);
                }
                else
                {
                    var tri = new StreamGeometry();
                    using (var ctx = tri.Open())
                    {
                        ctx.BeginFigure(at, true, true);
                        ctx.PolyLineTo([w1, w2], true, true);
                    }
                    tri.Freeze();
                    dc.DrawGeometry(fill ?? pen.Brush, pen, tri);
                }
                break;
            }
            case LineEnding.Circle:
                dc.DrawEllipse(fill, pen, at, size / 2, size / 2);
                break;
            default:
                dc.DrawRectangle(fill, pen, new Rect(at.X - size / 2, at.Y - size / 2, size, size));
                break;
        }
    }

    /// <summary>The selection frame (theme accent; system highlight in high contrast) with its handles.</summary>
    private void PaintSelection(DrawingContext dc, AnnotationInfo a)
    {
        var accent = _highContrast ? SystemColors.HighlightBrush : TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.RoyalBlue;
        var handleFill = _highContrast ? SystemColors.WindowBrush : TryFindResource("ControlSolidFillColorDefaultBrush") as Brush ?? Brushes.White;
        var framePen = Frozen(new Pen(accent, _highContrast ? 2 : 1));
        var handlePen = Frozen(new Pen(accent, 1.5));
        var g = ShownGeometry(a);
        var page = a.PageIndex;
        var radius = HandlePx / 2 + 1;
        if (a.Subtype == "Line" && g.Paths is [{ Count: 2 } seg])
        {
            foreach (var p in seg) dc.DrawEllipse(handleFill, handlePen, ToSurfacePoint(page, p.X, p.Y), radius, radius);
            return;
        }
        var fr = FrameRect(a, g);
        dc.DrawRectangle(null, framePen, fr);
        var frame = new RectD(fr.X, fr.Y, fr.Width, fr.Height);
        for (var i = 0; i < 8; i++)
        {
            var hp = DrawingGeometry.HandlePoint(frame, i);
            dc.DrawRoundedRectangle(handleFill, handlePen, new Rect(hp.X - HandlePx / 2, hp.Y - HandlePx / 2, HandlePx, HandlePx), 2, 2);
        }
        if (a.Subtype == "FreeText" && g.Callout.Count > 0)
            dc.DrawEllipse(handleFill, handlePen, ToSurfacePoint(page, g.Callout[0].X, g.Callout[0].Y), radius, radius);
    }

    // ───────────────────────────── Dev entries ─────────────────────────────

    internal IReadOnlyList<AnnotationInfo> DevDrawings => [.. _annotsByPage.Values.SelectMany(l => l).Where(DrawTools.IsDrawing)];
}
