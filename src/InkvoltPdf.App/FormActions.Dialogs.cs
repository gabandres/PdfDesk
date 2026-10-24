using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InkvoltPdf.Core;

namespace InkvoltPdf.App;

internal enum ConsentChoice { Once, Always, Cancel }

/// <summary>
/// The dialogs form actions open: consent before anything leaves the document (naming the exact destination and what
/// is sent), and the script dialogs app.alert and app.response. Styled like <see cref="Prompt"/>.
/// </summary>
internal static class FormActionDialogs
{
    /// <summary>
    /// Asks before an outward action: "Allow once", "Always allow for this destination in this document" (the same data
    /// to the same place; anything else asks again), or "Cancel" (also Esc; it stops the form's script too).
    /// </summary>
    public static ConsentChoice Consent(Window? owner, string title, string body, string destination) =>
        Show(owner, title, body, destination, "Only continue if you trust this document.", null, "", "SystemFillColorCautionBrush",
            [("Allow once", true), ("Always allow for this destination in this document", false), ("Cancel", false)], focus: 2) switch
        {
            0 => ConsentChoice.Once,
            1 => ConsentChoice.Always,
            _ => ConsentChoice.Cancel,
        };

    /// <summary>app.alert: returns 1 OK, 2 Cancel, 3 No, 4 Yes.</summary>
    public static int Alert(Window? owner, FormAlert a)
    {
        (string, bool)[] buttons;
        int[] codes;
        switch (a.Buttons)
        {
            case 1:
                buttons = [("OK", true), ("Cancel", false)];
                codes = [1, 2];
                break;
            case 2:
                buttons = [("Yes", true), ("No", false)];
                codes = [4, 3];
                break;
            case 3:
                buttons = [("Yes", true), ("No", false), ("Cancel", false)];
                codes = [4, 3, 2];
                break;
            default:
                buttons = [("OK", true)];
                codes = [1];
                break;
        }
        var (glyph, brush) = a.Icon switch
        {
            0 => ("", "SystemFillColorCriticalBrush"),
            1 => ("", "SystemFillColorCautionBrush"),
            2 => ("", "AccentTextFillColorPrimaryBrush"),
            _ => ("", "AccentTextFillColorPrimaryBrush"),
        };
        var title = a.Title.Length > 0 ? a.Title : "Message from the form";
        var i = Show(owner, title, a.Message, null, "This message comes from the document's script.", null, glyph, brush, buttons, focus: 0);
        return codes[Math.Clamp(i, 0, codes.Length - 1)];
    }

    /// <summary>app.response: the text entered, or null when cancelled.</summary>
    public static string? Response(Window? owner, FormPrompt p)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (p.Label.Length > 0) panel.Children.Add(new TextBlock { Text = p.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        Control input = p.Password ? new PasswordBox { Password = p.Default } : new TextBox { Text = p.Default };
        System.Windows.Automation.AutomationProperties.SetName(input, p.Label.Length > 0 ? p.Label : "Answer");
        panel.Children.Add(input);
        input.Loaded += (_, _) =>
        {
            input.Focus();
            if (input is TextBox tb) tb.SelectAll();
        };
        var title = p.Title.Length > 0 ? p.Title : "The form asks";
        var i = Show(owner, title, p.Question, null, "This question comes from the document's script.", panel, "", "AccentTextFillColorPrimaryBrush",
            [("OK", true), ("Cancel", false)], focus: -1);
        if (i != 0) return null;
        return input switch
        {
            PasswordBox pb => pb.Password,
            TextBox tb => tb.Text,
            _ => null,
        };
    }

    /// <summary>Returns the index of the button pressed (Esc or closing = the last one).</summary>
    private static int Show(Window? owner, string title, string body, string? destination, string? note, UIElement? extra, string glyph, string glyphBrush,
        (string Text, bool Accent)[] buttons, int focus)
    {
        owner ??= Application.Current.MainWindow;
        var result = buttons.Length - 1;
        var win = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is { IsLoaded: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
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
        bodyText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        text.Children.Add(new ScrollViewer { Content = bodyText, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (destination is not null)
        {
            var dest = new TextBlock
            {
                Text = destination.Length > 600 ? destination[..600] + "…" : destination,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                TextWrapping = TextWrapping.Wrap,
            };
            var box = new Border { Child = dest, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 10, 0, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
            box.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
            box.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            System.Windows.Automation.AutomationProperties.SetName(dest, "Destination: " + destination);
            text.Children.Add(box);
        }
        if (extra is not null) text.Children.Add(extra);
        if (note is not null)
        {
            var noteText = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
            noteText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            text.Children.Add(noteText);
        }
        var top = new DockPanel { Margin = new Thickness(24, 22, 24, 22) };
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        top.Children.Add(text);

        var bar = new WrapPanel { Margin = new Thickness(24, 0, 24, 0), HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i].Text, MinWidth = 96, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), Padding = new Thickness(12, 5, 12, 6) };
            if (buttons[i].Accent)
            {
                b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
                if (focus < 0) b.IsDefault = true; // a text prompt: Enter answers it
            }
            if (i == buttons.Length - 1) b.IsCancel = true;
            if (i == focus) b.Loaded += (_, _) => b.Focus();
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
