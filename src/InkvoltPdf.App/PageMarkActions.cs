using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using InkvoltPdf.App.Viewer;
using InkvoltPdf.Core;

namespace InkvoltPdf.App;

/// <summary>
/// Organize › Add Header &amp; Footer (page numbers, dates, Bates numbers): a dialog with a live preview of the first page
/// in the range, then one undoable edit that writes the text into the pages. Remove takes off what Inkvolt PDF added.
/// </summary>
public static class PageMarkActions
{
    /// <summary>Text colors offered for headers, footers and watermarks.</summary>
    internal static readonly (string Name, AnnotColor Color)[] Colors =
    [
        ("Black", new AnnotColor(0, 0, 0)),
        ("Dark gray", new AnnotColor(0.3, 0.3, 0.3)),
        ("Gray", new AnnotColor(0.5, 0.5, 0.5)),
        ("Red", new AnnotColor(0.8, 0.1, 0.1)),
        ("Blue", new AnnotColor(0.1, 0.3, 0.8)),
        ("Green", new AnnotColor(0.1, 0.5, 0.2)),
    ];

    /// <summary>The last header and footer applied (this session): the dialog opens with it.</summary>
    private static HeaderFooterOptions _lastHeaderFooter = new() { FooterCenter = "Page <<page>> of <<pages>>" };
    private static int _lastHeaderFooterColor;
    private static bool _lastBatesOn;

    private static string Pages(int n) => n == 1 ? "1 page" : $"{n} pages";

    private static string Inches(double pt) => (pt / 72).ToString("0.##");

