using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using InkvoltPdf.Core;

namespace InkvoltPdf.App;

/// <summary>
/// File commands that make or reshape a whole document: New from Images / Clipboard (and image files dropped on the
/// window or the thumbnails), Reduce File Size, Flatten.
/// </summary>
public static class FileTools
{
    /// <summary>Image types <see cref="ImageImport"/> reads.</summary>
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff"];

    public const string ImageFilter = "Images (*.jpg, *.png, *.bmp, *.gif, *.tif)|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*";

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Image files in a file drop (none when anything else is dropped along with them).</summary>
    public static string[] DroppedImages(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && files.All(f => IsImage(f) && File.Exists(f)) ? files : [];

    /// <summary>The page size chosen last (this session).</summary>
    private static int _imagePageSize;

    private static readonly string[] PageSizeNames = ["Fit each image (page the size of the image)", "Letter (8.5 × 11 in)", "A4 (210 × 297 mm)"];

    // ───────────────────────────── New from images ─────────────────────────────

    public static async Task NewFromImages(MainWindow host)
    {
        string[] files;
        if (DevScript.TakeSavePath(out var scripted)) files = scripted is null ? [] : scripted.Split(';');
        else
        {
            var dlg = new OpenFileDialog { Title = "New PDF from Images", Filter = ImageFilter, Multiselect = true, InitialDirectory = AppSettings.Current.LastFolder };
            if (dlg.ShowDialog(host) != true) return;
            files = dlg.FileNames;
        }
        if (files.Length == 0) return;
        if (AskPageSize(host, files.Length) is not { } size) return;
        await CreateFromFiles(host, files, size);
    }

    /// <summary>Asks how big the pages should be. Null when cancelled.</summary>
    private static PageSizeOption? AskPageSize(Window owner, int count)
    {
        var dlg = new FormDialog(owner, "New PDF from Images", "Create");
        dlg.AddNote(count == 1 ? "One image becomes one page." : $"{count} images become {count} pages, in this order (a multi-page TIFF adds a page per frame).");
        var size = dlg.AddChoice("Page size", PageSizeNames, _imagePageSize);
        if (!dlg.ShowModal()) return null;
        _imagePageSize = size.SelectedIndex;
        return (PageSizeOption)size.SelectedIndex;
    }

    private static async Task CreateFromFiles(MainWindow host, IReadOnlyList<string> files, PageSizeOption size)
    {
        host.ShowStatus(files.Count == 1 ? $"Converting {Path.GetFileName(files[0])}…" : $"Converting {files.Count} images…");
        try
        {
            var bytes = await Task.Run(() => ImageImport.ImagesToPdf([.. files.Select(f => (File.ReadAllBytes(f), Path.GetFileName(f)))], size));
            host.OpenSession(DocumentSession.FromBytes(bytes, "Untitled.pdf"));
            host.ShowStatus("New PDF made from " + (files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} images") + ". Save to keep it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Write($"Images to PDF failed: {ex}");
            Prompt.Error(host, "Couldn't make a PDF from the images", ex.Message);
        }
    }

    // ───────────────────────────── New from clipboard ─────────────────────────────

    public static async Task NewFromClipboard(MainWindow host)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(File.Exists).ToList();
                var images = files.Where(IsImage).ToList();
                if (images.Count > 0)
                {
                    await CreateFromFiles(host, images, (PageSizeOption)_imagePageSize);
                    return;
                }
                var pdfs = files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
                if (pdfs.Count > 0)
                {
                    host.OpenFiles(pdfs);
                    return;
                }
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                var frozen = (BitmapSource)image.GetAsFrozen(); // frozen here: the encoding runs on a worker thread
                var bytes = await Task.Run(() => ImageImport.ImagesToPdf([(EncodePng(frozen), "Clipboard.png")], PageSizeOption.FitImage));
                host.OpenSession(DocumentSession.FromBytes(bytes, "Untitled.pdf"));
                host.ShowStatus("New PDF made from the clipboard image. Save to keep it.");
                return;
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException or IOException)
        {
            Log.Write($"New from clipboard failed: {ex}");
            Prompt.Error(host, "Couldn't read the clipboard", ex.Message);
            return;
        }
        Prompt.Info(host, "Nothing to make a PDF from", "Copy an image (for example a screenshot) or image files first, then try again.");
    }

