using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using PdfDesk.Core;

namespace PdfDesk.App.Viewer;

/// <summary>Text layers (fetched in the background), text selection by mouse or keyboard (caret browsing), copying.</summary>
public sealed partial class PageViewer
{
    private bool _caretBrowsing;

    public bool HasSelection => _selAnchor is { } a && _selActive is { } b && a != b;
    public bool SelectionIsOnOnePage => _selAnchor is { } a && _selActive is { } b && a.Page == b.Page;

    /// <summary>Number of pages the selection touches (0 without a selection).</summary>
    public int SelectedPageCount => OrderedSelection() is ({ } a, { } b) ? Math.Min(b.Page, PageCount - 1) - a.Page + 1 : 0;

    private (TextPos?, TextPos?) OrderedSelection()
    {
        if (_selAnchor is not { } a || _selActive is not { } b || a == b) return (null, null);
        return a.CompareTo(b) <= 0 ? (a, b) : (b, a);
    }

    public void ClearSelection()
    {
        if (_selAnchor is null && _selActive is null) return;
        _selAnchor = _selActive = _caretBrowsing ? _selActive : null; // caret browsing keeps the caret
        _surface.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects all text in the document (instant: page text is only read when copied or shown).</summary>
    public void SelectAll()
    {
        if (_engine is null || PageCount == 0) return;
        if (_tool != ViewerTool.SelectText) Tool = ViewerTool.SelectText;
        _selAnchor = new TextPos(0, 0);
        _selActive = new TextPos(PageCount - 1, int.MaxValue);
        _surface.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (PageCount > LargeCopyPages) StatusMessage?.Invoke(this, $"Selected the text of all {PageCount:N0} pages");
    }

    private const int LargeCopyPages = 20;

    /// <summary>
    /// The selected text, read synchronously (pages not yet loaded are extracted on the calling thread). Fine for a few
    /// pages; for long selections use <see cref="GetSelectedTextAsync"/> or <see cref="CopySelectionAsync"/>.
    /// </summary>
    public string GetSelectedText()
    {
        var (from, to) = OrderedSelection();
        if (from is not { } a || to is not { } b || _engine is null) return "";
        var engine = _engine;
        var cacheNew = b.Page - a.Page < 8;
        return BuildText(a, b, p => _texts.TryGetValue(p, out var t) ? t : engine.GetText(p, cacheNew), PageCount, null, default);
    }

    /// <summary>The selected text, extracted on a background thread. Reports (pages done, total pages).</summary>
    /// <exception cref="ObjectDisposedException">The document was edited or closed meanwhile.</exception>
    public async Task<string> GetSelectedTextAsync(IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        var (from, to) = OrderedSelection();
        if (from is not { } a || to is not { } b || _engine is null) return "";
        var engine = _engine;
        var count = PageCount;
        var cached = new Dictionary<int, PageText>();
        for (var p = a.Page; p <= b.Page && p < count; p++)
            if (_texts.TryGetValue(p, out var t)) cached[p] = t;
        var total = Math.Min(b.Page, count - 1) - a.Page + 1;
        return await Task.Run(() => BuildText(a, b, p => cached.TryGetValue(p, out var t) ? t : engine.GetText(p, cache: false), count,
            done => progress?.Report((done, total)), cancel), cancel);
    }

    /// <summary>
    /// Copies the selected text to the clipboard without blocking the UI (pages are read in the background) and reports
    /// the outcome through <see cref="StatusMessage"/>. Returns the number of characters copied.
    /// </summary>
    public async Task<int> CopySelectionAsync(CancellationToken cancel = default)
    {
        var pages = SelectedPageCount;
        if (pages == 0) return 0;
        if (pages > LargeCopyPages) StatusMessage?.Invoke(this, $"Copying the text of {pages:N0} pages…");
        string text;
        try
        {
            text = await GetSelectedTextAsync(null, cancel);
        }
        catch (ObjectDisposedException)
        {
            StatusMessage?.Invoke(this, "Couldn't copy: the document changed meanwhile. Try again.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        if (text.Length == 0) return 0;
        try
        {
            Clipboard.SetText(text);
            StatusMessage?.Invoke(this, $"Copied {text.Length:N0} characters");
        }
        catch (COMException ex)
        {
            StatusMessage?.Invoke(this, $"Couldn't copy: {ex.Message}");
            return 0;
        }
        return text.Length;
    }

    private static string BuildText(TextPos a, TextPos b, Func<int, PageText> textOf, int pageCount, Action<int>? pageDone, CancellationToken cancel)
    {
        var sb = new System.Text.StringBuilder();
        for (var p = a.Page; p <= b.Page && p < pageCount; p++)
        {
            cancel.ThrowIfCancellationRequested();
            var text = textOf(p);
            var start = p == a.Page ? a.Caret : 0;
            var end = p == b.Page ? Math.Min(b.Caret, text.Text.Length) : text.Text.Length;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text.GetString(start, end));
            pageDone?.Invoke(p - a.Page + 1);
        }
        return sb.ToString().Replace("\n", Environment.NewLine);
    }

    /// <summary>The page's text if loaded; otherwise asks for it (soon) and returns null.</summary>
    private PageText? TryGetPageText(int page)
    {
        if (_texts.TryGetValue(page, out var t)) return t;
        RequestText(page, 0);
        return null;
    }

    /// <summary>Loads a page's text layer on the background lane (lower <paramref name="priority"/> = sooner).</summary>
    private void RequestText(int page, double priority)
    {
        if (_engine is null || _scheduler is null || page < 0 || page >= PageCount || _texts.ContainsKey(page)) return;
        var engine = _engine;
        var gen = _generation;
        _scheduler.Enqueue($"x{page}", RenderScheduler.BackgroundPriority + priority, _ => engine.GetText(page), result =>
        {
            if (gen != _generation || result is not PageText t) return;
            _texts[page] = t;
            _surface.InvalidateVisual();
            if (_surface.IsMouseOver && !_surface.IsMouseCaptured) UpdateCursor(Mouse.GetPosition(_surface));
            _peer?.TextArrived(page);
        }, tag: gen);
    }

    // ───────────────────────────── Caret browsing ─────────────────────────────

    /// <summary>
    /// Caret browsing (F7): a text caret moves with the arrow keys (Ctrl: by word; Home/End: line; Ctrl+Home/End:
    /// document) and Shift extends the selection. With the Select tool, Shift+arrows select even without it.
    /// </summary>
    public bool CaretBrowsing
    {
        get => _caretBrowsing;
        set
        {
            if (_caretBrowsing == value) return;
            _caretBrowsing = value;
            if (value && _selActive is null && PageCount > 0)
            {
                _selAnchor = _selActive = new TextPos(_currentPage, 0);
                RequestText(_currentPage, 0);
            }
            else if (!value && !HasSelection)
            {
                _selAnchor = _selActive = null;
            }
            _surface.InvalidateVisual();
            StatusMessage?.Invoke(this, value
                ? "Caret browsing on: arrow keys move through the text, Shift+arrows select. F7 turns it off."
                : "Caret browsing off");
        }
    }

    private bool HandleCaretKey(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.F7 && mods == ModifierKeys.None)
        {
            CaretBrowsing = !CaretBrowsing;
            return true;
        }
        if ((mods & ~(ModifierKeys.Shift | ModifierKeys.Control)) != 0) return false;
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) return false;
        var shift = (mods & ModifierKeys.Shift) != 0;
        var ctrl = (mods & ModifierKeys.Control) != 0;
        if (!_caretBrowsing && !(shift && _tool == ViewerTool.SelectText)) return false;
        if (_engine is null || PageCount == 0) return false;

        var caret = _selActive ?? new TextPos(_currentPage, 0);
        if (MoveCaret(caret, e.Key, ctrl) is not { } to) return true; // that page's text is still loading
        _selAnchor = shift ? _selAnchor ?? caret : to;
        _selActive = to;
        RevealCaret(to);
        _surface.InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Where a caret key takes the caret, or null while the text it needs is loading.</summary>
    private TextPos? MoveCaret(TextPos c, Key key, bool ctrl)
    {
        var last = PageCount - 1;
        var page = Math.Clamp(c.Page, 0, last);
        if (key == Key.Home && ctrl) return new TextPos(0, 0);
        if (key == Key.End && ctrl) return new TextPos(last, TryGetPageText(last)?.Text.Length ?? int.MaxValue);
        if (TryGetPageText(page) is not { } t) return null;
        var text = t.Text;
        var i = Math.Clamp(c.Caret, 0, text.Length);
        switch (key)
        {
            case Key.Left when i == 0:
                if (page == 0) return new TextPos(0, 0);
                return TryGetPageText(page - 1) is { } prev ? new TextPos(page - 1, prev.Text.Length) : null;
            case Key.Left:
                return new TextPos(page, ctrl ? WordLeft(text, i) : i - 1);
            case Key.Right when i >= text.Length:
                if (page == last) return new TextPos(page, text.Length);
                RequestText(page + 1, 0);
                return new TextPos(page + 1, 0);
            case Key.Right:
                return new TextPos(page, ctrl ? WordRight(text, i) : i + 1);
            case Key.Home:
                return new TextPos(page, t.LineAt(i).Start);
            case Key.End:
                return new TextPos(page, t.LineAt(i).End);
            case Key.Up:
            {
                var x = CaretRect(t, i)?.X ?? 0;
                var (start, _) = t.LineAt(i);
                if (start > 0)
                {
                    var (ps, pe) = t.LineAt(start - 1);
                    return new TextPos(page, NearestInLine(t, ps, pe, x));
                }
                if (page == 0) return new TextPos(0, 0);
                if (TryGetPageText(page - 1) is not { } above) return null;
                var (ls, le) = above.LineAt(above.Text.Length);
                return new TextPos(page - 1, NearestInLine(above, ls, le, x));
            }
            case Key.Down:
            {
                var x = CaretRect(t, i)?.X ?? 0;
                var (_, end) = t.LineAt(i);
                if (end < text.Length)
                {
                    var (ns, ne) = t.LineAt(end + 1);
                    return new TextPos(page, NearestInLine(t, ns, ne, x));
                }
                if (page == last) return new TextPos(page, text.Length);
                if (TryGetPageText(page + 1) is not { } next) return null;
                var (fs, fe) = next.LineAt(0);
                return new TextPos(page + 1, NearestInLine(next, fs, fe, x));
            }
        }
        return c;
    }

    private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    private static int WordLeft(string text, int i)
    {
        while (i > 0 && !IsWordChar(text[i - 1])) i--;
        while (i > 0 && IsWordChar(text[i - 1])) i--;
        return i;
    }

    private static int WordRight(string text, int i)
    {
        while (i < text.Length && IsWordChar(text[i])) i++;
        while (i < text.Length && !IsWordChar(text[i])) i++;
        return i;
    }

    /// <summary>The caret's box in page space: zero width, as tall as the character it touches.</summary>
    private static RectD? CaretRect(PageText t, int caret)
    {
        caret = Math.Clamp(caret, 0, t.Text.Length);
        if (caret < t.Text.Length && t.Text[caret] != '\n' && t.GetRects(caret, caret + 1) is [var r, ..])
            return new RectD(r.X, r.Y, 0, r.Height);
        if (caret > 0 && t.GetRects(caret - 1, caret) is [var l, ..])
            return new RectD(l.Right, l.Y, 0, l.Height);
        return null;
    }

    /// <summary>The caret in [start, end] nearest to x (for moving between lines).</summary>
    private static int NearestInLine(PageText t, int start, int end, double x)
    {
        for (var i = start; i < end; i++)
            if (t.GetRects(i, i + 1) is [var r, ..] && x < r.CenterX) return i;
        return end;
    }

    private void RevealCaret(TextPos c)
    {
        if (c.Page < 0 || c.Page >= PageCount) return;
        if (_texts.TryGetValue(c.Page, out var t) && CaretRect(t, c.Caret) is { } r) EnsureVisible(c.Page, r.Inflate(2));
        else if (c.Page < _rects.Length && (_rects[c.Page].IsEmpty || !_rects[c.Page].IntersectsWith(Viewport))) GoToPage(c.Page, record: false);
        _surface.InvalidateVisual();
    }
}
