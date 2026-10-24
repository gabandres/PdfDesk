using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace InkvoltPdf.App;

public enum SaveChoice { Save, DontSave, Cancel }

/// <summary>
/// Fluent-styled message dialogs (title, explanation, clear verb buttons) instead of Win32 message boxes,
/// following the Windows guidance: say what happened, what it means, and offer specific actions.
/// </summary>
public static class Prompt
{
    public static void Error(Window? owner, string title, string body) =>
        Show(owner, title, body, "", "SystemFillColorCriticalBrush", [("OK", true)]);

    public static void Info(Window? owner, string title, string body) =>
        Show(owner, title, body, "", "AccentTextFillColorPrimaryBrush", [("OK", true)]);

    /// <summary>True when the user chose <paramref name="confirm"/>.</summary>
    public static bool Confirm(Window? owner, string title, string body, string confirm, bool warning = false) =>
        Show(owner, title, body, warning ? "" : "", warning ? "SystemFillColorCautionBrush" : "AccentTextFillColorPrimaryBrush",
            [(confirm, true), ("Cancel", false)]) == 0;

    public static SaveChoice SaveChanges(Window owner, string name) =>
        Show(owner, $"Save changes to {name}?", "Your page edits will be lost if you don't save them.", "",
            "AccentTextFillColorPrimaryBrush", [("Save", true), ("Don't save", false), ("Cancel", false)]) switch
        {
            0 => SaveChoice.Save,
            1 => SaveChoice.DontSave,
            _ => SaveChoice.Cancel,
        };

    /// <summary>Returns the index of the button pressed (Esc or closing = last button).</summary>
    private static int Show(Window? owner, string title, string body, string glyph, string glyphBrush, (string Text, bool Accent)[] buttons)
    {
        owner ??= Application.Current.MainWindow;
        var result = buttons.Length - 1;
        var win = new Window
        {
            Title = title, // the action, not just the app name (Alt+Tab, screen readers)
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        if (owner is { IsLoaded: true }) win.Owner = owner;

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.FindResource("Icons"),
            FontSize = 26,
            Margin = new Thickness(0, 2, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, glyphBrush);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var bodyText = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        bodyText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.Children.Add(bodyText);
        var top = new DockPanel { Margin = new Thickness(24, 22, 24, 22) };
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        top.Children.Add(text);

        var bar = new UniformGrid { Rows = 1, Margin = new Thickness(24, 0, 24, 0), HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i].Text, MinWidth = 104, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0) };
            if (buttons[i].Accent)
            {
                b.IsDefault = true;
                b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
            }
            if (i == buttons.Length - 1) b.IsCancel = true;
            b.Click += (_, _) =>
            {
                result = index;
                win.Close();
            };
            bar.Children.Add(b);
        }
        var footer = new Border { Padding = new Thickness(0, 16, 0, 16), Child = bar, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(top);
        win.Content = root;
        win.ShowDialog();
        return result;
    }
}

