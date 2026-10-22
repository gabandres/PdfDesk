using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfDesk.App.Viewer;
using PdfDesk.Core;

namespace PdfDesk.App;

/// <summary>Print, export to images, and document properties.</summary>
public static class FileActions
{
    public static async Task Print(Window owner, DocumentTab tab)
    {
        var count = tab.PageView.PageCount;
        var selection = tab.SelectedPages;
        var dlg = new PrintDialog
        {
            UserPageRangeEnabled = true,
            CurrentPageEnabled = true,
            SelectedPagesEnabled = selection.Length > 1,
            MinPage = 1,
            MaxPage = (uint)count,
            PageRange = new System.Windows.Controls.PageRange(1, count),
        };
        if (dlg.ShowDialog() != true) return;

        int[] pages = dlg.PageRangeSelection switch
        {
            PageRangeSelection.UserPages => [.. Enumerable.Range(Math.Max(1, dlg.PageRange.PageFrom) - 1,
                Math.Max(0, Math.Min(count, dlg.PageRange.PageTo) - Math.Max(1, dlg.PageRange.PageFrom) + 1))],
            PageRangeSelection.CurrentPage => [tab.PageView.CurrentPage],
            PageRangeSelection.SelectedPages => selection,
            _ => [.. Enumerable.Range(0, count)],
        };
        if (pages.Length == 0) return;

        var (origin, area) = ImageableArea(dlg);
        var paginator = new PdfPaginator(tab.Session.Engine, pages, new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight), origin, area);
        tab.SetBusy($"Printing {pages.Length} page(s)…");
        try
        {
            // Pages render on a background thread (PdfPaginator.GetPageAsync), so the window stays responsive.
            var writer = PrintQueue.CreateXpsDocumentWriter(dlg.PrintQueue);
            await WriteAsync(writer, paginator, dlg.PrintTicket);
            Log.Write($"Printed {pages.Length} page(s) of {tab.Session.DisplayName} to {dlg.PrintQueue.FullName}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write($"Print failed: {ex}");
            MessageBox.Show(owner, $"Printing failed.{Environment.NewLine}{Environment.NewLine}{ex.Message}", "Print", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            tab.SetBusy(null);
        }
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
        if (ticket is null) writer.WriteAsync(paginator);
        else writer.WriteAsync(paginator, ticket);
        return done.Task;
    }

    /// <summary>Dev check: runs the print pipeline into an XPS file (Letter paper, quarter-inch margins).</summary>
    internal static async Task<int> WriteXps(PdfEngine engine, int[] pages, string path)
    {
        var paper = new Size(816, 1056);
        var paginator = new PdfPaginator(engine, pages, paper, new Point(24, 24), new Size(768, 1008));
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
    private sealed class PdfPaginator(PdfEngine engine, int[] pages, Size paper, Point origin, Size area) : DocumentPaginator
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
            Task.Run(() => Place(pageNumber)).ContinueWith(t => OnGetPageCompleted(t.IsFaulted
                    ? new GetPageCompletedEventArgs(null, pageNumber, t.Exception!.InnerException, false, userState)
                    : new GetPageCompletedEventArgs(Build(t.Result), pageNumber, null, false, userState)),
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        private Placed Place(int pageNumber)
        {
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
            var bmp = PageViewer.ToBitmap(engine.Render(index, pxScale));

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
        if (!dlg.ShowModal()) return;

        var jpeg = format.SelectedIndex == 1;
        var res = new[] { 72, 96, 150, 200, 300, 600 }[dpi.SelectedIndex];
        var dir = folder.Text;
        var name = Path.GetFileNameWithoutExtension(tab.Session.DisplayName);
        var engine = tab.Session.Engine;
        var done = 0;
        tab.SetBusy($"Exporting {pages.Length} page(s)…");
        try
        {
            await Task.Run(() =>
            {
                foreach (var p in pages)
                {
                    engine.SaveImage(p, res, PdfFiles.UniquePath(dir, $"{name} - page {p + 1}.{(jpeg ? "jpg" : "png")}"), jpeg);
                    Interlocked.Increment(ref done);
                }
            });
            host.ShowStatus($"Exported {done} image(s) to {dir}");
            Shell.Open(dir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Export stopped after {done} page(s).\n\n{ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            tab.SetBusy(null);
        }
    }

    public static async Task ShowProperties(Window owner, DocumentTab tab)
    {
        var s = tab.Session;
        var g = s.Engine.GetPage(tab.PageView.CurrentPage);
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
        Row("Page size", $"{Organize.Inches(g.Width)} × {Organize.Inches(g.Height)} in  ({g.Width * 25.4 / 72:0} × {g.Height * 25.4 / 72:0} mm)  — page {tab.PageView.CurrentPage + 1}");
        Row("PDF version", meta?.Version);
        Row("Encrypted", s.Engine.IsEncrypted ? "Yes" : null);
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

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.##} MB",
    };
}