    public static async Task AddHeaderFooter(Window owner, DocumentTab tab)
    {
        if (!tab.Session.IsPdf || !await tab.SettleAsync()) return;
        var last = _lastHeaderFooter;
        var dlg = new FormDialog(owner, "Add Header & Footer", "Apply", 860);
        dlg.AddNote("Written into the pages, so it prints and shows in every viewer. Applying again replaces the header and footer on those pages; Organize › Remove Header & Footer takes them off.");

        // Six boxes: header and footer, left / center / right.
        var slots = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        slots.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 3; i++) slots.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) slots.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] columns = ["Left", "Center", "Right"];
        for (var c = 0; c < 3; c++) Place(slots, new TextBlock { Text = columns[c], Style = Caption, Margin = new Thickness(c == 0 ? 0 : 6, 0, 0, 2) }, 0, c + 1);
        var boxes = new Dictionary<HeaderFooterSlot, TextBox>();
        foreach (var slot in Enum.GetValues<HeaderFooterSlot>())
        {
            var row = slot <= HeaderFooterSlot.HeaderRight ? 1 : 2;
            var col = (int)slot % 3;
            var box = new TextBox { Text = last.Text(slot), Margin = new Thickness(col == 0 ? 0 : 6, 2, 0, 2) };
            AutomationProperties.SetName(box, $"{(row == 1 ? "Header" : "Footer")} {columns[col].ToLowerInvariant()} text");
            boxes[slot] = box;
            Place(slots, box, row, col + 1);
        }
        Place(slots, new TextBlock { Text = "Header", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, 1, 0);
        Place(slots, new TextBlock { Text = "Footer", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, 2, 0);

        // Token buttons insert into the box last focused (the footer's center at first).
        var target = boxes[HeaderFooterSlot.FooterCenter];
        foreach (var box in boxes.Values) box.GotKeyboardFocus += (s, _) => target = (TextBox)s;
        var tokens = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        tokens.Children.Add(new TextBlock { Text = "Insert:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        foreach (var (name, token) in new[] { ("Page number", "<<page>>"), ("Page count", "<<pages>>"), ("Date", "<<date>>"), ("Time", "<<time>>"), ("File name", "<<file>>"), ("Bates number", "<<bates>>") })
        {
            var b = new Button { Content = name, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 2, 4, 2), ToolTip = $"Insert {token} where the cursor is" };
            b.Click += (_, _) =>
            {
                var at = target.SelectionStart;
                target.SelectedText = token;
                target.SelectionLength = 0;
                target.CaretIndex = at + token.Length;
                target.Focus();
            };
            tokens.Children.Add(b);
        }

        // Look: size, color, margins (inches from the page's edges).
        var look = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        var size = Field(look, "Font size (pt)", last.FontSize.ToString("0.#"), 0, 0);
        var color = new ComboBox { ItemsSource = Colors.Select(c => c.Name).ToList(), SelectedIndex = _lastHeaderFooterColor, Margin = new Thickness(0, 3, 0, 3) };
        AutomationProperties.SetName(color, "Text color");
        Labeled(look, "Color", color, 0, 2);
        var top = Field(look, "Top margin (in)", Inches(last.MarginTop), 1, 0);
        var bottom = Field(look, "Bottom margin (in)", Inches(last.MarginBottom), 1, 2);
        var left = Field(look, "Left margin (in)", Inches(last.MarginLeft), 2, 0);
        var right = Field(look, "Right margin (in)", Inches(last.MarginRight), 2, 2);

        // Bates numbering: <<bates>> becomes prefix + running number + suffix.
        var bates = new CheckBox { Content = "Bates numbering (<<bates>>: a running number for legal documents)", IsChecked = _lastBatesOn, Margin = new Thickness(0, 10, 0, 0) };
        var batesGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        var lastBates = last.Bates ?? new BatesFormat();
        var prefix = Field(batesGrid, "Prefix", lastBates.Prefix, 0, 0);
        var start = Field(batesGrid, "Start at", lastBates.Start.ToString(), 0, 2);
        var digits = Field(batesGrid, "Digits", lastBates.Digits.ToString(), 1, 0);
        var suffix = Field(batesGrid, "Suffix", lastBates.Suffix, 1, 2);
        void BatesShown() => batesGrid.IsEnabled = bates.IsChecked == true;
        BatesShown();
        bates.Click += (_, _) =>
        {
            BatesShown();
            // Turned on with nowhere to show it: the footer's right box, as in court filings.
            if (bates.IsChecked == true && !boxes.Values.Any(b => b.Text.Contains("<<bates>>", StringComparison.OrdinalIgnoreCase)))
                boxes[HeaderFooterSlot.FooterRight].Text = (boxes[HeaderFooterSlot.FooterRight].Text + " <<bates>>").Trim();
        };

        var range = new PageRangePicker(tab, preferSelection: false);

        var form = new StackPanel();
        form.Children.Add(slots);
        form.Children.Add(tokens);
        form.Children.Add(Heading("Look"));
        form.Children.Add(look);
        form.Children.Add(bates);
        form.Children.Add(batesGrid);
        form.Children.Add(Heading("Pages"));
        form.Children.Add(range);

        var preview = new PagePreview(tab);
        dlg.Add(null, SideBySide(form, preview));

        bool TryRead(out HeaderFooterOptions options, out int[] pages, out string? error)
        {
            options = last;
            pages = [];
            if (!range.TryGet(out pages, out error)) return false;
            if (!FormDialog.TryNumber(size.Text, 4, 72, out var fontSize)) return Fail("The font size must be from 4 to 72 points.", out error);
            var m = new double[4];
            var boxesM = new[] { top, bottom, left, right };
            for (var i = 0; i < 4; i++)
                if (!FormDialog.TryNumber(boxesM[i].Text, 0, 20, out m[i])) return Fail("Margins must be numbers of inches from 0 to 20.", out error);
            BatesFormat? b = null;
            var usesBates = boxes.Values.Any(x => x.Text.Contains("<<bates>>", StringComparison.OrdinalIgnoreCase));
            if (bates.IsChecked == true)
            {
                if (!long.TryParse(start.Text.Trim(), out var s) || s < 0 || s > 999_999_999_999) return Fail("The Bates number must start at a whole number of 0 or more.", out error);
                if (!int.TryParse(digits.Text.Trim(), out var d) || d is < 1 or > 12) return Fail("Bates numbers can have 1 to 12 digits.", out error);
                b = new BatesFormat(prefix.Text, s, d, suffix.Text);
                if (!usesBates) return Fail("Put <<bates>> in a header or footer box (Insert › Bates number).", out error);
            }
            else if (usesBates) return Fail("Turn on Bates numbering for <<bates>>, or remove it.", out error);
            options = new HeaderFooterOptions
            {
                HeaderLeft = boxes[HeaderFooterSlot.HeaderLeft].Text,
                HeaderCenter = boxes[HeaderFooterSlot.HeaderCenter].Text,
                HeaderRight = boxes[HeaderFooterSlot.HeaderRight].Text,
                FooterLeft = boxes[HeaderFooterSlot.FooterLeft].Text,
                FooterCenter = boxes[HeaderFooterSlot.FooterCenter].Text,
                FooterRight = boxes[HeaderFooterSlot.FooterRight].Text,
                FontSize = fontSize,
                Color = Colors[Math.Max(0, color.SelectedIndex)].Color,
                MarginTop = m[0] * 72,
                MarginBottom = m[1] * 72,
                MarginLeft = m[2] * 72,
                MarginRight = m[3] * 72,
                Bates = b,
                FileName = tab.Session.DisplayName,
                Date = DateTime.Now,
            };
            if (options.IsEmpty) return Fail("Type the text of a header or a footer.", out error);
            return true;
        }

        var pageCount = tab.PageView.PageCount;
        void Refresh()
        {
            if (!TryRead(out var o, out var pages, out var error))
            {
                preview.Show(error ?? "");
                return;
            }
            var first = pages.Min();
            preview.Request(first, page => HeaderFooter.Apply(page, null, [0], o with { PageNumberOffset = first, PageCount = pageCount }));
        }
        WatchChanges(dlg, Refresh);
        dlg.Loaded += (_, _) => Refresh();

        HeaderFooterOptions chosen = last;
        int[] chosenPages = [];
        dlg.Validate(() => TryRead(out chosen, out chosenPages, out var error) ? null : error);
        if (!dlg.ShowModal() || !await tab.SettleAsync()) return;
        _lastHeaderFooter = chosen;
        _lastHeaderFooterColor = Math.Max(0, color.SelectedIndex);
        _lastBatesOn = chosen.Bates is not null;
        var options = chosen with { Date = DateTime.Now };
        var targets = chosenPages.Where(p => p < tab.PageView.PageCount).ToArray();
        if (targets.Length == 0) return;
        await tab.RunEdit($"Add header & footer to {Pages(targets.Length)}", (d, pw) => HeaderFooter.Apply(d, pw, targets, options));
    }

    public static async Task RemoveHeaderFooter(DocumentTab tab)
    {
        if (!tab.Session.IsPdf || !await tab.SettleAsync()) return;
        if (await tab.TryEdit("Remove header & footer", HeaderFooter.Remove) == EditOutcome.NoChange)
            tab.ShowNotice("No header or footer added by Inkvolt PDF was found. Headers made by other apps are part of the page and can't be told apart.");
    }

    // ───────────────────────────── Dialog helpers ─────────────────────────────

    private static Style Caption => (Style)Application.Current.FindResource("Caption");

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) };

    private static void Place(Grid grid, UIElement e, int row, int col)
    {
        while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        grid.Children.Add(e);
    }

    /// <summary>A label and a control in a two-pairs-per-row grid (columns: label, control, label, control).</summary>
    internal static T Labeled<T>(Grid grid, string label, T control, int row, int col) where T : FrameworkElement
    {
        if (grid.ColumnDefinitions.Count == 0)
        {
            // Label columns line up across the dialog's grids (the form is a shared size scope).
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Label1" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Label2" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        Place(grid, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(col == 0 ? 0 : 16, 3, 8, 3) }, row, col);
        if (AutomationProperties.GetName(control).Length == 0) AutomationProperties.SetName(control, label);
        Place(grid, control, row, col + 1);
        return control;
    }

    internal static TextBox Field(Grid grid, string label, string value, int row, int col) =>
        Labeled(grid, label, new TextBox { Text = value, Margin = new Thickness(0, 3, 0, 3) }, row, col);

    /// <summary>The options on the left, the preview on the right.</summary>
    internal static Grid SideBySide(UIElement form, PagePreview preview)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetIsSharedSizeScope(form, true);
        Grid.SetColumn(preview, 1);
        preview.Margin = new Thickness(20, 0, 0, 0);
        grid.Children.Add(form);
        grid.Children.Add(preview);
        return grid;
    }

    /// <summary>Calls <paramref name="changed"/> after any typing, choice or check in the dialog.</summary>
    internal static void WatchChanges(Window dlg, Action changed)
    {
        dlg.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => changed()));
        dlg.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => changed()));
        dlg.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => changed()));
        dlg.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => changed()));
    }
}

