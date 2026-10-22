using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PdfDesk.App;

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
            Title = "PdfDesk",
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
        rows.AddRange(
        [
            ("Reading", "Scroll by a screen", "PgUp / PgDn / Space"),
            ("Reading", "Zoom at the pointer", "Ctrl+mouse wheel or pinch"),
            ("Reading", "Select a word / line", "Double-click / triple-click (Select text tool)"),
            ("Pages pane", "Delete selected pages", "Del"),
            ("Pages pane", "Reorder pages", "Drag thumbnails (onto another tab to copy)"),
            ("Organize pages", "Move between pages", "Arrow keys (Shift extends the selection)"),
            ("Organize pages", "Open the page", "Enter or double-click"),
            ("Organize pages", "Back to reading", "Esc"),
            ("Anywhere", "Search all commands", "Ctrl+K"),
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
