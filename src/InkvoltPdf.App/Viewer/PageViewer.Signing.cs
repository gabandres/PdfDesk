using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using InkvoltPdf.Core;

namespace InkvoltPdf.App.Viewer;

/// <summary>A page area (page space): where a digital signature or a handwritten signature goes.</summary>
public sealed record SignaturePlacement(int PageIndex, RectD Rect);

/// <summary>A handwritten signature stamp or a picture moved or resized (<see cref="Rect"/>), or deleted (null).</summary>
public sealed record SignatureStampChange(string Id, int PageIndex, RectD? Rect);

/// <summary>
/// Signing on top of the page view: dragging the box of a visible digital signature (<see cref="ViewerTool.PlaceSignature"/>),
/// and placing, selecting, moving, resizing and deleting stamps: handwritten signatures (<see cref="ViewerTool.AddSignature"/>),
/// pictures (<see cref="ViewerTool.AddImage"/>) and stamps (<see cref="ViewerTool.AddStamp"/>). The placing tools put the
/// host's pending item where the page is clicked.
/// </summary>
public sealed partial class PageViewer
{
    private static readonly Brush SigBoxFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x20, 0x70, 0xE0)));
    private static readonly Pen SigBoxPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x20, 0x70, 0xE0)), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen SigSelectPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x20, 0x70, 0xE0)), 1.5));
    private const double HandleSize = 9;

    private int _sigBoxPage = -1;
    private Point _sigBoxStart, _sigBoxEnd;
    private Dictionary<int, List<TextStamp>> _sigStamps = [];
    private string? _selectedSig;
    private (TextStamp Stamp, bool Resize, Point Start, RectD Rect)? _sigDrag;

    /// <summary>A visible digital signature's box was dragged with <see cref="ViewerTool.PlaceSignature"/>.</summary>
    public event EventHandler<SignaturePlacement>? SignatureBoxDrawn;

    /// <summary>The <see cref="ViewerTool.AddSignature"/> tool was clicked on an empty spot (rect: the default size, centered there).</summary>
    public event EventHandler<SignaturePlacement>? SignatureStampPlaced;

    public event EventHandler<SignatureStampChange>? SignatureStampChanged;

    /// <summary>The size (points) a handwritten signature gets when placed with a click.</summary>
    public Size SignatureStampSize { get; set; } = new(150, 50);

    public void SetSignatureStamps(IReadOnlyList<TextStamp> stamps)
    {
        _sigStamps = stamps.GroupBy(s => s.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        if (_selectedSig is not null && !stamps.Any(s => s.Id == _selectedSig)) _selectedSig = null;
        _surface.InvalidateVisual();
    }

    private void ResetSigning()
    {
        _sigStamps = [];
        _selectedSig = null;
        _sigDrag = null;
        _sigBoxPage = -1;
    }

    private TextStamp? SigStampAt(int page, Point pt) =>
        page >= 0 && _sigStamps.TryGetValue(page, out var list) ? list.LastOrDefault(s => s.Rect.Inflate(2).Contains(pt.X, pt.Y)) : null;

    private TextStamp? SelectedSig => _selectedSig is null ? null : _sigStamps.Values.SelectMany(l => l).FirstOrDefault(s => s.Id == _selectedSig);

    /// <summary>
    /// Near the selected stamp's resize handle (in surface pixels): its bottom-right corner on the page, which a turned
    /// view shows at another corner.
    /// </summary>
    private bool OnHandle(int page, Point sp, TextStamp s)
    {
        if (s.PageIndex != page) return false;
        var h = ToSurfacePoint(page, s.Rect.Right, s.Rect.Bottom);
        return Math.Abs(sp.X - h.X) <= HandleSize && Math.Abs(sp.Y - h.Y) <= HandleSize;
    }

    /// <summary>A tool whose click places the host's pending stamp (a signature, a picture, a stamp).</summary>
    public bool IsPlacingTool => _tool is ViewerTool.AddSignature or ViewerTool.AddImage or ViewerTool.AddStamp;

    private bool SigStampsActive => _tool == ViewerTool.Hand || IsPlacingTool;

    /// <summary>The kind of a placed stamp shown on the pages (null when it isn't one).</summary>
    public StampKind? PlacedStampKind(string id) => _sigStamps.Values.SelectMany(l => l).FirstOrDefault(s => s.Id == id)?.Kind;

    /// <summary>Selects a placed stamp (e.g. picked in the Comments pane): ready to move, resize or delete. Null clears.</summary>
    public void SelectPlacedStamp(string? id)
    {
        var stamp = id is null ? null : _sigStamps.Values.SelectMany(l => l).FirstOrDefault(s => s.Id == id);
        if (_selectedSig == stamp?.Id) return;
        _selectedSig = stamp?.Id;
        if (stamp is not null && !SigStampsActive) Tool = ViewerTool.Hand;
        _surface.InvalidateVisual();
    }

    /// <summary>Signing presses. True if consumed (the caller captures nothing: this does).</summary>
    private bool HandleSigningDown(MouseButtonEventArgs e, int page, Point sp)
    {
        if (e.ChangedButton != MouseButton.Left || page < 0) return false;
        var pt = ToPagePoint(page, sp);
        if (_tool == ViewerTool.PlaceSignature)
        {
            _sigBoxPage = page;
            _sigBoxStart = _sigBoxEnd = pt;
            Capture();
            return true;
        }
        if (!SigStampsActive) return false;
        var hit = SelectedSig is { } sel && OnHandle(page, sp, sel) ? sel : SigStampAt(page, pt);
        if (hit is not null)
        {
            _selectedSig = hit.Id;
            _sigDrag = (hit, OnHandle(page, sp, hit), pt, hit.Rect);
            Focus();
            Capture();
            _surface.InvalidateVisual();
            return true;
        }
        if (_selectedSig is not null)
        {
            _selectedSig = null;
            _surface.InvalidateVisual();
        }
        if (!IsPlacingTool) return false;
        var size = SignatureStampSize;
        var g = _engine!.GetPage(page);
        var x = Math.Clamp(pt.X - size.Width / 2, 0, Math.Max(0, g.Width - size.Width));
        var y = Math.Clamp(pt.Y - size.Height / 2, 0, Math.Max(0, g.Height - size.Height));
        SignatureStampPlaced?.Invoke(this, new SignaturePlacement(page, new RectD(x, y, size.Width, size.Height)));
        e.Handled = true;
        return true;

        void Capture()
        {
            _surface.CaptureMouse();
            e.Handled = true;
        }
    }

    /// <summary>A drag in progress (box or stamp). True if it was one of ours.</summary>
    private bool MoveSigningDrag(Point sp)
    {
        if (_sigBoxPage >= 0)
        {
            var g = _engine!.GetPage(_sigBoxPage);
            var pt = ToPagePoint(_sigBoxPage, sp);
            _sigBoxEnd = new Point(Math.Clamp(pt.X, 0, g.Width), Math.Clamp(pt.Y, 0, g.Height));
            _surface.InvalidateVisual();
            return true;
        }
        if (_sigDrag is not { } d) return false;
        if (!_dragged) return true;
        var page = d.Stamp.PageIndex;
        var gp = _engine!.GetPage(page);
        var p = ToPagePoint(page, sp);
        RectD r;
        if (d.Resize)
        {
            // Keep the aspect ratio (it's an image): the width follows the pointer.
            var aspect = d.Rect.Height > 0 ? d.Rect.Width / d.Rect.Height : 3;
            var w = Math.Clamp(p.X - d.Rect.X, 12, gp.Width - d.Rect.X);
            r = new RectD(d.Rect.X, d.Rect.Y, w, w / aspect);
        }
        else
        {
            var x = Math.Clamp(d.Rect.X + p.X - d.Start.X, 0, Math.Max(0, gp.Width - d.Rect.Width));
            var y = Math.Clamp(d.Rect.Y + p.Y - d.Start.Y, 0, Math.Max(0, gp.Height - d.Rect.Height));
            r = d.Rect with { X = x, Y = y };
        }
        _sigDrag = d with { Stamp = d.Stamp with { Rect = r } };
        _surface.InvalidateVisual();
        return true;
    }

    /// <summary>Ends a signing drag. True if there was one.</summary>
    private bool EndSigningDrag()
    {
        if (_sigBoxPage >= 0)
        {
            var page = _sigBoxPage;
            _sigBoxPage = -1;
            _surface.InvalidateVisual();
            var rect = RectD.FromEdges(_sigBoxStart.X, _sigBoxStart.Y, _sigBoxEnd.X, _sigBoxEnd.Y);
            if (!_dragged || rect.Width < 8 || rect.Height < 8)
            {
                // A click: a default-size box at the pointer.
                var g = _engine!.GetPage(page);
                rect = new RectD(Math.Clamp(_sigBoxStart.X, 0, Math.Max(0, g.Width - 180)), Math.Clamp(_sigBoxStart.Y, 0, Math.Max(0, g.Height - 50)), 180, 50);
            }
            SignatureBoxDrawn?.Invoke(this, new SignaturePlacement(page, rect));
            return true;
        }
        if (_sigDrag is not { } d) return false;
        _sigDrag = null;
        _surface.InvalidateVisual();
        if (_dragged && d.Stamp.Rect != d.Rect)
        {
            // Show it at the new place right away; the document catches up when the edit lands.
            if (_sigStamps.TryGetValue(d.Stamp.PageIndex, out var list) && list.FindIndex(s => s.Id == d.Stamp.Id) is >= 0 and var i) list[i] = d.Stamp;
            SignatureStampChanged?.Invoke(this, new SignatureStampChange(d.Stamp.Id, d.Stamp.PageIndex, d.Stamp.Rect));
        }
        return true;
    }

    private void CancelSigningDrag()
    {
        if (_sigBoxPage < 0 && _sigDrag is null) return;
        _sigBoxPage = -1;
        _sigDrag = null;
        _surface.InvalidateVisual();
    }

    private Cursor? SigningCursor(int page, Point sp)
    {
        if (_tool == ViewerTool.PlaceSignature) return page >= 0 ? Cursors.Cross : null;
        if (!SigStampsActive || page < 0) return null;
        if (SelectedSig is { } sel && OnHandle(page, sp, sel)) return Cursors.SizeNWSE;
        if (SigStampAt(page, ToPagePoint(page, sp)) is not null) return Cursors.SizeAll;
        return IsPlacingTool ? Cursors.Cross : null;
    }

    private bool HandleSigningKey(KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back && Keyboard.Modifiers == ModifierKeys.None && SelectedSig is { } s)
        {
            _selectedSig = null;
            SignatureStampChanged?.Invoke(this, new SignatureStampChange(s.Id, s.PageIndex, null));
            return true;
        }
        if (e.Key == Key.Escape && (_tool == ViewerTool.PlaceSignature || IsPlacingTool && _selectedSig is null))
        {
            Tool = ViewerTool.Hand;
            return true;
        }
        if (e.Key == Key.Escape && _selectedSig is not null)
        {
            _selectedSig = null;
            _surface.InvalidateVisual();
            return true;
        }
        return false;
    }

    private void PaintSigning(DrawingContext dc, int page)
    {
        if (_sigBoxPage == page)
            dc.DrawRectangle(SigBoxFill, SigBoxPen, ToSurfaceRect(page, RectD.FromEdges(_sigBoxStart.X, _sigBoxStart.Y, _sigBoxEnd.X, _sigBoxEnd.Y)));
        if (_sigDrag is { } d && d.Stamp.PageIndex == page)
            dc.DrawRectangle(SigBoxFill, SigBoxPen, ToSurfaceRect(page, d.Stamp.Rect));
        if (SelectedSig is { } s && s.PageIndex == page)
        {
            var shown = _sigDrag is { } dd && dd.Stamp.Id == s.Id ? dd.Stamp.Rect : s.Rect;
            var r = ToSurfaceRect(page, shown);
            r.Inflate(2, 2);
            dc.DrawRectangle(null, SigSelectPen, r);
            var h = ToSurfacePoint(page, shown.Right, shown.Bottom);
            dc.DrawRectangle(Brushes.White, SigSelectPen, new Rect(h.X - HandleSize / 2, h.Y - HandleSize / 2, HandleSize, HandleSize));
        }
        else if (IsPlacingTool && _sigStamps.TryGetValue(page, out var list))
        {
            foreach (var st in list) dc.DrawRectangle(null, _palette.StampPen, ToSurfaceRect(page, st.Rect.Inflate(1)));
        }
    }

    /// <summary>Dev/test entry: drag a signature box (page points) as if with the mouse.</summary>
    internal void DevSignatureBox(int page, RectD rect) => SignatureBoxDrawn?.Invoke(this, new SignaturePlacement(page, rect));

    /// <summary>Dev/test entry: click with the Add signature tool.</summary>
    internal void DevPlaceSignature(int page, double x, double y) =>
        SignatureStampPlaced?.Invoke(this, new SignaturePlacement(page, new RectD(x - SignatureStampSize.Width / 2, y - SignatureStampSize.Height / 2, SignatureStampSize.Width, SignatureStampSize.Height)));

    internal IReadOnlyList<TextStamp> DevSignatureStamps => [.. _sigStamps.Values.SelectMany(l => l)];

    internal void DevChangeSignature(string id, int page, RectD? rect) => SignatureStampChanged?.Invoke(this, new SignatureStampChange(id, page, rect));
}
