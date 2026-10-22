using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfDesk.Core;

namespace PdfDesk.App.Viewer;

/// <summary>A field value the user set (by full field name).</summary>
public sealed record FieldEdit(string FieldName, string Value);

/// <summary>Text or a mark to put on a page (new, or replacing <see cref="ReplaceId"/>); empty text with an id deletes it.</summary>
public sealed record StampEdit(int PageIndex, RectD Rect, StampKind Kind, string Text, double FontSize, string? ReplaceId);

/// <summary>Form filling and Fill &amp; Sign on top of the page view.</summary>
public sealed partial class PageViewer
{
    private const double DefaultStampSize = 11;

    private readonly Canvas _overlay = new();
    private Dictionary<int, List<FormWidget>> _widgetsByPage = [];
    private List<FormWidget> _fillOrder = [];
    private Dictionary<int, List<TextStamp>> _stampsByPage = [];
    private readonly Dictionary<string, string> _pendingValues = [];
    private bool _highlightFields = true;
    private FormWidget? _hoverWidget;
    private FormWidget? _focusWidget;

    // The open inline editor (a text field, a choice field, or Fill & Sign text).
    private FrameworkElement? _editor;
    private FormWidget? _editorWidget;
    private (int Page, Point Pt, string? Id, double Size)? _editorStamp;
    private bool _committing;

    public event EventHandler<FieldEdit>? FieldEdited;
    public event EventHandler<StampEdit>? StampEdited;

    public bool HasFormFields => _fillOrder.Count > 0;

    public bool HighlightFields
    {
        get => _highlightFields;
        set
        {
            _highlightFields = value;
            _surface.InvalidateVisual();
        }
    }

