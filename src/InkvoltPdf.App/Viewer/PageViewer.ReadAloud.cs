using System.Windows;
using System.Windows.Media;
using InkvoltPdf.Core;

namespace InkvoltPdf.App.Viewer;

/// <summary>
/// Read Aloud's highlight (the sentence being read, and the word inside it) and the view position helpers the Read
/// Aloud and bookmark commands use.
/// </summary>
public sealed partial class PageViewer
{
    private int _speechPage = -1;
    private IReadOnlyList<RectD> _speechSentence = [];
    private IReadOnlyList<RectD> _speechWord = [];

    private static readonly Brush SpeechSentenceBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD4)));
    private static readonly Brush SpeechWordBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x00, 0x78, 0xD4)));

    /// <summary>
    /// Where the view is: the current page and the page-space y at the top of the viewport (null when the view is turned,
    /// or the page's top is in view), for a bookmark destination.
    /// </summary>
    public (int Page, double? Top) ViewTarget()
    {
        if (_engine is null || PageCount == 0) return (0, null);
        var page = Math.Clamp(_currentPage, 0, PageCount - 1);
        if (_viewRotation != 0 || page >= _rects.Length || _rects[page].IsEmpty) return (page, null);
        var k = _zoom * PtToDip;
        var y = (VerticalOffset + Pad - _rects[page].Y) / k;
        var height = _engine.GetPage(page).Height;
        return y < 12 ? (page, null) : (page, Math.Round(Math.Min(y, height - 1), 1));
    }

    /// <summary>The selection in reading order, or null without one.</summary>
    public (TextPos From, TextPos To)? SelectionBounds => OrderedSelection() is ({ } a, { } b) ? (a, b) : null;

    /// <summary>Highlights what Read Aloud is saying (page-space rectangles); an empty word keeps just the sentence.</summary>
    public void SetSpeechHighlight(int page, IReadOnlyList<RectD> sentence, IReadOnlyList<RectD> word)
    {
        _speechPage = page;
        _speechSentence = sentence;
        _speechWord = word;
        _surface.InvalidateVisual();
    }

    public void ClearSpeechHighlight()
    {
        if (_speechPage < 0) return;
        _speechPage = -1;
        _speechSentence = _speechWord = [];
        _surface.InvalidateVisual();
    }

    /// <summary>Scrolls just enough to bring a page-space rectangle into view (follow-along reading).</summary>
    public void FollowSpeech(int page, RectD area)
    {
        if (_engine is null || page < 0 || page >= PageCount) return;
        if (_layout == ViewLayout.SinglePage && page != _currentPage) GoToPage(page, record: false);
        if (page >= _rects.Length || _rects[page].IsEmpty) return;
        var sr = ToSurfaceRect(page, area);
        var vp = Viewport;
        var margin = Math.Min(80, vp.Height / 5);
        if (sr.Top < vp.Top + 8 || sr.Bottom > vp.Bottom - margin) ScrollToVerticalOffset(sr.Y - vp.Height / 3);
        if (sr.Left < vp.Left || sr.Right > vp.Right) ScrollToHorizontalOffset(sr.X - vp.Width / 4);
    }

    private void PaintSpeech(DrawingContext dc, int page)
    {
        if (page != _speechPage) return;
        foreach (var r in _speechSentence) dc.DrawRectangle(_highContrast ? _palette.Hit : SpeechSentenceBrush, null, ToSurfaceRect(page, r.Inflate(0.5)));
        foreach (var r in _speechWord) dc.DrawRectangle(_highContrast ? _palette.ActiveHit : SpeechWordBrush, null, ToSurfaceRect(page, r.Inflate(0.8)));
    }
}
