using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using InkvoltPdf.Core;
using InkvoltPrint.Core.Escl;
using InkvoltPrint.Core.Net;
using InkvoltPrint.Core.Suite;
using InkvoltPrint.Windows.Discovery;
using ScanPage = InkvoltPrint.Core.Pdf.PdfPage;
using ScanPdfWriter = InkvoltPrint.Core.Pdf.PdfWriter;

namespace InkvoltPdf.App;

/// <summary>File › Scan… (a new tab) and Organize › Insert from Scanner… (into the current document, one undo step).</summary>
internal static class Scanning
{
    public static void ScanToNewTab(MainWindow host)
    {
        var name = $"Scan {DateTime.Now:yyyy-MM-dd HHmm}.pdf";
        var dlg = new ScanDialog(host, name, insert: null);
        if (dlg.ShowDialog() != true || dlg.Result is not { } pdf) return;
        host.OpenSession(DocumentSession.FromBytes(pdf, name));
        host.ShowStatus($"Scanned {Pages(dlg.PageCount)}. Save it to keep it.");
    }

    public static async Task InsertFromScanner(MainWindow host, DocumentTab tab)
    {
        if (!await tab.SettleAsync()) return;
        var name = $"Scan {DateTime.Now:yyyy-MM-dd HHmm}.pdf";
        var dlg = new ScanDialog(host, name, insert: (tab.PageView.PageCount, Organize.CurrentPage(tab)));
        if (dlg.ShowDialog() != true || dlg.Result is not { } pdf || !await tab.SettleAsync()) return;
        var before = Math.Clamp(dlg.InsertBefore, 0, tab.PageView.PageCount); // the document may have changed meanwhile
        var source = new PdfSource(pdf, null, name);
        await tab.RunEdit($"Insert {Pages(dlg.PageCount)} from the scanner", (d, pw) => PageEditor.Insert(d, pw, source, null, before), before);
    }

    private static string Pages(int n) => n == 1 ? "1 page" : $"{n} pages";
}

/// <summary>
/// Scans over eSCL with Inkvolt Print's client: the scanners Inkvolt Print remembers plus any found on the network, source (glass,
/// feeder, both sides), color, resolution and page size. Pages arrive as JPEGs, become PDF pages as they are (Inkvolt Print's
/// writer embeds them without re-encoding), and optionally get Inkvolt PDF's invisible OCR text layer. All device and file
/// work runs off the UI thread; Cancel stops a scan (the job is deleted on the device).
/// </summary>
internal sealed class ScanDialog : Window
{
    private sealed record ScannerItem(string Uuid, string Name, string Host, Uri? EsclRoot, bool Remembered)
    {
        public override string ToString() => $"{Name}  ({Host}){(Remembered ? "" : " · found on the network")}";
    }

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record Scanned(byte[] Jpeg, int Dpi);

    private readonly string _title;
    private readonly (int Count, int Current)? _insert;
    private readonly ObservableCollection<ScannerItem> _scanners = [];
    private readonly List<Scanned> _pages = [];