/// <summary>
/// A small preview of one page with a page edit applied, for dialogs: the page is extracted once, then each request
/// (debounced while typing) runs the edit on that one page and renders it, off the UI thread; the newest request wins.
/// </summary>
internal sealed class PagePreview : StackPanel
{
    private const double PreviewWidth = 260;
    private readonly DocumentTab _tab;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, MaxHeight = 360 };
    private readonly TextBlock _caption = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), MaxWidth = PreviewWidth };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private (int Page, Func<byte[], byte[]> Edit)? _wanted;
    private (int Page, byte[]? Bytes, string? Error)? _extracted;
    private int _ticket;

    public PagePreview(DocumentTab tab)
    {
        _tab = tab;
        Width = PreviewWidth;
        _caption.Style = (Style)Application.Current.FindResource("Caption");
        var frame = new Border { BorderThickness = new Thickness(1), Background = Brushes.White, Child = _image, MinHeight = 120 };
        frame.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        Children.Add(new TextBlock { Text = "Preview", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        Children.Add(frame);
        Children.Add(_caption);
        AutomationProperties.SetName(_image, "Preview");
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = RenderAsync();
        };
    }

    /// <summary>Shows a message instead of a new preview (e.g. why the options can't be used yet).</summary>
    public void Show(string message)
    {
        _wanted = null;
        _timer.Stop();
        _caption.Text = message;
    }

    /// <summary>Previews page <paramref name="page"/> (0-based) with <paramref name="edit"/> run on it as a one-page PDF.</summary>
    public void Request(int page, Func<byte[], byte[]> edit)
    {
        _wanted = (page, edit);
        _timer.Stop();
        _timer.Start();
    }

    private async Task RenderAsync()
    {
        if (_wanted is not { } want) return;
        var ticket = ++_ticket;
        var data = _tab.Session.Data;
        var password = _tab.Session.Password;
        try
        {
            if (_extracted is not { } ex || ex.Page != want.Page)
            {
                byte[]? bytes = null;
                string? error = null;
                try
                {
                    bytes = await Task.Run(() => PageEditor.Extract(data, password, [want.Page]));
                }
                catch (Exception e) when (e is InvalidOperationException or OwnerPasswordRequiredException or ArgumentException)
                {
                    error = e is OwnerPasswordRequiredException ? "No preview: editing this file needs its permissions password." : $"No preview: {e.Message}";
                }
                _extracted = (want.Page, bytes, error);
            }
            if (ticket != _ticket) return;
            if (_extracted is { Error: { } err })
            {
                _caption.Text = err;
                return;
            }
            var pageBytes = _extracted!.Value.Bytes!;
            var bmp = await Task.Run(() =>
            {
                var edited = want.Edit(pageBytes);
                using var engine = PdfEngine.Open(edited, MuPDFCore.InputFileTypes.PDF, null);
                var g = engine.GetPage(0);
                var scale = Math.Min(2 * PreviewWidth / Math.Max(1, g.Width), 2 * 360 / Math.Max(1, g.Height));
                return PageViewer.ToBitmap(engine.Render(0, scale));
            });
            if (ticket != _ticket) return;
            _image.Source = bmp;
            _caption.Text = $"Page {want.Page + 1}";
            AutomationProperties.SetName(_image, $"Preview of page {want.Page + 1}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (ticket == _ticket) _caption.Text = $"No preview: {e.Message}";
        }
    }
}
