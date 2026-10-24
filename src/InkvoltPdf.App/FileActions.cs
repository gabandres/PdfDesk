using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InkvoltPdf.App.Viewer;
using InkvoltPdf.Core;

namespace InkvoltPdf.App;

/// <summary>Print, export to images, and document properties.</summary>
public static class FileActions
{
    public static async Task Print(Window owner, DocumentTab tab)
    {
        // Let a value still being typed land first: the dialog takes focus, which would commit it mid-print.
        if (!await tab.SettleAsync()) return;
        var count = tab.PageView.PageCount;
        var selection = tab.SelectedPages;
        var current = Organize.CurrentPage(tab);
        var dlg = new PrintDialog
        {
            UserPageRangeEnabled = true,
            CurrentPageEnabled = true,
            SelectedPagesEnabled = selection.Length > 1,
            MinPage = 1,
            MaxPage = (uint)count,
            PageRange = new System.Windows.Controls.PageRange(1, count),
        };
        PrinterCheck.Prewarm(); // the default printer's status, while the dialog is open
        if (dlg.ShowDialog() != true || !await tab.SettleAsync()) return;
        if (!await ConfirmPrinterReady(owner, PrinterCheck.Describe(dlg.PrintQueue))) return;
        count = tab.PageView.PageCount;

        int[] pages = dlg.PageRangeSelection switch
        {
            PageRangeSelection.UserPages => [.. Enumerable.Range(Math.Max(1, dlg.PageRange.PageFrom) - 1,
                Math.Max(0, Math.Min(count, dlg.PageRange.PageTo) - Math.Max(1, dlg.PageRange.PageFrom) + 1))],
            PageRangeSelection.CurrentPage => [current],
            PageRangeSelection.SelectedPages => selection,
            _ => [.. Enumerable.Range(0, count)],
        };
        pages = [.. pages.Where(p => p >= 0 && p < count)];
        if (pages.Length == 0) return;

        // Busy before anything else can run: the print holds this engine until it is done.
        using var busy = tab.BeginBusy($"Printing {Pages(pages.Length)}…");
        var (origin, area) = ImageableArea(dlg);
        using var progress = new ProgressPrompt(owner, "Printing", $"Preparing {Pages(pages.Length)}…");
        var paginator = new PdfPaginator(tab.ViewEngine, pages, new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight), origin, area, progress.Token);
        try
        {
            // Pages render on a background thread (PdfPaginator.GetPageAsync), so the window stays responsive.
            var writer = PrintQueue.CreateXpsDocumentWriter(dlg.PrintQueue);
            progress.Cancelled += () =>
            {
                try { writer.CancelAsync(); } catch (InvalidOperationException) { }
            };
            writer.WritingProgressChanged += (_, e) =>
            {
                if (e.WritingLevel == System.Windows.Documents.Serialization.WritingProgressChangeLevel.FixedPageWritingProgress)
                    progress.Report($"Sending page {Math.Min(e.Number, pages.Length)} of {pages.Length} to the printer…", (double)e.Number / pages.Length);
            };
            await WriteAsync(writer, paginator, dlg.PrintTicket);
            Log.Write($"Printed {Pages(pages.Length)}");
        }
        catch (Exception ex) when (ex is OperationCanceledException || progress.Token.IsCancellationRequested)
        {
            Log.Write("Printing cancelled");
            (Window.GetWindow(tab) as MainWindow)?.ShowStatus("Printing cancelled");
        }
        catch (Exception ex)
        {
            Log.Write($"Print failed: {ex}");
            Prompt.Error(owner, "Printing didn't work", ex.Message);
        }
    }

    private static string Pages(int n) => n == 1 ? "1 page" : $"{n} pages";

    /// <summary>
    /// Inkvolt suite: when the chosen queue is a printer Inkvolt Print knows, shows its status (ready, sleeping, toner) and asks
    /// before printing to one that is offline or has an error. Takes at most <see cref="PrinterCheck.Budget"/>; without an
    /// answer, printing goes ahead.
    /// </summary>
    private static async Task<bool> ConfirmPrinterReady(Window owner, InkvoltPrint.Core.Suite.QueueInfo? queue)
    {
        if (queue is null || await PrinterCheck.CheckAsync(queue) is not { } check) return true;
        var (printer, health) = (check.Printer, check.Health);
        Log.Write($"Printer check: {printer.Name}: {health.State}");
        if (!health.ShouldWarn)
        {
            (owner as MainWindow)?.ShowStatus($"{printer.Name}: {health.Summary}");
            return true;
        }
        var body = health.State == InkvoltPrint.Core.Suite.Readiness.Offline
            ? "It didn't answer just now: it may be off, or not on this network. If you print anyway, Windows keeps the job until the printer is back."
            : $"{health.Summary}.\n\nFix it on the printer first, or print anyway: the job waits in the Windows queue until the printer can print it.";
        return Prompt.Confirm(owner, $"{printer.Name} {PrinterCheck.Problem(health)}", body, "Print anyway", warning: true);
    }

    private static Task WriteAsync(System.Windows.Xps.XpsDocumentWriter writer, DocumentPaginator paginator, System.Printing.PrintTicket? ticket)
    {
        var done = new TaskCompletionSource();
        writer.WritingCompleted += (_, e) =>
        {
            if (e.Error is not null) done.TrySetException(e.Error);
            else if (e.Cancelled) done.TrySetCanceled();
            else done.TrySetResult();
        };
        writer.WritingCancelled += (_, _) => done.TrySetCanceled();
        if (ticket is null) writer.WriteAsync(paginator);
        else writer.WriteAsync(paginator, ticket);
        return done.Task;
    }

    /// <summary>Dev check: runs the print pipeline into an XPS file (Letter paper, quarter-inch margins).</summary>
    internal static async Task<int> WriteXps(PdfEngine engine, int[] pages, string path)
    {
        var paper = new Size(816, 1056);
        var paginator = new PdfPaginator(engine, pages, paper, new Point(24, 24), new Size(768, 1008));
        File.Delete(path);
        using var xps = new System.Windows.Xps.Packaging.XpsDocument(path, FileAccess.ReadWrite);
        await WriteAsync(System.Windows.Xps.Packaging.XpsDocument.CreateXpsDocumentWriter(xps), paginator, null);
        return paginator.PageCount;
    }

    /// <summary>The printer's imageable area in DIPs; falls back to quarter-inch margins.</summary>
    private static (Point Origin, Size Area) ImageableArea(PrintDialog dlg)
    {
        var paper = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
        try
        {
            var caps = dlg.PrintQueue.GetPrintCapabilities(dlg.PrintTicket);
            if (caps.PageImageableArea is { } ia && ia.ExtentWidth > 0 && ia.ExtentHeight > 0)
                return (new Point(ia.OriginWidth, ia.OriginHeight), new Size(ia.ExtentWidth, ia.ExtentHeight));
        }
        catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or InvalidOperationException)
        {
            Log.Write($"Printer capabilities unavailable: {ex.Message}");
        }
        return (new Point(24, 24), new Size(Math.Max(1, paper.Width - 48), Math.Max(1, paper.Height - 48)));
    }

    /// <summary>Renders each page at print resolution on demand, shrinking oversized pages and auto-rotating to fit the paper.</summary>
    private sealed class PdfPaginator(PdfEngine engine, int[] pages, Size paper, Point origin, Size area, CancellationToken cancel = default) : DocumentPaginator
    {
        private const double Dpi = 300;

        public override bool IsPageCountValid => true;
        public override int PageCount => pages.Length;
        public override Size PageSize { get => paper; set { } }
        public override IDocumentPaginatorSource? Source => null;

        private sealed record Placed(BitmapSource Image, bool Rotate, double X, double Y, double W, double H);

        public override DocumentPage GetPage(int pageNumber) => Build(Place(pageNumber));

        /// <summary>Renders on a worker thread and completes on the UI thread (used by XpsDocumentWriter.WriteAsync).</summary>
        public override void GetPageAsync(int pageNumber, object userState)
        {
            Task.Run(() => Place(pageNumber), cancel).ContinueWith(t => OnGetPageCompleted(
                    t.IsCanceled || cancel.IsCancellationRequested ? new GetPageCompletedEventArgs(null, pageNumber, null, true, userState)
                    : t.IsFaulted ? new GetPageCompletedEventArgs(null, pageNumber, t.Exception!.InnerException, false, userState)
                    : new GetPageCompletedEventArgs(Build(t.Result), pageNumber, null, false, userState)),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private Placed Place(int pageNumber)
        {
            cancel.ThrowIfCancellationRequested();
            var index = pages[pageNumber];
            var g = engine.GetPage(index);
            var wDip = g.Width * 96 / 72;
            var hDip = g.Height * 96 / 72;

            // Rotate landscape pages onto portrait paper (and vice versa) when that fits better.
            var rotate = (wDip > hDip) != (area.Width > area.Height) && Math.Abs(wDip - hDip) > 1;
            var (fw, fh) = rotate ? (hDip, wDip) : (wDip, hDip);
            var fit = Math.Min(area.Width / fw, area.Height / fh);
            var scale = Math.Min(1, fit); // shrink oversized pages; never enlarge small ones

            var pxScale = Dpi / 72 * scale;
            while (g.Width * g.Height * pxScale * pxScale > 60_000_000) pxScale *= 0.85;
            var bmp = PageViewer.ToBitmap(engine.Render(index, pxScale, null, cancel));

            var dw = fw * scale;
            var dh = fh * scale;
            return new Placed(bmp, rotate, origin.X + (area.Width - dw) / 2, origin.Y + (area.Height - dh) / 2, dw, dh);
        }

        private DocumentPage Build(Placed p)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                if (p.Rotate)
                {
                    // Turn the page 90° clockwise into the (W × H) box.
                    dc.PushTransform(new TranslateTransform(p.X + p.W, p.Y));
                    dc.PushTransform(new RotateTransform(90));
                    dc.DrawImage(p.Image, new Rect(0, 0, p.H, p.W));
                    dc.Pop();
                    dc.Pop();
                }
                else
                {
                    dc.DrawImage(p.Image, new Rect(p.X, p.Y, p.W, p.H));
                }
            }
            return new DocumentPage(visual, paper, new Rect(paper), new Rect(p.X, p.Y, p.W, p.H));
        }
    }

    public static async Task ExportImages(Window owner, DocumentTab tab, MainWindow host)
    {
        if (!await tab.SettleAsync()) return;
        var dlg = new FormDialog(owner, "Export Pages as Images", "Export");
        var range = dlg.AddRange(tab, preferSelection: false);
        var format = dlg.AddChoice("Format", ["PNG (lossless)", "JPEG (smaller)"]);
        var dpi = dlg.AddChoice("Resolution", ["72 dpi", "96 dpi", "150 dpi", "200 dpi", "300 dpi", "600 dpi"], 2);
        var folder = dlg.AddText("Save to folder", Path.GetDirectoryName(tab.Session.FilePath) ?? AppSettings.Current.LastFolder);
        var browse = new Button { Content = "Browse…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        browse.Click += (_, _) => { if (PdfFiles.PickFolder(dlg, "Export images to", folder.Text) is { } f) folder.Text = f; };
        dlg.Add(null, browse);
        int[] pages = [];
        dlg.Validate(() =>
        {
            if (!range.TryGet(out pages, out var err)) return err;
            return Directory.Exists(folder.Text) ? null : "Choose an existing folder.";
        });
        if (!dlg.ShowModal() || !await tab.SettleAsync()) return;
        pages = [.. pages.Where(p => p < tab.PageView.PageCount)];
        if (pages.Length == 0) return;

        var jpeg = format.SelectedIndex == 1;
        var res = new[] { 72, 96, 150, 200, 300, 600 }[dpi.SelectedIndex];
        var dir = folder.Text;
        var name = Path.GetFileNameWithoutExtension(tab.Session.DisplayName);
        var engine = tab.ViewEngine; // what is shown (chosen layers included)

        // Clamp the resolution of very large pages so one image can't exhaust memory.
        var dpiFor = pages.Distinct().ToDictionary(p => p, p => ClampDpi(engine.GetPage(p), res));
        var clamped = dpiFor.Values.Where(d => d < res).ToList();
        // Busy from here (also while asking about large pages), so nothing replaces the engine this export reads.
        using var busy = tab.BeginBusy($"Exporting {Pages(pages.Length)}…");
        if (clamped.Count > 0)
        {
            var lowest = clamped.Min();
            var many = clamped.Count > 1;
            if (!Prompt.Confirm(owner, "Some pages are very large",
                    $"{(many ? $"{clamped.Count} pages are" : "1 page is")} too large to export at {res} dpi, so {(many ? "they are" : "it is")} exported at a lower resolution (down to {lowest:0} dpi) to stay under {MaxExportPixels / 1e6:0} megapixels per image.",
                    "Export")) return;
            Log.Write($"Export: {clamped.Count} page(s) clamped below {res} dpi (lowest {lowest:0})");
        }

        var ext = jpeg ? "jpg" : "png";
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<(int Page, string Path)>();
        try
        {
            foreach (var p in pages) targets.Add((p, PdfFiles.UniquePath(dir, $"{name} - page {p + 1}.{ext}", reserved)));
        }
        catch (IOException ex)
        {
            Prompt.Error(owner, "Can't export to that folder", ex.Message);
            return;
        }

        var done = 0;
        using var progress = new ProgressPrompt(owner, "Exporting images", $"Exporting {Pages(pages.Length)}…");
        var cancel = progress.Token;
        try
        {
            await Task.Run(() =>
            {
                foreach (var (p, path) in targets)
                {
                    cancel.ThrowIfCancellationRequested();
                    progress.Report($"Exporting page {p + 1} ({done + 1} of {targets.Count})…", (double)done / targets.Count);
                    try
                    {
                        engine.SaveImage(p, dpiFor[p], path, jpeg, cancel);
                    }
                    catch (OperationCanceledException)
                    {
                        try { File.Delete(path); } catch (Exception) { } // don't leave half an image behind
                        throw;
                    }
                    Interlocked.Increment(ref done);
                }
            }, cancel);
            host.ShowStatus($"Exported {done} image(s) to {dir}");
            Shell.Open(dir);
        }
        catch (OperationCanceledException)
        {
            Log.Write($"Export cancelled after {done} of {targets.Count} image(s)");
            host.ShowStatus($"Export cancelled. {done} image(s) were saved to {dir}");
        }
        catch (Exception ex)
        {
            Log.Write($"Export failed after {done} image(s): {ex}");
            Prompt.Error(owner, $"Export stopped after {done} page(s)", ex.Message);
        }
    }

    /// <summary>Largest image Export writes, in pixels: A0 at 600 dpi would be ~560 MP (about 1.6 GB while rendering).</summary>
    private const double MaxExportPixels = 64_000_000;

    /// <summary>The requested dpi, lowered if the page would exceed <see cref="MaxExportPixels"/> at that resolution.</summary>
    private static double ClampDpi(PageGeometry g, double dpi)
    {
        var squareInches = Math.Max(1e-6, g.Width / 72 * (g.Height / 72));
        return Math.Min(dpi, Math.Floor(Math.Sqrt(MaxExportPixels / squareInches)));
    }

    public static async Task ShowProperties(Window owner, DocumentTab tab)
    {
        var s = tab.Session;
        var page = Organize.CurrentPage(tab);
        var g = s.Engine.GetPage(page);
        PdfMetadata? meta = null;
        if (s.IsPdf)
        {
            try
            {
                meta = PageEditor.ReadMetadata(s.Data, s.Password);
            }
            catch (Exception ex) when (ex is InvalidOperationException or PdfSharp.PdfSharpException)
            {
                Log.Write($"Metadata unreadable: {ex.Message}");
            }
        }

        var dlg = new FormDialog(owner, "Document Properties", s.IsPdf ? "Save Changes" : "Close", 520);
        var info = new Grid();
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        info.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var r = info.RowDefinitions.Count;
            info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Margin = new Thickness(0, 2, 8, 2), Style = (Style)Application.Current.FindResource("Caption") };
            var v = new TextBox { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, r);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 1);
            info.Children.Add(l);
            info.Children.Add(v);
        }
        Row("File", s.DisplayName);
        Row("Location", s.FilePath is null ? "Not saved yet" : Path.GetDirectoryName(s.FilePath));
        Row("File size", FormatSize(s.Data.Length));
        Row("Pages", s.Engine.PageCount.ToString());
        Row("Page size", $"{Organize.Inches(g.Width)} × {Organize.Inches(g.Height)} in  ({g.Width * 25.4 / 72:0} × {g.Height * 25.4 / 72:0} mm)  — page {page + 1}");
        Row("PDF version", meta?.Version);
        Row("Security", SecurityActions.Describe(s));
        Row("Created by", meta?.Creator);
        Row("Producer", meta?.Producer);
        Row("Created", meta?.Created?.ToString("g"));
        Row("Modified", meta?.Modified?.ToString("g"));
        dlg.Add("Document", info);

        TextBox? title = null, author = null, subject = null, keywords = null;
        if (meta is not null)
        {
            title = dlg.AddText("Title", meta.Title);
            author = dlg.AddText("Author", meta.Author);
            subject = dlg.AddText("Subject", meta.Subject);
            keywords = dlg.AddText("Keywords", meta.Keywords);
        }
        if (!dlg.ShowModal() || meta is null) return;

        if (title!.Text == meta.Title && author!.Text == meta.Author && subject!.Text == meta.Subject && keywords!.Text == meta.Keywords) return;
        var (t, a, sub, k) = (title.Text, author!.Text, subject!.Text, keywords!.Text);
        await tab.RunEdit("Edit document properties", (d, pw) => PageEditor.WriteMetadata(d, pw, t, a, sub, k));
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.##} MB",
    };
}