    public void SetFormWidgets(IReadOnlyList<FormWidget> widgets)
    {
        _widgetsByPage = widgets.GroupBy(w => w.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        _fillOrder = [.. widgets.Where(IsFillable)];
        // Keep only optimistic values the document doesn't reflect yet.
        foreach (var name in _pendingValues.Keys.ToList())
            if (widgets.FirstOrDefault(w => w.FieldName == name) is { } w && w.Value == _pendingValues[name])
                _pendingValues.Remove(name);
        if (_focusWidget is not null)
            _focusWidget = widgets.FirstOrDefault(w => w.FieldName == _focusWidget.FieldName && w.Rect == _focusWidget.Rect);
        _surface.InvalidateVisual();
    }

    public void ClearPendingValues()
    {
        _pendingValues.Clear();
        _surface.InvalidateVisual();
    }

    public void SetStamps(IReadOnlyList<TextStamp> stamps)
    {
        _stampsByPage = stamps.GroupBy(s => s.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        _surface.InvalidateVisual();
    }

    private static bool IsFillable(FormWidget w) =>
        !w.ReadOnly && w.Kind is FormFieldKind.Text or FormFieldKind.CheckBox or FormFieldKind.Radio or FormFieldKind.Choice;

    private string ValueOf(FormWidget w) => _pendingValues.TryGetValue(w.FieldName, out var v) ? v : w.Value;

    private FormWidget? WidgetAt(int page, Point pt)
    {
        if (page < 0 || !_widgetsByPage.TryGetValue(page, out var list)) return null;
        // Smallest match wins (a check box inside a larger field's box).
        return list.Where(w => IsFillable(w) && w.Rect.Inflate(1).Contains(pt.X, pt.Y)).MinBy(w => w.Rect.Width * w.Rect.Height);
    }

    private TextStamp? StampAt(int page, Point pt) =>
        page >= 0 && _stampsByPage.TryGetValue(page, out var list) ? list.LastOrDefault(s => s.Rect.Inflate(2).Contains(pt.X, pt.Y)) : null;

    // ───────────────────────────── Painting ─────────────────────────────

    private static readonly Brush FieldBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x2C, 0x33, 0x88, 0xFF)));
    private static readonly Pen FieldHoverPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)), 1.5));
    private static readonly Pen StampPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0x00, 0x67, 0xC0)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush PendingOnBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)));
    private static readonly Brush WhiteBrush = Frozen(Brushes.White);

    private void PaintForms(DrawingContext dc, int page)
    {
        if (_widgetsByPage.TryGetValue(page, out var widgets))
        {
            foreach (var w in widgets)
            {
                if (!IsFillable(w)) continue;
                var r = ToSurfaceRect(page, w.Rect);
                if (_highlightFields && !_invert) dc.DrawRectangle(FieldBrush, null, r);

                // Optimistic check boxes / radios: show the new state until the re-rendered page arrives.
                if (w.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio && _pendingValues.TryGetValue(w.FieldName, out var pending))
                {
                    var on = pending == w.OnState || (pending == "On" && w.Kind == FormFieldKind.CheckBox);
                    var inner = r;
                    inner.Inflate(-Math.Min(r.Width, r.Height) * 0.12, -Math.Min(r.Width, r.Height) * 0.12);
                    dc.DrawRectangle(WhiteBrush, null, inner);
                    if (on) DrawCheck(dc, inner);
                }
                if (ReferenceEquals(w, _hoverWidget) || ReferenceEquals(w, _focusWidget))
                    dc.DrawRectangle(null, FieldHoverPen, r);
            }
        }
        if (_stampsByPage.TryGetValue(page, out var stamps) && _tool is ViewerTool.AddText or ViewerTool.AddCheck or ViewerTool.AddCross)
            foreach (var s in stamps)
                dc.DrawRectangle(null, StampPen, ToSurfaceRect(page, s.Rect.Inflate(1)));
    }

    private static void DrawCheck(DrawingContext dc, Rect r)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(r.X + r.Width * 0.15, r.Y + r.Height * 0.55), false, false);
            ctx.LineTo(new Point(r.X + r.Width * 0.42, r.Y + r.Height * 0.82), true, false);
            ctx.LineTo(new Point(r.X + r.Width * 0.88, r.Y + r.Height * 0.18), true, false);
        }
        dc.DrawGeometry(null, new Pen(PendingOnBrush, Math.Max(1.5, r.Width * 0.14)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
    }

    // ───────────────────────────── Mouse ─────────────────────────────

    /// <summary>Handles a click on a field, a Fill &amp; Sign item, or with a Fill &amp; Sign tool. True if consumed.</summary>
    private bool HandleFormMouseDown(MouseButtonEventArgs e, int page, Point sp)
    {
        if (page < 0) return false;
        if (!HandleFormClick(page, ToPagePoint(page, sp), e.ClickCount)) return false;
        e.Handled = true;
        return true;
    }

    /// <summary>Dev/test entry: the same as clicking at a page point.</summary>
    internal bool DevClick(int page, double x, double y) => HandleFormClick(page, new Point(x, y), 1);

    internal void DevType(string text)
    {
        if (_editor is TextBox tb) tb.Text = text;
        else if (_editor is ComboBox cb) cb.SelectedItem = text;
    }

    internal int FillableCount => _fillOrder.Count;
    internal string DevDescribeField(int i) => i < _fillOrder.Count ? $"{_fillOrder[i].Kind} p{_fillOrder[i].PageIndex + 1} {_fillOrder[i].Rect.X:0},{_fillOrder[i].Rect.Y:0} {_fillOrder[i].Rect.Width:0}x{_fillOrder[i].Rect.Height:0} = {ValueOf(_fillOrder[i])}" : "";

    internal bool HandleFormClick(int page, Point pt, int clickCount)
    {

        if (WidgetAt(page, pt) is { } widget)
        {
            CommitEditor();
            ActivateWidget(widget);
            return true;
        }

        var fillTool = _tool is ViewerTool.AddText or ViewerTool.AddCheck or ViewerTool.AddCross;
        if (StampAt(page, pt) is { } stamp && (fillTool || clickCount == 2))
        {
            CommitEditor();
            if (stamp.Kind == StampKind.Text) OpenStampEditor(page, new Point(stamp.Rect.X, stamp.Rect.Y), stamp.Text, stamp.FontSize, stamp.Id);
            else StampEdited?.Invoke(this, new StampEdit(page, stamp.Rect, stamp.Kind, "", stamp.FontSize, stamp.Id)); // clicking a mark removes it
            return true;
        }

        if (!fillTool) return false;
        CommitEditor();
        if (_tool == ViewerTool.AddText)
        {
            // Put the text's top-left at the pointer, nudged up so the baseline sits where the user clicked.
            OpenStampEditor(page, new Point(pt.X, pt.Y - DefaultStampSize * 0.6), "", DefaultStampSize, null);
        }
        else
        {
            const double size = 12;
            var kind = _tool == ViewerTool.AddCheck ? StampKind.Check : StampKind.Cross;
            StampEdited?.Invoke(this, new StampEdit(page, new RectD(pt.X - size / 2, pt.Y - size / 2, size, size), kind, "", size, null));
        }
        return true;
    }

    private Cursor? FormCursor(int page, Point sp)
    {
        if (page < 0) return null;
        var pt = ToPagePoint(page, sp);
        var w = WidgetAt(page, pt);
        if (!ReferenceEquals(w, _hoverWidget))
        {
            _hoverWidget = w;
            _surface.InvalidateVisual();
        }
        if (w is not null) return w.Kind == FormFieldKind.Text ? Cursors.IBeam : Cursors.Hand;
        if (_tool is ViewerTool.AddText or ViewerTool.AddCheck or ViewerTool.AddCross)
            return StampAt(page, pt) is not null ? Cursors.Hand : _tool == ViewerTool.AddText ? Cursors.IBeam : Cursors.Cross;
        return null;
    }

    // ───────────────────────────── Fields ─────────────────────────────

    private void ActivateWidget(FormWidget w)
    {
        _focusWidget = w;
        Focus();
        switch (w.Kind)
        {
            case FormFieldKind.CheckBox:
                SetValue(w, ValueOf(w) is "Off" or "" ? w.OnState ?? "On" : "Off");
                break;
            case FormFieldKind.Radio:
                if (ValueOf(w) != w.OnState && w.OnState is not null) SetValue(w, w.OnState);
                break;
            case FormFieldKind.Text:
            case FormFieldKind.Choice:
                OpenFieldEditor(w);
                break;
        }
        _surface.InvalidateVisual();
    }

    private void SetValue(FormWidget w, string value)
    {
        if (ValueOf(w) == value) return;
        _pendingValues[w.FieldName] = value;
        _surface.InvalidateVisual();
        FieldEdited?.Invoke(this, new FieldEdit(w.FieldName, value));
    }

    private void OpenFieldEditor(FormWidget w)
    {
        EnsureVisible(w.PageIndex, w.Rect);
        var k = _zoom * PtToDip;
        var fontPt = w.Multiline ? Math.Min(11, w.Rect.Height * 0.7) : Math.Clamp(w.Rect.Height * 0.62, 6, 14);

        if (w.Kind == FormFieldKind.Choice)
        {
            var combo = new ComboBox
            {
                ItemsSource = w.Options,
                IsEditable = w.Editable,
                Text = ValueOf(w),
                SelectedItem = w.Options.Contains(ValueOf(w)) ? ValueOf(w) : null,
                FontSize = Math.Max(11, fontPt * k),
                MinHeight = 0,
                Padding = new Thickness(4, 0, 4, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(combo, w.FieldName);
            combo.SelectionChanged += (_, _) => { if (!w.Editable && combo.SelectedItem is not null && !_committing) CommitEditor(); };
            ShowEditor(combo, w, null);
            combo.Dispatcher.BeginInvoke(() => combo.IsDropDownOpen = true);
            return;
        }

        var box = new TextBox
        {
            Text = ValueOf(w),
            FontFamily = new FontFamily("Arial"),
            FontSize = Math.Max(6, fontPt * k),
            AcceptsReturn = w.Multiline,
            TextWrapping = w.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = w.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            MaxLength = w.MaxLength,
            Padding = new Thickness(2, 0, 2, 0),
            MinHeight = 0,
            BorderThickness = new Thickness(1.5),
            Background = Brushes.White,
            Foreground = Brushes.Black,
            CaretBrush = Brushes.Black,
        };
        box.SetResourceReference(BorderBrushProperty, "AccentFillColorDefaultBrush");
        System.Windows.Automation.AutomationProperties.SetName(box, w.FieldName);
        ShowEditor(box, w, null);
        box.SelectAll();
    }

    // ───────────────────────────── Fill & Sign text ─────────────────────────────

    private void OpenStampEditor(int page, Point topLeftPt, string text, double sizePt, string? id)
    {
        var k = _zoom * PtToDip;
        var box = new TextBox
        {
            Text = text,
            FontFamily = new FontFamily("Arial"),
            FontSize = sizePt * k,
            AcceptsReturn = true,
            MinWidth = 60 * k,
            Padding = new Thickness(sizePt * 0.15 * k - 1, 0, 2, 0),
            MinHeight = 0,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            Foreground = Brushes.Black,
            CaretBrush = Brushes.Black,
            ToolTip = "Type, then click elsewhere. Enter adds a line; Esc cancels; empty text removes it.",
        };
        box.SetResourceReference(BorderBrushProperty, "AccentFillColorDefaultBrush");
        System.Windows.Automation.AutomationProperties.SetName(box, "Text on the page");
        ShowEditor(box, null, (page, topLeftPt, id, sizePt));
        box.CaretIndex = box.Text.Length;
    }

    // ───────────────────────────── Editor plumbing ─────────────────────────────

    private void ShowEditor(FrameworkElement editor, FormWidget? widget, (int, Point, string?, double)? stamp)
    {
        CloseEditor();
        _editor = editor;
        _editorWidget = widget;
        _editorStamp = stamp;
        editor.PreviewKeyDown += OnEditorKey;
        editor.LostKeyboardFocus += OnEditorLostFocus;
        _overlay.Children.Add(editor);
        PositionEditor();
        editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => editor.Focus());
    }

    /// <summary>Keeps the editor glued to its field or text when zooming or scrolling.</summary>
    private void PositionEditor()
    {
        if (_editor is null || _rects.Length == 0) return;
        var k = _zoom * PtToDip;
        if (_editorWidget is { } w && w.PageIndex < _rects.Length && !_rects[w.PageIndex].IsEmpty)
        {
            var r = ToSurfaceRect(w.PageIndex, w.Rect);
            Canvas.SetLeft(_editor, r.X);
            Canvas.SetTop(_editor, r.Y);
            _editor.Width = Math.Max(r.Width, 40);
            _editor.Height = Math.Max(r.Height, 16);
        }
        else if (_editorStamp is { } s && s.Page < _rects.Length && !_rects[s.Page].IsEmpty)
        {
            var r = _rects[s.Page];
            Canvas.SetLeft(_editor, r.X + s.Pt.X * k - 2);
            Canvas.SetTop(_editor, r.Y + s.Pt.Y * k - 1);
            if (_editor is TextBox tb) tb.FontSize = s.Size * k;
        }
        else
        {
            _editor.Visibility = Visibility.Collapsed;
        }
    }

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Escape:
                CloseEditor();
                Focus();
                e.Handled = true;
                break;
            case Key.Enter when _editorWidget is { Multiline: false } || (_editorStamp is not null && !shift && Keyboard.Modifiers == ModifierKeys.Control):
                CommitEditor();
                Focus();
                e.Handled = true;
                break;
            case Key.Tab when _editorWidget is { } w:
                CommitEditor();
                FocusNextField(w, shift);
                e.Handled = true;
                break;
        }
    }

    private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Moving into the combo box's own dropdown is not leaving the editor.
        if (sender is ComboBox c && (c.IsDropDownOpen || c.IsKeyboardFocusWithin)) return;
        if (e.NewFocus is DependencyObject d && sender is DependencyObject s && IsDescendant(s, d)) return;
        CommitEditor();
    }

    private static bool IsDescendant(DependencyObject parent, DependencyObject child)
    {
        for (var x = child; x is not null; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x == parent) return true;
        return false;
    }

    /// <summary>Saves what the open editor holds (if it changed) and closes it.</summary>
    public void CommitEditor()
    {
        if (_editor is null || _committing) return;
        _committing = true;
        try
        {
            if (_editorWidget is { } w)
            {
                var value = _editor switch
                {
                    TextBox tb => tb.Text,
                    ComboBox cb => cb.IsEditable ? cb.Text : cb.SelectedItem as string ?? ValueOf(w),
                    _ => ValueOf(w),
                };
                SetValue(w, value);
            }
            else if (_editorStamp is { } s && _editor is TextBox tb)
            {
                var text = tb.Text.TrimEnd();
                if (text.Length > 0 || s.Id is not null)
                    StampEdited?.Invoke(this, new StampEdit(s.Page, MeasureStamp(s.Pt, text, s.Size), StampKind.Text, text, s.Size, s.Id));
            }
        }
        finally
        {
            CloseEditor();
            _committing = false;
        }
    }

    private void CloseEditor()
    {
        if (_editor is null) return;
        _editor.PreviewKeyDown -= OnEditorKey;
        _editor.LostKeyboardFocus -= OnEditorLostFocus;
        _overlay.Children.Remove(_editor);
        _editor = null;
        _editorWidget = null;
        _editorStamp = null;
    }

    /// <summary>The page-space box for typed text (Arial metrics match the PDF's Helvetica closely).</summary>
    private RectD MeasureStamp(Point topLeft, string text, double size)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var width = 0.0;
        var typeface = new Typeface("Arial");
        foreach (var line in lines)
        {
            var ft = new FormattedText(line.Length == 0 ? " " : line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1.0);
            width = Math.Max(width, ft.WidthIncludingTrailingWhitespace);
        }
        return new RectD(topLeft.X, topLeft.Y, width + size * 0.4, lines.Length * size * 1.2 + size * 0.25);
    }

    // ───────────────────────────── Keyboard ─────────────────────────────

    /// <summary>Tab / Shift+Tab: move through the fields in reading order (text and choice fields open for typing).</summary>
    private bool FocusNextField(FormWidget? from, bool backwards)
    {
        if (_fillOrder.Count == 0) return false;
        var index = from is null ? -1 : _fillOrder.FindIndex(w => w.FieldName == from.FieldName && w.Rect == from.Rect);
        if (index < 0)
        {
            // Start at the first field on (or after) the current page.
            index = backwards ? _fillOrder.FindLastIndex(w => w.PageIndex <= _currentPage) : _fillOrder.FindIndex(w => w.PageIndex >= _currentPage);
            if (index < 0) index = backwards ? _fillOrder.Count - 1 : 0;
        }
        else
        {
            index = (index + (backwards ? -1 : 1) + _fillOrder.Count) % _fillOrder.Count;
        }

        var next = _fillOrder[index];
        _focusWidget = next;
        EnsureVisible(next.PageIndex, next.Rect);
        if (next.Kind is FormFieldKind.Text or FormFieldKind.Choice) OpenFieldEditor(next);
        else Focus();
        _surface.InvalidateVisual();
        return true;
    }

    /// <summary>Viewer keys for forms: Tab moves between fields, Space toggles the focused check box or radio.</summary>
    private bool HandleFormKey(KeyEventArgs e)
    {
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ~ModifierKeys.Shift) == 0 && _fillOrder.Count > 0)
            return FocusNextField(_focusWidget, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        if (e.Key is Key.Space or Key.Enter && Keyboard.Modifiers == ModifierKeys.None && _focusWidget is { } w && IsFillable(w))
        {
            ActivateWidget(w);
            return true;
        }
        if (e.Key == Key.Escape && _focusWidget is not null)
        {
            _focusWidget = null;
            _surface.InvalidateVisual();
            return true;
        }
        return false;
    }

    private void EnsureVisible(int page, RectD rect)
    {
        if (page >= _rects.Length) return;
        if (_rects[page].IsEmpty) GoToPage(page, record: false);
        var r = ToSurfaceRect(page, rect);
        var vp = Viewport;
        if (r.Top < vp.Top + 8 || r.Bottom > vp.Bottom - 60) ScrollToVerticalOffset(r.Y - vp.Height / 3);
        if (r.Left < vp.Left || r.Right > vp.Right) ScrollToHorizontalOffset(r.X - 40);
        UpdateLayout();
    }
}
