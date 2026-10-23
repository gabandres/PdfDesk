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

/// <summary>A user event on a form widget whose actions should run (see <see cref="FormEditor.RunAction"/>).</summary>
public sealed record FormWidgetAction(FormWidget Widget, FormTrigger Trigger);

/// <summary>
/// Live keystroke checks for the inline text editor: <see cref="Prepare"/> when it opens on a field with a keystroke
/// script (load off the UI thread), <see cref="Filter"/> per edit (null = not ready or no verdict: let it through),
/// <see cref="Release"/> when it closes.
/// </summary>
public interface IFieldKeystrokes
{
    void Prepare(FormWidget widget);
    KeystrokeResult? Filter(FormWidget widget, string value, string change, int selStart, int selEnd);
    void Release();
}

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
    private double _editorFontPt; // the open editor's text size in points (scaled with the zoom)
    private bool _committing;
    private IReadOnlyList<int>? _pageRotations;

    // Push buttons and widget actions.
    private FormWidget? _pressedWidget;
    private bool _formMouseHooked;
    private ToolTip? _widgetTip;
    private FormWidget? _tipWidget;

    public event EventHandler<FieldEdit>? FieldEdited;
    public event EventHandler<StampEdit>? StampEdited;

    /// <summary>A widget's actions should run: a push button pressed or released, a field clicked, entered or left,
    /// or its editor opened or closed. Raised only when the widget has actions for that trigger.</summary>
    public event EventHandler<FormWidgetAction>? WidgetAction;

    /// <summary>The user clicked a signature field, or pressed Enter/Space on it.</summary>
    public event EventHandler<FormWidget>? SignatureFieldActivated;

    /// <summary>Live keystroke scripts for the inline text editor (null = none).</summary>
    public IFieldKeystrokes? Keystrokes { get; set; }

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
        if (_hoverWidget is not null)
            _hoverWidget = widgets.FirstOrDefault(w => w.FieldName == _hoverWidget.FieldName && w.Rect == _hoverWidget.Rect);
        if (_pressedWidget is not null)
            _pressedWidget = widgets.FirstOrDefault(w => w.FieldName == _pressedWidget.FieldName && w.Rect == _pressedWidget.Rect);
        HookFormMouse();
        _surface.InvalidateVisual();
    }

    /// <summary>Moves the focus to a field (a script's field.setFocus): its editor opens for text and lists.</summary>
    public void FocusField(string name)
    {
        if (_fillOrder.FirstOrDefault(w => w.FieldName == name) is not { } w) return;
        CommitEditor();
        FocusStop(new TabStop(w.PageIndex, w.Rect, w, null));
    }

    /// <summary>
    /// Each page's /Rotate (0, 90, 180 or 270; null = all upright), so the inline editor of a field on a turned page
    /// runs along the field like its printed text.
    /// </summary>
    public void SetPageRotations(IReadOnlyList<int>? rotations)
    {
        _pageRotations = rotations;
        PositionEditor();
    }

    private int RotationOf(int page) => _pageRotations is { } r && page >= 0 && page < r.Count ? ((r[page] % 360) + 360) % 360 : 0;

    public void ClearPendingValues()
    {
        _pendingValues.Clear();
        _surface.InvalidateVisual();
    }

    /// <summary>
    /// The form's scripts refused these fields' values: show the document's values again (unless the field is open
    /// in the editor or was changed since, which would be a newer value).
    /// </summary>
    public void RevertPendingValues(IReadOnlyDictionary<string, string> rejected)
    {
        foreach (var (name, value) in rejected)
            if (_pendingValues.TryGetValue(name, out var pending) && pending == value) _pendingValues.Remove(name);
        _surface.InvalidateVisual();
    }

    public void SetStamps(IReadOnlyList<TextStamp> stamps)
    {
        _stampsByPage = stamps.GroupBy(s => s.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        _surface.InvalidateVisual();
    }

    /// <summary>A widget the user can act on (and Tab to): fields to fill, push buttons, and signature fields.</summary>
    private static bool IsFillable(FormWidget w) =>
        w.Kind == FormFieldKind.Signature
        || (!w.ReadOnly && w.Kind is FormFieldKind.Text or FormFieldKind.CheckBox or FormFieldKind.Radio or FormFieldKind.Choice or FormFieldKind.PushButton);

    private void RaiseAction(FormWidget w, FormTrigger trigger)
    {
        var needed = trigger switch
        {
            FormTrigger.Down => FormTriggers.Down,
            FormTrigger.Up => FormTriggers.Up,
            FormTrigger.Click => FormTriggers.Down | FormTriggers.Up,
            FormTrigger.Enter => FormTriggers.Enter,
            FormTrigger.Exit => FormTriggers.Exit,
            FormTrigger.Focus => FormTriggers.Focus,
            _ => FormTriggers.Blur,
        };
        if ((w.Triggers & needed) != 0) WidgetAction?.Invoke(this, new FormWidgetAction(w, trigger));
    }

    /// <summary>Mouse release (push buttons fire on release inside) and leaving the page view (hover ends).</summary>
    private void HookFormMouse()
    {
        if (_formMouseHooked) return;
        _formMouseHooked = true;
        _surface.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnFormMouseUp), handledEventsToo: true);
        _surface.MouseLeave += (_, _) => SetHoverWidget(null);
    }

    private void OnFormMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedWidget is not { } w) return;
        _pressedWidget = null;
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        var sp = e.GetPosition(_surface);
        var page = HitPage(sp);
        if (page == w.PageIndex && ToPagePoint(page, sp) is var pt && w.Rect.Inflate(1).Contains(pt.X, pt.Y)) RaiseAction(w, FormTrigger.Up);
        _surface.InvalidateVisual();
    }

    private void SetHoverWidget(FormWidget? w)
    {
        if (ReferenceEquals(w, _hoverWidget)) return;
        var old = _hoverWidget;
        _hoverWidget = w;
        if (old is not null) RaiseAction(old, FormTrigger.Exit);
        if (w is not null) RaiseAction(w, FormTrigger.Enter);
        ShowWidgetTip(w);
        _surface.InvalidateVisual();
    }

    /// <summary>"Click to sign" over an unsigned signature field.</summary>
    private void ShowWidgetTip(FormWidget? w)
    {
        var tip = w is { Kind: FormFieldKind.Signature, Signed: false } ? "Click to sign" : null;
        if (tip is null)
        {
            _tipWidget = null;
            if (_widgetTip is { IsOpen: true }) _widgetTip.IsOpen = false;
            return;
        }
        if (_widgetTip is { IsOpen: true } && ReferenceEquals(_tipWidget, w)) return;
        _widgetTip ??= new ToolTip { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
        _widgetTip.IsOpen = false;
        _tipWidget = w;
        _widgetTip.Content = tip;
        _widgetTip.PlacementTarget = _surface;
        _widgetTip.IsOpen = true;
    }

    internal string? DevWidgetTip => _widgetTip is { IsOpen: true } t ? t.Content as string : null;

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

    private static readonly Brush PendingOnBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)));
    private static readonly Brush WhiteBrush = Frozen(Brushes.White);
    private static readonly Brush PressedBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x48, 0x00, 0x00, 0x00)));

    private void PaintForms(DrawingContext dc, int page)
    {
        var pal = _palette;
        var focused = IsKeyboardFocusWithin;
        if (_widgetsByPage.TryGetValue(page, out var widgets))
        {
            foreach (var w in widgets)
            {
                if (!IsFillable(w)) continue;
                var r = ToSurfaceRect(page, w.Rect);
                if (_highlightFields && !EffectiveInvert && !w.Signed) dc.DrawRectangle(pal.Field, pal.FieldPen, r);
                if (ReferenceEquals(w, _pressedWidget)) dc.DrawRectangle(PressedBrush, null, r);

                // Optimistic check boxes / radios: show the new state until the re-rendered page arrives.
                if (w.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio && _pendingValues.TryGetValue(w.FieldName, out var pending))
                {
                    var inner = r;
                    inner.Inflate(-Math.Min(r.Width, r.Height) * 0.12, -Math.Min(r.Width, r.Height) * 0.12);
                    dc.DrawRectangle(WhiteBrush, null, inner);
                    if (IsOnValue(w, pending)) DrawCheck(dc, inner);
                }
                if (focused && ReferenceEquals(w, _focusWidget) && _editorWidget is null)
                {
                    var fr = r;
                    fr.Inflate(2, 2);
                    dc.DrawRectangle(null, pal.FocusPen, fr);
                }
                else if (ReferenceEquals(w, _hoverWidget) || ReferenceEquals(w, _focusWidget))
                {
                    dc.DrawRectangle(null, pal.HoverPen, r);
                }
            }
        }
        if (_stampsByPage.TryGetValue(page, out var stamps) && _tool is ViewerTool.AddText or ViewerTool.AddCheck or ViewerTool.AddCross)
            foreach (var s in stamps)
                dc.DrawRectangle(null, pal.StampPen, ToSurfaceRect(page, s.Rect.Inflate(1)));
    }

    /// <summary>
    /// Whether a check box / radio value shows this widget checked, the way FormEditor.SetValues stores it: a widget is
    /// on when the value is its own "on" state ("On" means the field's first widget that has one); a widget without an
    /// appearance (no on state) can't be shown checked.
    /// </summary>
    private bool IsOnValue(FormWidget w, string value) =>
        value is not ("Off" or "") && w.OnState is { } on
        && (value == on || (value == "On" && _fillOrder.Find(x => x.FieldName == w.FieldName && x.OnState is not null)?.OnState == on));

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
        if (WidgetAt(page, ToPagePoint(page, sp)) is { Kind: FormFieldKind.PushButton } button)
        {
            // Pressed until released: the down action runs now, the up action (and /A) on release inside the button.
            CommitEditor();
            Focus();
            _focusWidget = button;
            _focusLink = null;
            _pressLink = null;
            _pressedWidget = button;
            HookFormMouse();
            _surface.CaptureMouse();
            RaiseAction(button, FormTrigger.Down);
            _surface.InvalidateVisual();
            e.Handled = true;
            return true;
        }
        if (!HandleFormClick(page, ToPagePoint(page, sp), e.ClickCount)) return false;
        e.Handled = true;
        return true;
    }

    /// <summary>Dev/test entry: the same as clicking at a page point.</summary>
    internal bool DevClick(int page, double x, double y) => HandleFormClick(page, new Point(x, y), 1);

    internal void DevType(string text)
    {
        if (_editor is TextBox tb) tb.Text = text;
        else if (_editor is PasswordBox pb) pb.Password = text;
        else if (_editor is ComboBox cb) cb.SelectedItem = text;
        else if (_editor is ListBox lb)
            foreach (var item in lb.Items.OfType<ListBoxItem>()) item.IsSelected = text.Split('|').Contains(item.Content as string);
    }

    /// <summary>An inline editor (form field or Fill &amp; Sign text) is open; its value isn't in the document yet.</summary>
    public bool IsEditing => _editor is not null;

    internal string? DevEditorText => _editor switch
    {
        TextBox tb => tb.Text,
        PasswordBox pb => new string('*', pb.Password.Length),
        ComboBox cb => cb.Text,
        ListBox lb => string.Join('|', lb.Items.OfType<ListBoxItem>().Where(i => i.IsSelected).Select(i => i.Content)),
        _ => null,
    };

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
        if (page < 0)
        {
            SetHoverWidget(null);
            return null;
        }
        var pt = ToPagePoint(page, sp);
        var w = WidgetAt(page, pt);
        SetHoverWidget(w);
        if (w is not null) return w.Kind == FormFieldKind.Text ? Cursors.IBeam : Cursors.Hand;
        if (_tool is ViewerTool.AddText or ViewerTool.AddCheck or ViewerTool.AddCross)
            return StampAt(page, pt) is not null ? Cursors.Hand : _tool == ViewerTool.AddText ? Cursors.IBeam : Cursors.Cross;
        return null;
    }

    // ───────────────────────────── Fields ─────────────────────────────

    private void ActivateWidget(FormWidget w)
    {
        Focus(); // first: focus arriving from outside resets the focused item
        _focusWidget = w;
        _focusLink = null;
        switch (w.Kind)
        {
            case FormFieldKind.CheckBox when w.OnState is null:
                // No checked appearance: the document could only store "Off" for it, so don't pretend to check it.
                StatusMessage?.Invoke(this, "This check box can't be changed: the form doesn't define its checked look.");
                break;
            case FormFieldKind.CheckBox:
                SetValue(w, ValueOf(w) == w.OnState ? "Off" : w.OnState);
                break;
            case FormFieldKind.Radio:
                if (ValueOf(w) != w.OnState && w.OnState is not null) SetValue(w, w.OnState);
                break;
            case FormFieldKind.Text:
            case FormFieldKind.Choice:
                OpenFieldEditor(w);
                break;
            case FormFieldKind.PushButton:
                // Keyboard, touch or a scripted click: press and release at once.
                RaiseAction(w, FormTrigger.Click);
                _surface.InvalidateVisual();
                return;
            case FormFieldKind.Signature:
                SignatureFieldActivated?.Invoke(this, w);
                _surface.InvalidateVisual();
                return;
        }
        // Fields can carry mouse actions too (a check box showing or hiding other fields): they run after the change.
        RaiseAction(w, FormTrigger.Click);
        _surface.InvalidateVisual();
    }

    private void SetValue(FormWidget w, string value)
    {
        if (ValueOf(w) == value) return;
        _pendingValues[w.FieldName] = value;
        _surface.InvalidateVisual();
        FieldEdited?.Invoke(this, new FieldEdit(w.FieldName, value));
    }

    /// <summary>Option texts to show: the display labels when the field has them, else the export values.</summary>
    private static IReadOnlyList<string> LabelsOf(FormWidget w) => w.OptionLabels is { } l && l.Count == w.Options.Count ? l : w.Options;

    private static string ExportOf(FormWidget w, string label)
    {
        if (IndexOf(w.Options, label) >= 0) return label;
        var i = IndexOf(LabelsOf(w), label);
        return i >= 0 ? w.Options[i] : label;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }

    private void OpenFieldEditor(FormWidget w)
    {
        EnsureVisible(w.PageIndex, w.Rect);
        // Size the text from the field's own (unrotated) height: on a turned page that's the displayed width.
        var fieldHeight = RotationOf(w.PageIndex) is 90 or 270 ? w.Rect.Width : w.Rect.Height;

        if (w.Kind == FormFieldKind.Choice && w.MultiSelect)
        {
            _editorFontPt = 10;
            var chosen = ValueOf(w).Split('\n').ToHashSet();
            var labels = LabelsOf(w);
            var list = new ListBox
            {
                SelectionMode = SelectionMode.Multiple,
                MinHeight = 0,
                BorderThickness = new Thickness(1.5),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                ToolTip = "Space or click picks options; Enter or Tab saves; Esc cancels.",
            };
            for (var i = 0; i < w.Options.Count; i++)
                list.Items.Add(new ListBoxItem { Content = labels[i], Tag = w.Options[i], IsSelected = chosen.Contains(w.Options[i]), Padding = new Thickness(4, 0, 4, 0) });
            list.SetResourceReference(BorderBrushProperty, "AccentFillColorDefaultBrush");
            System.Windows.Automation.AutomationProperties.SetName(list, w.FieldName);
            ShowEditor(list, w, null);
            return;
        }

        if (w.Kind == FormFieldKind.Choice)
        {
            _editorFontPt = Math.Clamp(fieldHeight * 0.62, 6, 14);
            var value = ValueOf(w);
            var combo = new ComboBox
            {
                ItemsSource = LabelsOf(w),
                IsEditable = w.Editable,
                MinHeight = 0,
                Padding = new Thickness(4, 0, 4, 0),
            };
            var index = IndexOf(w.Options, value);
            if (index >= 0) combo.SelectedIndex = index;
            else if (w.Editable) combo.Text = value;
            System.Windows.Automation.AutomationProperties.SetName(combo, w.FieldName);
            combo.SelectionChanged += (_, _) => { if (!w.Editable && combo.SelectedIndex >= 0 && !_committing) CommitEditor(); };
            ShowEditor(combo, w, null);
            combo.Dispatcher.BeginInvoke(() => combo.IsDropDownOpen = true);
            return;
        }

        if (w.Password)
        {
            // Masked like the form asks; the value is still stored as plain text, as in any PDF form.
            _editorFontPt = Math.Clamp(fieldHeight * 0.62, 6, 14);
            var secret = new PasswordBox
            {
                Password = ValueOf(w),
                MaxLength = w.MaxLength,
                Padding = new Thickness(2, 0, 2, 0),
                MinHeight = 0,
                BorderThickness = new Thickness(1.5),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.White,
                Foreground = Brushes.Black,
                CaretBrush = Brushes.Black,
            };
            secret.SetResourceReference(BorderBrushProperty, "AccentFillColorDefaultBrush");
            System.Windows.Automation.AutomationProperties.SetName(secret, w.FieldName);
            ShowEditor(secret, w, null);
            secret.SelectAll();
            return;
        }

        _editorFontPt = w.Multiline ? Math.Min(11, fieldHeight * 0.7) : Math.Clamp(fieldHeight * 0.62, 6, 14);
        var box = new TextBox
        {
            Text = ValueOf(w),
            FontFamily = new FontFamily("Arial"),
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
        _editorFontPt = sizePt;
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
        if (widget is not null && editor is TextBox box && (widget.Triggers & FormTriggers.Keystroke) != 0 && Keystrokes is { } keys)
        {
            keys.Prepare(widget);
            _editorKeystrokes = true;
            _checkedText = box.Text;
            _approvedText = null;
            box.PreviewTextInput += OnEditorTextInput;
            box.PreviewKeyDown += OnEditorEditKey;
            box.TextChanged += OnEditorTextChanged;
            DataObject.AddPastingHandler(box, OnEditorPaste);
        }
        _overlay.Children.Add(editor);
        PositionEditor();
        if (widget is not null) RaiseAction(widget, FormTrigger.Focus);
        editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (!ReferenceEquals(_editor, editor)) return;
            // A list box takes focus on an option, so arrows and Space work at once.
            if (editor is ListBox lb && (lb.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.IsSelected) ?? lb.Items.OfType<ListBoxItem>().FirstOrDefault()) is { } item)
                item.Focus();
            else
                editor.Focus();
        });
        _surface.InvalidateVisual();
    }

    /// <summary>Keeps the editor glued to its field or text when zooming or scrolling.</summary>
    private void PositionEditor()
    {
        if (_editor is null || _rects.Length == 0) return;
        var k = _zoom * PtToDip;
        _editor.Visibility = Visibility.Visible;
        if (_editorWidget is { } w && w.PageIndex < _rects.Length && !_rects[w.PageIndex].IsEmpty)
        {
            var r = ToSurfaceRect(w.PageIndex, w.Rect);
            var rotation = RotationOf(w.PageIndex);
            // On a turned page the field's text runs along its long side: lay the editor out unrotated, then turn it.
            var (length, thickness) = rotation is 90 or 270 ? (r.Height, r.Width) : (r.Width, r.Height);
            Canvas.SetLeft(_editor, r.X);
            Canvas.SetTop(_editor, r.Y);
            _editor.Width = Math.Max(length, 40);
            _editor.Height = Math.Max(thickness, 16);
            _editor.LayoutTransform = rotation == 0 ? Transform.Identity : Frozen(new RotateTransform(rotation));
            var size = _editorFontPt * k;
            switch (_editor)
            {
                case TextBox tb:
                    tb.FontSize = Math.Max(6, size);
                    break;
                case ComboBox cb:
                    cb.FontSize = Math.Max(11, size);
                    break;
                case ListBox lb:
                    lb.FontSize = Math.Max(9, size);
                    break;
                case PasswordBox pb:
                    pb.FontSize = Math.Max(6, size);
                    break;
            }
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
            case Key.Enter when _editor is ListBox:
                CommitEditor();
                Focus();
                e.Handled = true;
                break;
            case Key.Tab when _editorWidget is { } w && (Keyboard.Modifiers & ~ModifierKeys.Shift) == 0:
                CommitEditor();
                if (!MoveFocusStop(shift, w)) LeaveViewer(shift);
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

    /// <summary>True when a key event comes from the open editor (or its dropdown) rather than the viewer itself.</summary>
    private bool IsEditorKey(KeyEventArgs e) =>
        _editor is { } editor && (editor.IsKeyboardFocusWithin || (e.OriginalSource is DependencyObject d && IsDescendant(editor, d)));

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
                    PasswordBox pb => pb.Password,
                    ComboBox { IsEditable: true } cb => ExportOf(w, cb.Text),
                    ComboBox cb => cb.SelectedIndex >= 0 && cb.SelectedIndex < w.Options.Count ? w.Options[cb.SelectedIndex] : ValueOf(w),
                    ListBox lb => string.Join('\n', lb.Items.OfType<ListBoxItem>().Where(i => i.IsSelected).Select(i => (string)i.Tag)),
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
        var closed = _editorWidget;
        _editor.PreviewKeyDown -= OnEditorKey;
        _editor.LostKeyboardFocus -= OnEditorLostFocus;
        if (_editor is TextBox box && _editorKeystrokes)
        {
            box.PreviewTextInput -= OnEditorTextInput;
            box.PreviewKeyDown -= OnEditorEditKey;
            box.TextChanged -= OnEditorTextChanged;
            DataObject.RemovePastingHandler(box, OnEditorPaste);
            Keystrokes?.Release();
        }
        _editorKeystrokes = false;
        _checkedText = _approvedText = null;
        _overlay.Children.Remove(_editor);
        _editor = null;
        _editorWidget = null;
        _editorStamp = null;
        _surface.InvalidateVisual();
        if (closed is not null) RaiseAction(closed, FormTrigger.Blur);
    }

    // ───────────────────────────── Live keystrokes ─────────────────────────────

    private bool _editorKeystrokes;
    private string? _checkedText;  // the editor's text as the keystroke checks last saw it
    private string? _approvedText; // the text an edit the checks just let through will produce
    private bool _applyingEdit;    // the editor's text is being changed here (a rewritten or reverted edit)

    private void OnEditorTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is TextBox box && e.Text.Length > 0 && !FilterEdit(box, e.Text, deleteBack: null)) e.Handled = true;
    }

    /// <summary>
    /// The catch-all for edits the key handlers don't see (cut, Ctrl+Backspace/Delete, drag and drop, Enter in a
    /// multiline field, IME): the change is worked out from the text before and after and checked like a keystroke;
    /// a refused one is taken back (as an edit, so undo keeps working). Undo and redo themselves aren't checked.
    /// </summary>
    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var before = _checkedText ?? box.Text;
        var after = box.Text;
        var approved = _approvedText;
        _checkedText = after;
        _approvedText = null;
        if (_applyingEdit || after == before || after == approved || e.UndoAction is UndoAction.Undo or UndoAction.Redo) return;
        var (start, end, change) = FieldKeystrokes.Diff(before, after);
        if (Check(before, change, start, end) is not { } r || (r.Accepted && r.Change == change && r.SelStart == start && r.SelEnd == end)) return;
        // Changing the text from inside TextChanged isn't safe: take it back right after.
        box.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, () =>
        {
            if (!ReferenceEquals(_editor, box) || box.Text != after) return;
            Replace(box, start, start + change.Length, before[start..end]);
            if (r.Accepted) Replace(box, r.SelStart, r.SelEnd, r.Change);
            else box.CaretIndex = end;
        });
    }

    private void OnEditorEditKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || (Keyboard.Modifiers & ~ModifierKeys.Shift) != 0) return;
        // Space never reaches PreviewTextInput in a TextBox; Backspace and Delete are edits too.
        var ok = e.Key switch
        {
            Key.Space => FilterEdit(box, " ", deleteBack: null),
            Key.Back => FilterEdit(box, "", deleteBack: true),
            Key.Delete when Keyboard.Modifiers == ModifierKeys.None => FilterEdit(box, "", deleteBack: false),
            _ => true,
        };
        if (!ok) e.Handled = true;
    }

    private void OnEditorPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box || e.DataObject.GetData(DataFormats.UnicodeText) is not string text) return;
        if (!box.AcceptsReturn) text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        e.CancelCommand();
        // A paste longer than the field allows keeps what fits (as a plain paste would).
        var (start, end) = (box.SelectionStart, box.SelectionStart + box.SelectionLength);
        text = FieldKeystrokes.Fit(box.Text, start, end, text, box.MaxLength);
        if (text.Length == 0 && start == end) return;
        if (FilterEdit(box, text, deleteBack: null)) Replace(box, start, end, text);
    }

    /// <summary>The keystroke script's verdict on replacing [start, end) of value with change (alerts shown); null = no verdict.</summary>
    private KeystrokeResult? Check(string value, string change, int start, int end)
    {
        if (_editorWidget is not { } w || Keystrokes is not { } keys) return null;
        KeystrokeResult? r;
        try
        {
            r = keys.Filter(w, value, change, start, end);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        if (r is not null)
            foreach (var alert in r.Alerts.Take(2)) StatusMessage?.Invoke(this, alert);
        return r;
    }

    /// <summary>
    /// Runs the field's keystroke script on an edit about to happen. True = let it happen as is; false = it was
    /// refused, or the script rewrote it and the rewritten edit was applied here.
    /// </summary>
    private bool FilterEdit(TextBox box, string change, bool? deleteBack)
    {
        if (_editorWidget is null || Keystrokes is null) return true;
        var value = box.Text;
        var start = box.SelectionStart;
        var end = start + box.SelectionLength;
        if (deleteBack is { } back && start == end)
        {
            if (back)
            {
                if (start == 0) return true;
                start--;
            }
            else
            {
                if (end >= value.Length) return true;
                end++;
            }
        }
        var approved = value[..start] + change + value[end..];
        if (Check(value, change, start, end) is not { } r)
        {
            _approvedText = approved;
            return true;
        }
        if (!r.Accepted) return false;
        if (r.Change == change && r.SelStart == start && r.SelEnd == end)
        {
            _approvedText = approved;
            return true;
        }
        Replace(box, r.SelStart, r.SelEnd, r.Change);
        return false;
    }

    /// <summary>
    /// Replaces [start, end) of the editor's text, through the selection so undo keeps working (setting Text would
    /// clear it), changing only the characters that differ; text past the field's length limit is cut off.
    /// </summary>
    private void Replace(TextBox box, int start, int end, string text)
    {
        var value = box.Text;
        start = Math.Clamp(start, 0, value.Length);
        end = Math.Clamp(end, start, value.Length);
        text = FieldKeystrokes.Fit(value, start, end, text, box.MaxLength);
        var (from, to, change) = FieldKeystrokes.Diff(value, value[..start] + text + value[end..]);
        if (from != to || change.Length > 0)
        {
            _applyingEdit = true;
            try
            {
                box.Select(from, to - from);
                box.SelectedText = change;
            }
            finally
            {
                _applyingEdit = false;
            }
        }
        box.Select(Math.Min(start + text.Length, box.Text.Length), 0);
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