    /// <param name="frozen">A frozen bitmap (usable from any thread).</param>
    private static byte[] EncodePng(BitmapSource frozen)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frozen));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ───────────────────────────── Dropped images ─────────────────────────────

    /// <summary>
    /// Image files dropped on the window (not on the thumbnails): asks whether to make a new PDF of them, add them as
    /// pages of the open document, or view each one. True when the drop was handled here (the caller opens nothing).
    /// </summary>
    public static bool HandleWindowDrop(MainWindow host, string[] files)
    {
        if (files.Length == 0 || !files.All(f => IsImage(f) && File.Exists(f))) return false;
        var tab = host.ActiveTab;
        var canInsert = tab is { IsDisposed: false, IsBusy: false } && tab.Session.IsPdf && tab.PageView.PageCount > 0;
        var dlg = new FormDialog(host, files.Length == 1 ? $"Open {Path.GetFileName(files[0])}" : $"Open {files.Length} images", "OK", 480);
        List<string> choices = [files.Length == 1 ? "Make a new PDF of it" : "Combine them into a new PDF (a page per image)"];
        if (canInsert) choices.Add($"Add as pages at the end of {tab!.Session.DisplayName}");
        choices.Add(files.Length == 1 ? "Just view the image" : "View each image in its own tab");
        var what = dlg.AddChoice("What should Inkvolt PDF do?", choices);
        var size = dlg.AddChoice("Page size", PageSizeNames, _imagePageSize);
        what.SelectionChanged += (_, _) => size.IsEnabled = what.SelectedIndex < choices.Count - 1;
        if (!dlg.ShowModal()) return true;
        _imagePageSize = size.SelectedIndex;
        var pageSize = (PageSizeOption)size.SelectedIndex;
        if (what.SelectedIndex == choices.Count - 1) return false; // view them, as any other file
        if (what.SelectedIndex == 1 && canInsert) _ = InsertImages(tab!, files, tab!.PageView.PageCount, pageSize);
        else _ = CreateFromFiles(host, files, pageSize);
        return true;
    }

    /// <summary>Converts images to pages and inserts them before page <paramref name="before"/> (one undoable edit).</summary>
    public static async Task InsertImages(DocumentTab tab, IReadOnlyList<string> files, int before, PageSizeOption? size = null)
    {
        var pageSize = size ?? (PageSizeOption)_imagePageSize;
        var label = files.Count == 1 ? $"Insert {Path.GetFileName(files[0])}" : $"Insert {files.Count} images";
        await tab.RunEdit(label, (d, pw) =>
        {
            var pdf = ImageImport.ImagesToPdf([.. files.Select(f => (File.ReadAllBytes(f), Path.GetFileName(f)))], pageSize);
            return PageEditor.Insert(d, pw, new PdfSource(pdf, null, label), null, before);
        }, before);
    }

    // ───────────────────────────── Reduce file size ─────────────────────────────

    private static readonly (string Name, double? Dpi, int Quality)[] Qualities =
    [
        ("High: images up to 200 dpi", 200, 85),
        ("Medium: images up to 150 dpi (good for screens)", 150, 75),
        ("Low: images up to 96 dpi (smallest)", 96, 60),
        ("Keep images as they are", null, 75),
    ];

    public static async Task ReduceSize(Window owner, DocumentTab tab)
    {
        if (!tab.Session.IsPdf || !await tab.SettleAsync()) return;
        var dlg = new FormDialog(owner, "Reduce File Size", "Reduce", 480);
        dlg.AddNote($"Now {FileActions.FormatSize(tab.Session.Data.Length)}. Pages look the same; large images are resampled and stored as JPEG.");
        var quality = dlg.AddChoice("Image quality", Qualities.Select(q => q.Name), 1);
        dlg.Add("Also", new System.Windows.Controls.StackPanel());
        var thumbs = dlg.AddCheck("Remove embedded page thumbnails", true);
        var pieces = dlg.AddCheck("Remove other apps' private editing data", true);
        var dedupe = dlg.AddCheck("Store identical images and fonts once", true);
        var metadata = dlg.AddCheck("Remove XMP metadata (title, author and subject stay)");
        if (!dlg.ShowModal() || !await tab.SettleAsync()) return;

        var q = Qualities[quality.SelectedIndex];
        var options = new OptimizeOptions
        {
            MaxImageDpi = q.Dpi,
            JpegQuality = q.Quality,
            RemoveThumbnails = thumbs.IsChecked == true,
            RemovePieceInfo = pieces.IsChecked == true,
            DeduplicateStreams = dedupe.IsChecked == true,
            RemoveMetadata = metadata.IsChecked == true,
        };
        OptimizeResult? result = null;
        using var taskbar = new TaskbarProgress(Window.GetWindow(tab));
        var outcome = await tab.TryEdit("Reduce file size", (d, pw) =>
        {
            var r = Optimizer.Optimize(d, pw, options);
            result = r;
            // Savings under 1% (or a bigger file) aren't worth a rewrite: keep the original.
            return IsWorthIt(r) ? r.Data : d;
        }, quiet: true);
        if (result is not { } res) return;
        if (outcome == EditOutcome.NoChange)
        {
            tab.ShowNotice(res.Saved <= 0
                ? $"This file is already compact ({FileActions.FormatSize(res.SizeBefore)}): nothing was changed."
                : $"Only {FileActions.FormatSize(res.Saved)} could be saved, so the file was left as it is.");
            return;
        }
        if (outcome == EditOutcome.Done)
            tab.ShowNotice($"Reduced from {FileActions.FormatSize(res.SizeBefore)} to {FileActions.FormatSize(res.SizeAfter)} " +
                           $"({100.0 * res.Saved / Math.Max(1, res.SizeBefore):0}% smaller). Save to keep it.", undo: true);
    }

    /// <summary>True when the optimized file is at least 1% (and 1 KB) smaller.</summary>
    public static bool IsWorthIt(OptimizeResult r) => r.Saved >= Math.Max(1024, r.SizeBefore / 100);

    // ───────────────────────────── Flatten ─────────────────────────────

    public static async Task Flatten(Window owner, DocumentTab tab)
    {
        if (!tab.Session.IsPdf || !await tab.SettleAsync()) return;
        var dlg = new FormDialog(owner, "Flatten", "Flatten", 460);
        dlg.AddNote("Draws comments and filled-in fields into the pages as they look now. They can't be edited, replied to or filled in afterwards. Undo brings them back until you close the document.");
        var comments = dlg.AddCheck("Comments, markup and stamps", true);
        var fields = dlg.AddCheck("Form fields (signature fields stay)", true);
        var printable = dlg.AddCheck("Only what prints (drop the rest)");
        dlg.Validate(() => comments.IsChecked != true && fields.IsChecked != true ? "Choose what to flatten." : null);
        if (!dlg.ShowModal()) return;
        var options = new FlattenOptions
        {
            Annotations = comments.IsChecked == true,
            FormFields = fields.IsChecked == true,
            PrintableOnly = printable.IsChecked == true,
        };
        var what = options.Annotations && options.FormFields ? "comments and fields" : options.Annotations ? "comments" : "form fields";
        await tab.RunEdit($"Flatten {what}", (d, pw) => Flattener.Flatten(d, pw, options));
    }
}