    private readonly ComboBox _scanner = new() { MinWidth = 300 };
    private readonly TextBlock _scannerState = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ComboBox _source = new();
    private readonly ComboBox _color = new();
    private readonly ComboBox _resolution = new();
    private readonly ComboBox _size = new();
    private readonly CheckBox _ocr = new() { Content = "Make the text searchable (OCR)", Margin = new Thickness(0, 14, 0, 0) };
    private readonly PositionPicker? _position;
    private readonly WrapPanel _thumbs = new();
    private readonly TextBlock _pageCount = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _removeLast = new() { Content = "Remove last page", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _scan = new() { Content = "Scan", MinWidth = 104, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _stop = new() { Content = "Stop scanning", MinWidth = 104, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly Button _finish = new() { MinWidth = 104, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _close = new() { Content = "Cancel", MinWidth = 104 };

    private PrinterDiscovery? _discovery;
    private DeviceHttp? _http;
    private EsclClient? _client;
    private ScannerCapabilities? _caps;
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _scanCts;
    private bool _busy;
    private bool _filling;
    private bool _closeRequested;

    /// <summary>The finished PDF (OK only).</summary>
    public byte[]? Result { get; private set; }
    public int PageCount { get; private set; }

    /// <summary>Insert mode: the insertion index chosen (0..page count).</summary>
    public int InsertBefore { get; private set; }

    /// <param name="insert">Insert mode: the document's page count and current page; null scans into a new document.</param>
    public ScanDialog(Window owner, string documentName, (int Count, int Current)? insert)
    {
        _title = Path.GetFileNameWithoutExtension(documentName);
        _insert = insert;
        Owner = owner;
        Title = insert is null ? "Scan" : "Insert from Scanner";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _scanner.ItemsSource = _scanners;
        System.Windows.Automation.AutomationProperties.SetName(_scanner, "Scanner");
        System.Windows.Automation.AutomationProperties.SetName(_source, "Source");
        System.Windows.Automation.AutomationProperties.SetName(_color, "Color");
        System.Windows.Automation.AutomationProperties.SetName(_resolution, "Resolution");
        System.Windows.Automation.AutomationProperties.SetName(_size, "Page size");
        _scannerState.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _finish.Content = insert is null ? "Open as new document" : "Insert pages";
        _finish.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _scan.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _scan.IsDefault = true;

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
        body.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        body.Children.Add(Heading("Scanner", 0));
        body.Children.Add(_scanner);
        body.Children.Add(_scannerState);

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Cell(string label, ComboBox box, int row, int col)
        {
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cell = new StackPanel();
            cell.Children.Add(Heading(label, 12));
            cell.Children.Add(box);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            grid.Children.Add(cell);
        }
        Cell("Source", _source, 0, 0);
        Cell("Color", _color, 0, 2);
        Cell("Resolution", _resolution, 1, 0);
        Cell("Page size", _size, 1, 2);
        body.Children.Add(grid);

        var ocrAvailable = PageOcr.Engine is not null;
        _ocr.IsChecked = ocrAvailable && AppSettings.Current.ScanOcr;
        _ocr.IsEnabled = ocrAvailable;
        _ocr.ToolTip = ocrAvailable
            ? "Adds an invisible text layer with Windows' text recognition, so the scan can be searched, selected and copied."
            : PageOcr.Unavailable;
        body.Children.Add(_ocr);

        if (insert is { } ins)
        {
            body.Children.Add(Heading("Insert", 14));
            _position = new PositionPicker(ins.Count, ins.Current);
            body.Children.Add(_position);
        }

        // Scanned pages: thumbnails, count, remove-last.
        var pagesHeader = new DockPanel { Margin = new Thickness(0, 16, 0, 6) };
        DockPanel.SetDock(_removeLast, Dock.Right);
        pagesHeader.Children.Add(_removeLast);
        pagesHeader.Children.Add(_pageCount);
        body.Children.Add(pagesHeader);
        var strip = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            MinHeight = 60,
            Child = new ScrollViewer { Content = _thumbs, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        strip.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        strip.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        body.Children.Add(strip);
        _message.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(_message);
        body.Children.Add(_progress);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 16, 24, 16) };
        buttons.Children.Add(_scan);
        buttons.Children.Add(_stop);
        buttons.Children.Add(_finish);
        buttons.Children.Add(_close);
        var footer = new Border { Child = buttons, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        _scanner.SelectionChanged += (_, _) => { if (!_filling) _ = ConnectAsync(); };
        _source.SelectionChanged += (_, _) => { if (!_filling) FillOptions(); };
        _scan.Click += (_, _) => _ = ScanAsync();
        _stop.Click += (_, _) => _scanCts?.Cancel();
        _finish.Click += (_, _) => _ = FinishAsync();
        // Cancel and Esc close through Closing (which stops a running scan and asks about scanned pages) rather than IsCancel.
        _close.Click += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape || e.Handled) return;
            e.Handled = true;
            Close();
        };
        _removeLast.Click += (_, _) =>
        {
            if (_busy || _pages.Count == 0) return;
            _pages.RemoveAt(_pages.Count - 1);
            _thumbs.Children.RemoveAt(_thumbs.Children.Count - 1);
            UpdateState();
        };
        Loaded += (_, _) => _ = LoadScannersAsync();
        Closing += OnClosing;
        Closed += (_, _) => Cleanup();
        UpdateState();
    }

    private static TextBlock Heading(string text, double top) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 4) };