/// <summary>Every keyboard shortcut, read from the menus (so it is always current), with a filter box.</summary>
public static class ShortcutsWindow
{
    public static void Show(Window owner, Menu menu)
    {
        var rows = new List<(string Group, string Name, string Keys)>();
        void Walk(ItemsControl parent, string group)
        {
            foreach (var mi in parent.Items.OfType<MenuItem>())
            {
                var name = (mi.Header as string ?? (mi.Command as RoutedUICommand)?.Text ?? "").Replace("_", "").TrimEnd('…');
                if (mi.Items.Count > 0 && mi.Command is null)
                {
                    Walk(mi, group.Length == 0 ? name : group);
                    continue;
                }
                var keys = mi.Command is RoutedUICommand cmd
                    ? string.Join("  or  ", cmd.InputGestures.OfType<KeyGesture>().Select(g => g.GetDisplayStringForCulture(System.Globalization.CultureInfo.CurrentCulture)))
                    : "";
                if (keys.Length == 0 && !string.IsNullOrEmpty(mi.InputGestureText)) keys = mi.InputGestureText;
                if (keys.Length > 0 && !rows.Exists(r => r.Name == name)) rows.Add((group, name, keys));
            }
        }
        Walk(menu, "");
        // Commands the window binds but no menu shows (so new commands appear here as soon as they are bound).
        foreach (var field in typeof(Cmd).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.GetValue(null) is not RoutedUICommand cmd) continue;
            var gestures = cmd.InputGestures.OfType<KeyGesture>().ToList();
            if (gestures.Count == 0 || !owner.CommandBindings.Cast<CommandBinding>().Any(b => b.Command == cmd)) continue;
            var name = cmd.Text.Replace("_", "").TrimEnd('…');
            if (rows.Exists(r => r.Name == name)) continue;
            rows.Add(("Other commands", name, string.Join("  or  ", gestures.Select(g => g.GetDisplayStringForCulture(System.Globalization.CultureInfo.CurrentCulture)))));
        }
        rows.AddRange(
        [
            ("Reading", "Scroll by a screen", "PgUp / PgDn / Space"),
            ("Reading", "Zoom at the pointer", "Ctrl+mouse wheel or pinch"),
            ("Reading", "Select a word / line", "Double-click / triple-click (Select text tool)"),
            ("Pages pane", "Delete selected pages", "Del"),
            ("Pages pane", "Reorder pages", "Drag thumbnails (onto another tab to copy)"),
            ("Organize pages", "Move between pages", "Arrow keys (Shift extends the selection)"),
            ("Organize pages", "Move the selected pages", "Alt+Shift+arrow keys"),
            ("Organize pages", "Open the page", "Enter or double-click"),
            ("Organize pages", "Back to reading", "Esc"),
            ("Drawing", "Square, circle, or a line at 45°", "Shift while dragging"),
            ("Drawing", "Back to the hand tool", "Esc"),
            ("Drawing", "Select a drawing", "Click it (hand tool)"),
            ("Drawing", "Move the selected drawing", "Drag, or arrow keys (Shift: 10 pt)"),
            ("Drawing", "Resize the selected drawing", "Drag a handle (Shift keeps the shape)"),
            ("Drawing", "Delete the selected drawing", "Del"),
            ("Drawing", "Edit a text box or callout", "Double-click or Enter"),
            ("Drawing", "Finish typing in a text box", "Esc, Ctrl+Enter or click outside"),
            ("Pictures and signatures", "Move / resize the selected one", "Drag it / drag its corner"),
            ("Pictures and signatures", "Delete the selected one", "Del"),
            ("Links", "Add a link (Links tool)", "Drag a box over the text"),
            ("Links", "Change where the selected link leads", "Double-click or Enter"),
            ("Links", "Delete the selected link", "Del, or right-click › Delete Link"),
            ("Anywhere", "Search all commands", Cmd.GestureText(Cmd.CommandPalette) ?? ""),
        ]);

        var win = new Window
        {
            Owner = owner,
            Title = "Keyboard shortcuts",
            Width = 640,
            Height = 620,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var filter = new TextBox { Margin = new Thickness(20, 20, 20, 10) };
        System.Windows.Automation.AutomationProperties.SetName(filter, "Filter shortcuts");
        var list = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
        void Fill()
        {
            list.Children.Clear();
            var q = filter.Text.Trim();
            foreach (var g in rows.Where(r => q.Length == 0 || $"{r.Group} {r.Name} {r.Keys}".Contains(q, StringComparison.CurrentCultureIgnoreCase)).GroupBy(r => r.Group))
            {
                list.Children.Add(new TextBlock { Text = g.Key, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 14, 0, 6) });
                foreach (var r in g)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                    var keys = new Border { Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = new TextBlock { Text = r.Keys, FontSize = 12 } };
                    keys.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
                    keys.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                    DockPanel.SetDock(keys, Dock.Right);
                    row.Children.Add(keys);
                    row.Children.Add(new TextBlock { Text = r.Name, VerticalAlignment = VerticalAlignment.Center });
                    list.Children.Add(row);
                }
            }
        }
        filter.TextChanged += (_, _) => Fill();
        Fill();
        var root = new DockPanel();
        DockPanel.SetDock(filter, Dock.Top);
        root.Children.Add(filter);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        win.Content = root;
        win.Loaded += (_, _) => filter.Focus();
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.ShowDialog();
    }
}

