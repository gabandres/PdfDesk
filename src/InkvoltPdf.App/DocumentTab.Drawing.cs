using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using InkvoltPdf.App.Viewer;
using InkvoltPdf.Core;

namespace InkvoltPdf.App;

/// <summary>Each drawing tool's look, remembered in the settings (the tool's default until changed).</summary>
public static class DrawStyles
{
    public static AnnotStyle Get(ViewerTool tool)
    {
        var d = DrawTools.DefaultStyle(tool);
        if (!AppSettings.Current.DrawStyles.TryGetValue(tool.ToString(), out var s)) return d;
        var style = d with
        {
            Color = s.Color is null ? null : Parse(s.Color) ?? d.Color,
            Fill = s.Fill is null ? null : Parse(s.Fill) ?? d.Fill,
            TextColor = Parse(s.TextColor) ?? d.TextColor,
            Width = double.IsFinite(s.Width) ? Math.Clamp(s.Width, 0, 100) : d.Width,
            Opacity = double.IsFinite(s.Opacity) ? Math.Clamp(s.Opacity, 0.1, 1) : d.Opacity,
            FontSize = double.IsFinite(s.FontSize) ? Math.Clamp(s.FontSize, 4, 144) : d.FontSize,
            StartEnding = Enum.TryParse<LineEnding>(s.StartEnding, out var se) ? se : d.StartEnding,
            EndEnding = Enum.TryParse<LineEnding>(s.EndEnding, out var ee) ? ee : d.EndEnding,
        };
        return IsValid(tool, style) ? style : d;
    }

    public static void Set(ViewerTool tool, AnnotStyle s)
    {
        AppSettings.Current.DrawStyles[tool.ToString()] = new AppSettings.DrawStyleSetting
        {
            Color = Hex(s.Color),
            Fill = Hex(s.Fill),
            TextColor = Hex(s.TextColor),
            Width = s.Width,
            Opacity = s.Opacity,
            FontSize = s.FontSize,
            StartEnding = s.StartEnding.ToString(),
            EndEnding = s.EndEnding.ToString(),
        };
        AppSettings.Current.Save();
    }

    /// <summary>What Core accepts: lines and ink need a stroke; shapes a stroke or a fill.</summary>
    public static bool IsValid(ViewerTool tool, AnnotStyle s) => DrawTools.Subtype(tool) switch
    {
        "Ink" or "Line" => s.Color is not null && s.Width > 0,
        "Square" or "Circle" => s.Color is not null && s.Width > 0 || s.Fill is not null,
        _ => true,
    };

    public static string? Hex(AnnotColor? c) => c is { } k
        ? $"#{(int)Math.Round(Math.Clamp(k.R, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(k.G, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(k.B, 0, 1) * 255):X2}"
        : null;