    /// <summary>Remembered printers with a scanner first (instant), then whatever discovery finds on the network.</summary>
    private async Task LoadScannersAsync()
    {
        _scannerState.Text = "Looking for scanners…";
        var known = await Task.Run(() => InkvoltPrinters.Load());
        foreach (var p in known.Printers.Where(p => p.EsclRoot is not null))
            AddOrUpdate(new ScannerItem(Key(p.Uuid), p.Name, p.Host, p.EsclRoot, Remembered: true));
        try
        {
            _discovery = new PrinterDiscovery();
            _discovery.Changed += list => Dispatcher.InvokeAsync(() => OnDiscovered(list));
            _discovery.InitialSweepCompleted += () => Dispatcher.InvokeAsync(() =>
            {
                if (_scanners.Count == 0)
                    _scannerState.Text = "No scanner found. Turn the scanner on and connect it to this network; Inkvolt Print can add one by its address.";
            });
            _discovery.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"Scanner discovery unavailable: {ex.Message}");
            if (_scanners.Count == 0) _scannerState.Text = "No scanner found (network discovery isn't available).";
        }
        if (_scanner.SelectedItem is null && _scanners.Count > 0) SelectPreferred();
    }

    private void OnDiscovered(IReadOnlyList<DiscoveredPrinter> found)
    {
        if (!IsLoaded) return;
        var hadNone = _scanners.Count == 0;
        foreach (var d in found.Where(d => d.HasScanner && d.Host is not null && d.EsclRoot is not null))
            AddOrUpdate(new ScannerItem(Key(d.Uuid), d.Name, d.Host!, d.EsclRoot, Remembered: false));
        if (hadNone && _scanners.Count > 0) SelectPreferred();
    }

    /// <summary>Adds a scanner, or refreshes a known one's address from discovery (DHCP may have moved it).</summary>
    private void AddOrUpdate(ScannerItem item)
    {
        var i = _scanners.ToList().FindIndex(s => s.Uuid == item.Uuid);
        if (i < 0)
        {
            _scanners.Add(item);
            return;
        }
        var old = _scanners[i];
        if (old.Host == item.Host && old.EsclRoot == item.EsclRoot) return;
        var selected = ReferenceEquals(_scanner.SelectedItem, old);
        _filling = true;
        try
        {
            _scanners[i] = item with { Name = old.Name, Remembered = old.Remembered };
            if (selected) _scanner.SelectedItem = _scanners[i];
        }
        finally
        {
            _filling = false;
        }
        if (selected && _caps is null) _ = ConnectAsync(); // the old address didn't answer: try the new one
    }

    private void SelectPreferred() =>
        _scanner.SelectedItem = _scanners.FirstOrDefault(s => s.Uuid == AppSettings.Current.ScanDevice) ?? _scanners[0];

    private static string Key(string uuid) => Guid.TryParse(uuid.Trim('{', '}'), out var g) ? g.ToString("D") : uuid.ToLowerInvariant();

    /// <summary>Reads the chosen scanner's capabilities (its stored eSCL address, then the standard one).</summary>
    private async Task ConnectAsync()
    {
        _connectCts?.Cancel();
        if (_scanner.SelectedItem is not ScannerItem item) return;
        var cts = _connectCts = new CancellationTokenSource();
        _caps = null;
        _client = null;
        var oldHttp = _http;
        _http = null;
        oldHttp?.Dispose();
        FillOptions();
        _scannerState.Text = $"Connecting to {item.Name}…";
        UpdateState();

        var host = item.EsclRoot?.Host ?? item.Host;
        var http = new DeviceHttp(host);
        try
        {
            ScannerCapabilities? caps = null;
            EsclClient? client = null;
            foreach (var root in new[] { item.EsclRoot, http.Http("/eSCL/") }.OfType<Uri>().Distinct())
            {
                try
                {
                    var c = new EsclClient(http, root);
                    caps = await c.GetCapabilitiesAsync(cts.Token);
                    if (caps is not null) { client = c; break; }
                }
                catch (Exception ex) when (ex is HttpRequestException or TimeoutException or DeviceHttpException or System.Xml.XmlException or FormatException)
                {
                    Log.Write($"Scanner {Log.Name(root.Host)}: {ex.GetType().Name}");
                }
            }
            if (cts.IsCancellationRequested) { http.Dispose(); return; }
            if (caps is null || client is null)
            {
                http.Dispose();
                _scannerState.Text = $"{item.Name} didn't answer as a scanner. Check that it's on and on this network.";
                UpdateState();
                return;
            }
            _http = http;
            _client = client;
            _caps = caps;
            _scannerState.Text = $"Ready: {caps.MakeAndModel ?? item.Name}";
            FillOptions();
            UpdateState();
        }
        catch (OperationCanceledException)
        {
            http.Dispose();
        }
    }

    /// <summary>Fills source/color/resolution/size from the capabilities, keeping current (or remembered) choices.</summary>
    private void FillOptions()
    {
        _filling = true;
        try
        {
            if (_caps is not { } caps)
            {
                foreach (var box in new[] { _source, _color, _resolution, _size }) box.ItemsSource = null;
                return;
            }
            var s = AppSettings.Current;
            var prevSource = (_source.SelectedItem as Choice<ScanSource>)?.Value.ToString() ?? s.ScanSource;
            var sources = ScanChoices.Sources(caps).Select(x => new Choice<ScanSource>(x, ScanChoices.SourceLabel(x))).ToList();
            if (_source.ItemsSource is not IEnumerable<Choice<ScanSource>> current || !current.SequenceEqual(sources))
            {
                _source.ItemsSource = sources;
                _source.SelectedItem = sources.FirstOrDefault(x => x.Value.ToString() == prevSource) ?? sources.FirstOrDefault();
            }
            if (_source.SelectedItem is not Choice<ScanSource> source) return;

            var colorWanted = (_color.SelectedItem as Choice<ScanColor>)?.Value ?? (s.ScanColor ? ScanColor.Color : ScanColor.Grayscale);
            var colors = ScanChoices.Colors(caps, source.Value).Select(c => new Choice<ScanColor>(c, c == ScanColor.Color ? "Color" : "Grayscale")).ToList();
            _color.ItemsSource = colors;
            _color.SelectedItem = colors.FirstOrDefault(c => c.Value == colorWanted) ?? colors[0];

            var dpiWanted = (_resolution.SelectedItem as Choice<int>)?.Value ?? s.ScanResolution;
            var dpis = ScanChoices.Resolutions(caps, source.Value).Select(d => new Choice<int>(d, d switch
            {
                <= 150 => $"{d} dpi (smaller files)",
                <= 200 => $"{d} dpi",
                _ => $"{d} dpi (best for text)",
            })).ToList();
            _resolution.ItemsSource = dpis;
            _resolution.SelectedItem = dpis.MinBy(d => Math.Abs(d.Value - dpiWanted));

            var sizeWanted = (_size.SelectedItem as Choice<ScanPageSize>)?.Value.Key ?? s.ScanPageSize;
            var offered = ScanChoices.PageSizesFor(caps, source.Value);
            var sizes = offered.Select(p => new Choice<ScanPageSize>(p, p.Label)).ToList();
            var preferred = ScanChoices.DefaultPageSize(offered, sizeWanted, RegionInfo.CurrentRegion.IsMetric);
            _size.ItemsSource = sizes;
            _size.SelectedItem = sizes.First(x => x.Value == preferred);
        }
        finally
        {
            _filling = false;
            UpdateState();
        }
    }

    private bool IsGlass => (_source.SelectedItem as Choice<ScanSource>)?.Value is ScanSource.Platen or null;

    private async Task ScanAsync()
    {
        if (_busy || _client is not { } client || _scanner.SelectedItem is not ScannerItem item
            || _source.SelectedItem is not Choice<ScanSource> source || _resolution.SelectedItem is not Choice<int> dpi) return;
        var color = (_color.SelectedItem as Choice<ScanColor>)?.Value ?? ScanColor.Color;
        var size = (_size.SelectedItem as Choice<ScanPageSize>)?.Value;
        var s = AppSettings.Current;
        (s.ScanDevice, s.ScanSource, s.ScanColor, s.ScanResolution, s.ScanPageSize, s.ScanOcr) =
            (item.Uuid, source.Value.ToString(), color == ScanColor.Color, dpi.Value, size?.Key ?? "", _ocr.IsChecked == true);
        s.Save();

        var request = ScanChoices.ToRequest(source.Value, color, dpi.Value, size);
        using var cts = _scanCts = new CancellationTokenSource();
        var added = 0;
        SetBusy(true, scanning: true);
        _message.Text = "Scanning… (the first page takes longer if the scanner was asleep)";
        try
        {
            await foreach (var jpeg in client.ScanAsync(request, cts.Token))
            {
                try
                {
                    ScanPdfWriter.JpegInfo(jpeg); // a page the PDF can't hold (no size) is reported, not kept
                }
                catch (FormatException)
                {
                    _message.Text = "The scanner sent a page that can't be read; it was left out.";
                    continue;
                }
                _pages.Add(new Scanned(jpeg, dpi.Value));
                _thumbs.Children.Add(await ThumbnailAsync(jpeg, _pages.Count));
                added++;
                UpdateState();
                _message.Text = $"Scanned page {_pages.Count}. Scanning…";
            }
            _message.Text = added == 0
                ? "The scanner returned no pages."
                : IsGlass
                    ? $"Done. For another page, put it on the glass and press Scan another page; or press {_finish.Content}."
                    : $"Done: {added} page{(added == 1 ? "" : "s")} from the feeder.";
        }
        catch (OperationCanceledException)
        {
            _message.Text = added == 0 ? "Scan stopped." : $"Scan stopped. The {added} page{(added == 1 ? "" : "s")} scanned so far are kept.";
        }
        catch (ScanException ex)
        {
            _message.Text = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or DeviceHttpException or InvalidOperationException
                                       or System.Xml.XmlException or IOException)
        {
            Log.Write($"Scan failed: {ex.GetType().Name}: {ex.Message}");
            _message.Text = $"Scanning didn't work: {ex.Message}";
        }
        finally
        {
            _scanCts = null;
            SetBusy(false);
            if (_closeRequested) _ = Dispatcher.BeginInvoke(Close); // closed while scanning: close now that the job stopped
        }
    }

    private static Task<FrameworkElement> ThumbnailAsync(byte[] jpeg, int number) =>
        Task.Run(() =>
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 72;
            img.StreamSource = new MemoryStream(jpeg);
            img.EndInit();
            img.Freeze();
            return img;
        }).ContinueWith(t =>
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            if (t.IsCompletedSuccessfully)
                panel.Children.Add(new System.Windows.Controls.Image { Source = t.Result, Width = 72, Height = 96, Stretch = System.Windows.Media.Stretch.Uniform });
            else
                panel.Children.Add(new Border { Width = 72, Height = 96 });
            panel.Children.Add(new TextBlock { Text = number.ToString(CultureInfo.CurrentCulture), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 });
            System.Windows.Automation.AutomationProperties.SetName(panel, $"Scanned page {number}");
            return (FrameworkElement)panel;
        }, TaskScheduler.FromCurrentSynchronizationContext());

    /// <summary>Builds the PDF (JPEG pages as they are), adds the OCR text layer if chosen, then closes with the result.</summary>
    private async Task FinishAsync()
    {
        if (_busy || _pages.Count == 0) return;
        var before = 0;
        if (_position is not null && !_position.TryGet(out before, out var error))
        {
            _message.Text = error;
            return;
        }
        var pages = _pages.Select(p => new ScanPage(p.Jpeg, p.Dpi)).ToList();
        var ocr = _ocr.IsChecked == true ? PageOcr.Engine : null;
        AppSettings.Current.ScanOcr = _ocr.IsChecked == true;
        SetBusy(true);
        _message.Text = "Making the PDF…";
        try
        {
            var title = _title;
            var pdf = await Task.Run(() =>
            {
                using var ms = new MemoryStream();
                ScanPdfWriter.Write(ms, pages, title);
                return ms.ToArray();
            });
            if (ocr is not null)
            {
                using var progress = new ProgressPrompt(this, "Recognizing text", "Starting…");
                try
                {
                    pdf = await Task.Run(() =>
                    {
                        var (words, _) = DocumentTab.RecognizePages(ocr, pdf, null, [.. Enumerable.Range(0, pages.Count)], progress, "Recognizing text");
                        return words.Count == 0 ? pdf : TextLayer.AddInvisibleText(pdf, null, words);
                    }, progress.Token);
                }
                catch (OperationCanceledException)
                {
                    _message.Text = "Text recognition cancelled. Your pages are still here; clear \"Make the text searchable\" to skip it.";
                    return;
                }
            }
            Result = pdf;
            PageCount = pages.Count;
            InsertBefore = before;
            _pages.Clear(); // handed over: closing doesn't ask about them
            DialogResult = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or IOException or ArgumentException or PdfSharp.PdfSharpException)
        {
            Log.Write($"Scan to PDF failed: {ex}");
            _message.Text = $"The scan couldn't be made into a PDF: {ex.Message}";
        }
        finally
        {
            if (IsLoaded) SetBusy(false);
        }
    }

    private void SetBusy(bool busy, bool scanning = false)
    {
        _busy = busy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _stop.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        _scan.Visibility = scanning ? Visibility.Collapsed : Visibility.Visible;
        UpdateState();
    }

    private void UpdateState()
    {
        var ready = _caps is not null && !_busy;
        _scanner.IsEnabled = _source.IsEnabled = _color.IsEnabled = _resolution.IsEnabled = _size.IsEnabled = !_busy;
        _scan.IsEnabled = ready;
        _scan.Content = _pages.Count == 0 ? "Scan" : IsGlass ? "Scan another page" : "Scan more pages";
        _finish.IsEnabled = _pages.Count > 0 && !_busy;
        _finish.IsDefault = _pages.Count > 0;
        _scan.IsDefault = _pages.Count == 0;
        _removeLast.Visibility = _pages.Count > 0 ? Visibility.Visible : Visibility.Hidden;
        _removeLast.IsEnabled = !_busy;
        _pageCount.Text = _pages.Count switch { 0 => "No pages yet", 1 => "1 page", var n => $"{n} pages" };
        if (_pages.Count == 0 && _thumbs.Children.Count == 0 && !_busy && _message.Text.Length == 0 && _caps is not null)
            _message.Text = IsGlass ? "Put a page face down on the scanner glass, then press Scan." : "Put the pages face up in the document feeder, then press Scan.";
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DialogResult == true) return;
        if (_scanCts is { } scanning)
        {
            scanning.Cancel(); // stops the job on the device; the dialog closes once it has
            _closeRequested = true;
            e.Cancel = true;
            _message.Text = "Stopping the scan…";
            return;
        }
        if (_busy)
        {
            e.Cancel = true;
            return;
        }
        if (_pages.Count > 0 && !Prompt.Confirm(this, $"Discard {(_pages.Count == 1 ? "the scanned page" : $"{_pages.Count} scanned pages")}?",
                "They haven't been added to a document yet.", "Discard", warning: true))
            e.Cancel = true;
    }

    private void Cleanup()
    {
        _connectCts?.Cancel();
        _scanCts?.Cancel();
        _discovery?.Dispose();
        _http?.Dispose();
    }

    /// <summary>Dev check: adds a page as if the scanner had sent it (no scanner needed).</summary>
    internal async Task DevAddPageAsync(byte[] jpeg, int dpi)
    {
        ScanPdfWriter.JpegInfo(jpeg);
        _pages.Add(new Scanned(jpeg, dpi));
        _thumbs.Children.Add(await ThumbnailAsync(jpeg, _pages.Count));
        UpdateState();
    }

    /// <summary>Dev check: the scanners listed and the dialog's state.</summary>
    internal string DevDescribe() =>
        $"scanners=[{string.Join(" | ", _scanners)}] selected={_scanner.SelectedItem} state='{_scannerState.Text}' " +
        $"source={_source.SelectedItem} color={_color.SelectedItem} dpi={_resolution.SelectedItem} size={_size.SelectedItem} " +
        $"ocr={_ocr.IsChecked} pages={_pages.Count} scan={_scan.IsEnabled} message='{_message.Text}'";
}