/// <summary>
/// A long job's progress on Inkvolt PDF's taskbar button (indeterminate until a fraction is reported), so it shows while the
/// window is minimized or behind others. Jobs may overlap: the button shows progress until the last one ends.
/// </summary>
public sealed class TaskbarProgress : IDisposable
{
    private static readonly Dictionary<Window, int> Active = [];
    private readonly Window? _window;
    private bool _ended;

    public TaskbarProgress(Window? owner)
    {
        // The taskbar button belongs to the top-level window, not to a dialog it owns.
        var w = owner ?? Application.Current?.MainWindow;
        while (w?.Owner is { } parent) w = parent;
        _window = w;
        if (w is null) return;
        w.TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        Active[w] = Active.GetValueOrDefault(w) + 1;
        w.TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Indeterminate;
    }

    /// <summary>0..1; call on the UI thread.</summary>
    public void Report(double fraction)
    {
        if (_ended || _window?.TaskbarItemInfo is not { } info) return;
        // With several jobs running, one bar can't stand for all of them.
        if (Active.GetValueOrDefault(_window) > 1) return;
        info.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
        info.ProgressValue = Math.Clamp(fraction, 0, 1);
    }

    public void Dispose()
    {
        if (_ended || _window is null) return;
        _ended = true;
        var left = Active.GetValueOrDefault(_window) - 1;
        if (left > 0)
        {
            Active[_window] = left;
            if (_window.TaskbarItemInfo is { } busy) busy.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Indeterminate;
            return;
        }
        Active.Remove(_window);
        if (_window.TaskbarItemInfo is { } info) info.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
    }
}

/// <summary>
/// A small progress window with a Cancel button for long jobs (printing, exporting). It only appears if the job takes
/// longer than a moment. <see cref="Report"/> may be called from any thread.
/// </summary>
public sealed class ProgressPrompt : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Window _win;
    private readonly TextBlock _text;
    private readonly ProgressBar _bar;
    private readonly Button _cancel;
    private readonly System.Windows.Threading.DispatcherTimer _delay;
    private readonly TaskbarProgress _taskbar;
    private bool _closed;

    public CancellationToken Token { get; }

    /// <summary>Raised on the UI thread when the user cancels.</summary>
    public event Action? Cancelled;

    public ProgressPrompt(Window? owner, string title, string text)
    {
        Token = _cts.Token;
        owner ??= Application.Current.MainWindow;
        _taskbar = new TaskbarProgress(owner);
        _text = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        _bar = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 14, 0, 0) };
        _cancel = new Button { Content = "Cancel", MinWidth = 104, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        _cancel.Click += (_, _) => Cancel();
        _win = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        if (owner is { IsLoaded: true }) _win.Owner = owner;
        // Closing the window counts as Cancel; the job closes it when it ends.
        _win.Closing += (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true;
            Cancel();
        };

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(_text);
        body.Children.Add(_bar);
        var footer = new Border { Padding = new Thickness(24, 14, 24, 14), Child = _cancel, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        _win.Content = root;
        System.Windows.Automation.AutomationProperties.SetName(_bar, title);

        _delay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            if (!_closed) _win.Show();
        };
        _delay.Start();
    }

    /// <summary>Updates the message and (when given, 0..1) the progress bar.</summary>
    public void Report(string text, double? fraction = null)
    {
        if (!_win.Dispatcher.CheckAccess())
        {
            _win.Dispatcher.BeginInvoke(() => Report(text, fraction));
            return;
        }
        if (_closed || _cts.IsCancellationRequested) return;
        _text.Text = text;
        if (fraction is { } f)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = Math.Clamp(f, 0, 1) * 100;
            _taskbar.Report(f);
        }
    }

    private void Cancel()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _cancel.IsEnabled = false;
        _text.Text = "Cancelling…";
        Cancelled?.Invoke();
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _delay.Stop();
        _taskbar.Dispose();
        _win.Close();
    }
}