    public static AnnotColor? Parse(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? new AnnotColor(((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0)
            : null;
}

/// <summary>
/// Drawing annotations: turns the page view's gestures (new strokes and shapes, moves, resizes, text boxes, deletes)
/// into undoable edits, and runs the floating style bar for the drawing tool, the selected drawing or the open text box.
/// </summary>
public partial class DocumentTab
{
    private static readonly (string Name, AnnotColor Color)[] Swatches =
    [
        ("Black", new AnnotColor(0, 0, 0)),
        ("Red", new AnnotColor(0.9, 0.1, 0.1)),
        ("Orange", new AnnotColor(1, 0.55, 0)),
        ("Yellow", new AnnotColor(1, 0.85, 0.1)),
        ("Green", new AnnotColor(0.1, 0.6, 0.25)),
        ("Blue", new AnnotColor(0.1, 0.4, 0.9)),
        ("Purple", new AnnotColor(0.55, 0.25, 0.75)),
        ("Gray", new AnnotColor(0.5, 0.5, 0.5)),
    ];

    private static readonly double[] WidthSteps = [0.5, 1, 1.5, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 32];
    private static readonly double[] FontSteps = [6, 7, 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72];

    private (string Id, AnnotStyle Style)? _pendingStyle; // a style edit not in the document yet (the bar builds on it)
    private int _styleEdits;

    private void InitDrawing()
    {
        Viewer.CanAnnotate = Session.IsPdf;
        Viewer.DrawStyleFor = DrawStyles.Get;
        Viewer.EditorCompanion = StyleBar;
        Viewer.DrawingRequested += (_, r) => _ = AddDrawing(r);
        Viewer.DrawingChanged += (_, c) => _ = ChangeDrawing(c);
        Viewer.DrawingTextCommitted += (_, t) => _ = SaveDrawingText(t);
        Viewer.DrawingSelectionChanged += (_, _) =>
        {
            if (_pendingStyle is { } p && Viewer.SelectedDrawing?.Id != p.Id) _pendingStyle = null;
            UpdateStyleBar();
            SyncCommentSelection();
        };
        Viewer.ToolChanged += (_, _) => UpdateStyleBar();

        foreach (var (name, color) in Swatches)
        {
            var dot = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = Frozen(new SolidColorBrush(Color.FromRgb(Byte(color.R), Byte(color.G), Byte(color.B)))),
            };
            dot.SetResourceReference(Border.BorderBrushProperty, "ControlStrongStrokeColorDefaultBrush");
            var button = new ToggleButton { Content = dot, ToolTip = name, MinWidth = 28, Height = 30, Padding = new Thickness(5, 4, 5, 4), Tag = color };
            button.SetResourceReference(StyleProperty, "IconButton");
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => ApplyStyle("color", s => WithColor(s, color));
            StyleSwatches.Children.Add(button);
        }
        StyleCustomColor.Click += (_, _) =>
        {
            if (StyleTarget() is not { } t) return;
            var current = IsTextTool(t.Tool) ? t.Style.TextColor : t.Style.Color ?? AnnotStyle.Black;
            if (PickColor(current) is { } picked) ApplyStyle("color", s => WithColor(s, picked));
        };
        StyleThinner.Click += (_, _) => ApplyStyle("line width", s => s with { Width = Step(WidthSteps, s.Width, -1) });
        StyleThicker.Click += (_, _) => ApplyStyle("line width", s => s with { Width = Step(WidthSteps, s.Width, 1) });
        StyleSmaller.Click += (_, _) => ApplyStyle("text size", s => s with { FontSize = Step(FontSteps, s.FontSize, -1) });
        StyleLarger.Click += (_, _) => ApplyStyle("text size", s => s with { FontSize = Step(FontSteps, s.FontSize, 1) });
        StyleMoreClear.Click += (_, _) => ApplyStyle("opacity", s => s with { Opacity = Math.Max(0.1, Math.Round(s.Opacity * 10 - 1) / 10) });
        StyleLessClear.Click += (_, _) => ApplyStyle("opacity", s => s with { Opacity = Math.Min(1, Math.Round(s.Opacity * 10 + 1) / 10) });
        StyleFill.Click += (_, _) => ApplyStyle("fill", s => s with
        {
            Fill = s.Fill is not null ? null : StyleTarget() is { } t && IsTextTool(t.Tool) ? new AnnotColor(1, 1, 1) : Tint(s.Color ?? AnnotStyle.Black),
        });
        StyleBorder.Click += (_, _) => ApplyStyle("border", s => s.Color is null ? s with { Color = s.TextColor, Width = 1 } : s with { Color = null, Width = 0 });
        StyleStartArrow.Click += (_, _) => ApplyStyle("arrowheads", s => s with { StartEnding = s.StartEnding == LineEnding.None ? LineEnding.OpenArrow : LineEnding.None });
        StyleEndArrow.Click += (_, _) => ApplyStyle("arrowheads", s => s with { EndEnding = s.EndEnding == LineEnding.None ? LineEnding.OpenArrow : LineEnding.None });
        StyleDelete.Click += (_, _) =>
        {
            if (Viewer.SelectedDrawing is { } a) _ = ChangeDrawing(new DrawingChange(a, DrawingEdit.Delete, null));
            Viewer.Focus();
        };
        UpdateStyleBar();
    }

    private static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static bool IsTextTool(ViewerTool tool) => DrawTools.IsText(tool);

    /// <summary>A light version of a color, for fills.</summary>
    private static AnnotColor Tint(AnnotColor c) => new(1 - (1 - c.R) * 0.3, 1 - (1 - c.G) * 0.3, 1 - (1 - c.B) * 0.3);

    private AnnotStyle WithColor(AnnotStyle s, AnnotColor c) =>
        StyleTarget() is { } t && IsTextTool(t.Tool)
            ? s with { TextColor = c, Color = s.Color is null ? null : c }
            : s with { Color = c, Fill = s.Fill is null ? null : Tint(c) };

    private static double Step(double[] steps, double value, int direction) => direction > 0
        ? steps.FirstOrDefault(v => v > value + 1e-6, steps[^1])
        : steps.LastOrDefault(v => v < value - 1e-6, steps[0]);

    /// <summary>
    /// What the style bar works on: the open text box, else the selected drawing, else the drawing tool's look.
    /// <c>Annot</c> is set for a selected drawing; <c>Text</c> for an open text box (<c>Existing</c>: it is in the document).
    /// </summary>
    private (ViewerTool Tool, AnnotStyle Style, AnnotationInfo? Annot, bool Text, bool Existing)? StyleTarget()
    {
        if (IsOrganizing) return null;
        if (Viewer.EditingText is { } t) return (t.Tool, t.Style, null, true, t.Existing);
        if (Viewer.SelectedDrawing is { } a && DrawTools.ToolOf(a) is { } tool)
            return (tool, _pendingStyle is { } p && p.Id == a.Id ? p.Style : a.Style!, a, false, true);
        if (DrawTools.IsDrawTool(Viewer.Tool)) return (Viewer.Tool, DrawStyles.Get(Viewer.Tool), null, false, false);
        return null;
    }

    private void UpdateStyleBar()
    {
        if (StyleTarget() is not { } t)
        {
            StyleBar.Visibility = Visibility.Collapsed;
            return;
        }
        var s = t.Style;
        var text = IsTextTool(t.Tool);
        var subtype = t.Annot?.Subtype ?? DrawTools.Subtype(t.Tool);
        StyleBar.Visibility = Visibility.Visible;
        StyleBarIcon.Text = DrawTools.Glyph(t.Tool);
        StyleBarTitle.Text = t.Annot is { } a ? CommentItem.TitleOf(a) : t.Text ? DrawTools.Title(t.Tool) : DrawTools.ToolName(t.Tool);
        var shown = text ? s.TextColor : s.Color;
        foreach (var b in StyleSwatches.Children.OfType<ToggleButton>())
            b.IsChecked = shown is { } c && b.Tag is AnnotColor k && Math.Abs(c.R - k.R) + Math.Abs(c.G - k.G) + Math.Abs(c.B - k.B) < 0.02;
        StyleWidthGroup.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
        StyleWidthText.Text = $"{s.Width:0.#} pt";
        StyleFontGroup.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        StyleFontText.Text = $"{s.FontSize:0.#} pt";
        StyleOpacityText.Text = $"{s.Opacity * 100:0}%";
        StyleFillGroup.Visibility = text || subtype is "Square" or "Circle" or "Polygon" ? Visibility.Visible : Visibility.Collapsed;
        StyleFill.IsChecked = s.Fill is not null;
        StyleBorder.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        StyleBorder.IsChecked = s.Color is not null;
        StyleArrowGroup.Visibility = subtype == "Line" ? Visibility.Visible : Visibility.Collapsed;
        StyleStartArrow.IsChecked = s.StartEnding != LineEnding.None;
        StyleEndArrow.IsChecked = s.EndEnding != LineEnding.None;
        StyleDeleteGroup.Visibility = t.Annot is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Applies a style change from the bar to its target (one undo step for a drawing in the document).</summary>
    private void ApplyStyle(string what, Func<AnnotStyle, AnnotStyle> change)
    {
        var mouse = InputManager.Current.MostRecentInputDevice is MouseDevice or StylusDevice;
        if (StyleTarget() is { } t)
        {
            var next = change(t.Style);
            if (DrawStyles.IsValid(t.Tool, next) && next != t.Style)
            {
                if (t.Text)
                {
                    Viewer.SetEditingTextStyle(next);
                    if (!t.Existing) DrawStyles.Set(t.Tool, next);
                }
                else if (t.Annot is { } a)
                {
                    var id = a.Id;
                    _pendingStyle = (id, next);
                    _ = RestyleDrawing($"Change {CommentItem.TitleOf(a).ToLowerInvariant()} {what}", id, next);
                }
                else
                {
                    DrawStyles.Set(t.Tool, next);
                    Viewer.RefreshDrawPreview();
                }
            }
        }
        UpdateStyleBar();
        // Clicked with the mouse: the keys (arrows, Delete, typing) go back to the page or the text box.
        if (Viewer.EditingText is not null) Viewer.FocusEditor();
        else if (mouse) Viewer.Focus();
    }

    private async Task RestyleDrawing(string label, string id, AnnotStyle style)
    {
        _styleEdits++;
        try
        {
            await RunDrawingEdit(label, (d, pw) => Annotations.UpdateStyle(d, pw, id, style));
        }
        finally
        {
            if (--_styleEdits == 0) _pendingStyle = null;
        }
    }

    // ───────────────────────────── Edits ─────────────────────────────

    /// <summary>An undoable annotation edit; waits while another edit (or its prompt) holds the tab.</summary>
    private async Task<bool> RunDrawingEdit(string label, Func<byte[], string?, byte[]> edit, string? pendingId = null, bool quiet = false)
    {
        BeginWrite();
        try
        {
            while (true)
            {
                while (IsBusy)
                {
                    await WhenIdle();
                    if (_disposed) return false;
                }
                var outcome = await TryEdit(label, edit, quiet: quiet, contentOnly: true, veil: false);
                if (outcome == EditOutcome.Busy && !_disposed) continue;
                var ok = outcome is EditOutcome.Done or EditOutcome.NoChange;
                if (!ok && pendingId is not null) Viewer.ForgetPendingDrawing(pendingId);
                return ok;
            }
        }
        finally
        {
            EndWrite();
        }
    }

    private Task<bool> AddDrawing(DrawingRequest r)
    {
        var (page, g, s, id, author) = (r.PageIndex, r.Geometry, r.Style, r.Id, Author);
        Func<byte[], string?, byte[]> edit = r.Tool switch
        {
            ViewerTool.Pen or ViewerTool.Highlighter => (d, pw) => Annotations.AddInk(d, pw, page, g.Paths, s, author, id),
            ViewerTool.Rectangle => (d, pw) => Annotations.AddShape(d, pw, page, ShapeKind.Rectangle, g.Box, s, author, id),
            ViewerTool.Ellipse => (d, pw) => Annotations.AddShape(d, pw, page, ShapeKind.Ellipse, g.Box, s, author, id),
            _ => (d, pw) => Annotations.AddLine(d, pw, page, g.Paths[0][0], g.Paths[0][1], s, author, id),
        };
        // Quiet: what was drawn is on the page (Ctrl+Z undoes it); a toast per stroke would only get in the way.
        return RunDrawingEdit($"Add {DrawTools.Title(r.Tool).ToLowerInvariant()}", edit, id, quiet: true);
    }

    private Task<bool> ChangeDrawing(DrawingChange c)
    {
        var id = c.Annotation.Id;
        var title = CommentItem.TitleOf(c.Annotation).ToLowerInvariant();
        if (c.Geometry is not { } g)
            return RunDrawingEdit($"Delete {title}", (d, pw) => Annotations.Delete(d, pw, id), id);
        var verb = c.Edit switch { DrawingEdit.Move => "Move", DrawingEdit.Resize => "Resize", _ => "Reshape" };
        return RunDrawingEdit($"{verb} {title}", (d, pw) => Annotations.UpdateGeometry(d, pw, id, g), id, quiet: true);
    }

    private Task<bool> SaveDrawingText(DrawingTextCommit t)
    {
        var (page, g, s, text, author) = (t.PageIndex, t.Geometry, t.Style, t.Text, Author);
        var title = DrawTools.Title(t.Tool).ToLowerInvariant();
        if (t.Id is not { } id)
        {
            var newId = t.NewId;
            return RunDrawingEdit($"Add {title}", (d, pw) => Annotations.AddFreeText(d, pw, page, g.Box, text, s, author, g.Callout.Count > 0 ? g.Callout : null, newId),
                quiet: true);
        }
        if (text.Length == 0) return RunDrawingEdit($"Delete {title}", (d, pw) => Annotations.Delete(d, pw, id));
        if (!t.TextChanged && !t.GeometryChanged && !t.StyleChanged) return Task.FromResult(false);
        return RunDrawingEdit(t.TextChanged ? $"Edit {title}" : $"Change {title}", (d, pw) =>
        {
            if (t.TextChanged) d = Annotations.SetContents(d, pw, id, text);
            if (t.StyleChanged) d = Annotations.UpdateStyle(d, pw, id, s);
            if (t.GeometryChanged) d = Annotations.UpdateGeometry(d, pw, id, g);
            return d;
        }, quiet: true);
    }

    /// <summary>A drawing selected on the page is selected in the Comments pane too.</summary>
    private void SyncCommentSelection()
    {
        if (_syncingComments || Viewer.SelectedDrawing is not { } a) return;
        if (_commentItems.FirstOrDefault(c => c.Info.Id == a.Id) is not { } item || CommentList.SelectedItem == item) return;
        _syncingComments = true;
        try
        {
            CommentList.SelectedItem = item;
            CommentList.ScrollIntoView(item);
        }
        finally
        {
            _syncingComments = false;
        }
    }

    // ───────────────────────────── Custom color ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct ChooseColorInfo
    {
        public int Size;
        public IntPtr Owner;
        public IntPtr Instance;
        public int Rgb;
        public IntPtr CustomColors;
        public int Flags;
        public IntPtr Data;
        public IntPtr Hook;
        public IntPtr Template;
    }

    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
    private static extern bool ChooseColor(ref ChooseColorInfo info);

    private static IntPtr _customColors;

    /// <summary>The Windows color picker (null: cancelled). Its 16 custom colors are kept while Inkvolt PDF runs.</summary>
    private AnnotColor? PickColor(AnnotColor current)
    {
        if (_customColors == IntPtr.Zero)
        {
            _customColors = Marshal.AllocHGlobal(16 * 4);
            for (var i = 0; i < 16; i++) Marshal.WriteInt32(_customColors, i * 4, 0xFFFFFF);
        }
        var info = new ChooseColorInfo
        {
            Size = Marshal.SizeOf<ChooseColorInfo>(),
            Owner = Window.GetWindow(this) is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero,
            Rgb = Byte(current.R) | Byte(current.G) << 8 | Byte(current.B) << 16,
            CustomColors = _customColors,
            Flags = 0x1 | 0x2 | 0x100, // CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR
        };
        if (!ChooseColor(ref info)) return null;
        return new AnnotColor((info.Rgb & 0xFF) / 255.0, ((info.Rgb >> 8) & 0xFF) / 255.0, ((info.Rgb >> 16) & 0xFF) / 255.0);
    }

    // ───────────────────────────── Dev entries ─────────────────────────────

    internal IReadOnlyList<AnnotationInfo> DevDrawings => Viewer.DevDrawings;

    /// <summary>Dev/test: a style bar change by name (color #RRGGBB, thinner, thicker, smaller, larger, clearer, opaque, fill, border, start, end, delete).</summary>
    internal void DevStyle(string what, string? value)
    {
        switch (what)
        {
            case "color" when DrawStyles.Parse(value) is { } c: ApplyStyle("color", s => WithColor(s, c)); break;
            case "thinner": StyleThinner.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
            case "thicker": StyleThicker.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
            case "smaller": StyleSmaller.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
            case "larger": StyleLarger.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
            case "clearer": StyleMoreClear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
            case "opaque": StyleLessClear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
            case "fill": StyleFill.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
            case "border": StyleBorder.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
            case "start": StyleStartArrow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
            case "end": StyleEndArrow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
            case "delete": StyleDelete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
            default: throw new ArgumentException($"Unknown style change '{what}'.");
        }
    }

    internal string DevStyleBar => StyleBar.Visibility != Visibility.Visible ? "(hidden)"
        : $"{StyleBarTitle.Text}: width {StyleWidthText.Text} ({StyleWidthGroup.Visibility}), font {StyleFontText.Text} ({StyleFontGroup.Visibility}), " +
          $"opacity {StyleOpacityText.Text}, fill={StyleFill.IsChecked} ({StyleFillGroup.Visibility}), arrows {StyleStartArrow.IsChecked}/{StyleEndArrow.IsChecked} ({StyleArrowGroup.Visibility}), " +
          $"color={StyleSwatches.Children.OfType<ToggleButton>().FirstOrDefault(b => b.IsChecked == true)?.ToolTip ?? "custom"}";
}
