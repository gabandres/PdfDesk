using System.IO;
using System.Windows;
using System.Windows.Controls;
using InkvoltPdf.Core;
using PageRange = InkvoltPdf.Core.PageRange;

namespace InkvoltPdf.App;

/// <summary>The Organize menu: each command gathers options in a dialog, then runs one undoable page edit.</summary>
public static class Organize
{
    private static readonly (string Name, double W, double H)[] PaperSizes =
    [
        ("Letter (8.5 × 11 in)", 612, 792),
        ("Legal (8.5 × 14 in)", 612, 1008),
        ("Tabloid (11 × 17 in)", 792, 1224),
        ("A3 (297 × 420 mm)", 842, 1191),
        ("A4 (210 × 297 mm)", 595, 842),
        ("A5 (148 × 210 mm)", 420, 595),
    ];

    private static string Pages(int n) => n == 1 ? "1 page" : $"{n} pages";

    /// <summary>
    /// The page "current page" means for a command: the focused card while organizing (the reader's page is hidden then),
    /// else the reader's page.
    /// </summary>
    public static int CurrentPage(DocumentTab tab)
    {
        var count = tab.PageView.PageCount;
        var page = tab.IsOrganizing && tab.Organizer.FocusedPage is >= 0 and var focused ? focused : tab.PageView.CurrentPage;
        return count == 0 ? 0 : Math.Clamp(page, 0, count - 1);
    }

    /// <summary>For CanExecute of the selection commands (rotate, delete, duplicate): false in Organize with no card selected.</summary>
    public static bool HasSelection(DocumentTab tab) => tab.SelectedPages.Length > 0;

    public static Task RotateSelected(DocumentTab tab, int degrees)
    {
        var pages = tab.SelectedPages;
        if (pages.Length == 0) return Task.CompletedTask;
        return Reselect(tab, pages, tab.RunEdit($"Rotate {Pages(pages.Length)}", (d, pw) => PageEditor.Rotate(d, pw, pages, degrees)));
    }

    public static async Task RotateDialog(Window owner, DocumentTab tab)
    {
        var dlg = new FormDialog(owner, "Rotate Pages", "Rotate");
        var dir = dlg.AddChoice("Direction", ["90° clockwise", "90° counterclockwise", "180°"]);
        var range = dlg.AddRange(tab);
        var orient = dlg.AddChoice("Only these pages", ["Portrait and landscape", "Portrait pages only", "Landscape pages only"]);
        int[] pages = [];
        dlg.Validate(() =>
        {
            if (!range.TryGet(out pages, out var err)) return err;
            var engine = tab.Session.Engine;
            pages = orient.SelectedIndex switch
            {
                1 => [.. pages.Where(p => engine.GetPage(p).Height >= engine.GetPage(p).Width)],
                2 => [.. pages.Where(p => engine.GetPage(p).Width > engine.GetPage(p).Height)],
                _ => pages,
            };
            return pages.Length == 0 ? "No pages in the range have that orientation." : null;
        });
        if (!dlg.ShowModal()) return;
        var degrees = dir.SelectedIndex switch { 0 => 90, 1 => -90, _ => 180 };
        await Reselect(tab, pages, tab.RunEdit($"Rotate {Pages(pages.Length)}", (d, pw) => PageEditor.Rotate(d, pw, pages, degrees)));
    }

    public static Task DeleteSelected(DocumentTab tab)
    {
        var pages = tab.SelectedPages;
        return Delete(tab, pages);
    }

    public static async Task DeleteDialog(Window owner, DocumentTab tab)
    {
        var dlg = new FormDialog(owner, "Delete Pages", "Delete");
        var range = dlg.AddRange(tab);
        int[] pages = [];
        dlg.Validate(() =>
        {
            if (!range.TryGet(out pages, out var err)) return err;
            return pages.Length >= tab.PageView.PageCount ? "You can't delete every page of a document." : null;
        });
        if (dlg.ShowModal()) await Delete(tab, pages);
    }

    private static Task Delete(DocumentTab tab, int[] pages)
    {
        if (pages.Length == 0) return Task.CompletedTask;
        if (pages.Length >= tab.PageView.PageCount)
        {
            Prompt.Info(Window.GetWindow(tab), "A PDF needs at least one page", "Leave at least one page. To get rid of the whole file, delete it in File Explorer.");
            return Task.CompletedTask;
        }
        return tab.RunEdit($"Delete {Pages(pages.Length)}", (d, pw) => PageEditor.Delete(d, pw, pages), pages.Min());
    }

    public static async Task InsertFromFile(Window owner, DocumentTab tab)
    {
        var path = PdfFiles.PickPdf(owner, "Insert pages from…");
        if (path is null || PdfFiles.LoadForImport(owner, path) is not { } source) return;

        int srcCount;
        using (var e = PdfEngine.Open(source.Data, MuPDFCore.InputFileTypes.PDF, source.Password)) srcCount = e.PageCount;

        var dlg = new FormDialog(owner, "Insert Pages", "Insert");
        dlg.AddNote($"From: {Path.GetFileName(path)} ({Pages(srcCount)})");
        var srcRange = dlg.AddText("Pages to insert", $"1-{srcCount}");
        var pos = dlg.AddPosition(tab.PageView.PageCount, CurrentPage(tab));
        int[] srcPages = [];
        var before = 0;
        dlg.Validate(() =>
        {
            if (!PageRange.TryParse(srcRange.Text, srcCount, out srcPages, out var err)) return err;
            return pos.TryGet(out before, out err) ? null : err;
        });
        if (!dlg.ShowModal()) return;
        await tab.RunEdit($"Insert {Pages(srcPages.Length)} from {source.Name}", (d, pw) => PageEditor.Insert(d, pw, source, srcPages, before), before);
    }

    public static async Task InsertBlank(Window owner, DocumentTab tab)
    {
        var currentPage = CurrentPage(tab);
        var current = tab.Session.Engine.GetPage(currentPage);
        var dlg = new FormDialog(owner, "Insert Blank Pages", "Insert");
        var count = dlg.AddText("Number of pages", "1");
        var size = dlg.AddChoice("Page size", [$"Same as current page ({Inches(current.Width)} × {Inches(current.Height)} in)", .. PaperSizes.Select(p => p.Name)]);
        var orient = dlg.AddChoice("Orientation", ["Portrait", "Landscape"], current.Width > current.Height ? 1 : 0);
        var pos = dlg.AddPosition(tab.PageView.PageCount, currentPage);
        var n = 1;
        var before = 0;
        dlg.Validate(() =>
        {
            if (!int.TryParse(count.Text.Trim(), out n) || n < 1 || n > 1000) return "Enter a number of pages from 1 to 1000.";
            return pos.TryGet(out before, out var err) ? null : err;
        });
        if (!dlg.ShowModal()) return;

        var (w, h) = size.SelectedIndex == 0 ? (current.Width, current.Height) : (PaperSizes[size.SelectedIndex - 1].W, PaperSizes[size.SelectedIndex - 1].H);
        var landscape = orient.SelectedIndex == 1;
        if (landscape != w > h) (w, h) = (h, w);
        await tab.RunEdit($"Insert {Pages(n)}", (d, pw) => PageEditor.InsertBlank(d, pw, before, n, w, h), before);
    }

    public static async Task Extract(Window owner, DocumentTab tab, MainWindow host)
    {
        if (!await tab.SettleAsync()) return;
        var dlg = new FormDialog(owner, "Extract Pages", "Extract");
        var range = dlg.AddRange(tab);
        var mode = dlg.AddChoice("Extract to", ["A new document (opens in a tab)", "One PDF file…", "A separate PDF file per page…"]);
        var delete = dlg.AddCheck("Delete the pages from this document after extracting");
        int[] pages = [];
        dlg.Validate(() =>
        {
            if (!range.TryGet(out pages, out var err)) return err;
            return delete.IsChecked == true && pages.Length >= tab.PageView.PageCount ? "You can't delete every page of a document." : null;
        });
        if (!dlg.ShowModal() || !await tab.SettleAsync()) return;
        if (pages.Any(p => p >= tab.PageView.PageCount)) return; // the document changed meanwhile
        if (!ConfirmUnprotected(owner, tab, "The extracted pages are saved without its password protection.", "Extract anyway")) return;

        var name = Path.GetFileNameWithoutExtension(tab.Session.DisplayName);
        var data = tab.Session.Data;
        var pw = tab.Session.Password;
        var label = PageRange.Format(pages);
        // Held from here, also while the save dialogs are open, so no edit lands between reading the pages and writing them.
        var busy = tab.BeginBusy(null);
        try
        {
            switch (mode.SelectedIndex)
            {
                case 0:
                {
                    busy.Dispose();
                    busy = tab.BeginBusy("Extracting pages…");
                    var bytes = await Task.Run(() => PageEditor.Extract(data, pw, pages));
                    host.OpenSession(DocumentSession.FromBytes(bytes, $"{name} (pages {label}).pdf", pw));
                    break;
                }
                case 1:
                {
                    string? path;
                    while (true)
                    {
                        path = PdfFiles.PickSavePath(owner, $"{name} (pages {label}).pdf", Path.GetDirectoryName(tab.Session.FilePath));
                        if (path is null) return;
                        // Writing over a document open in a tab would swap the file under it (and the tab wouldn't know).
                        if (!PdfFiles.IsOpenInTab(path)) break;
                        Prompt.Info(owner, $"{Path.GetFileName(path)} is open in Inkvolt PDF",
                            "Choose a different name. To replace that document, extract to a new document instead, then use Save As.");
                    }
                    busy.Dispose();
                    busy = tab.BeginBusy("Extracting pages…");
                    var bytes = await Task.Run(() => PageEditor.Extract(data, pw, pages));
                    await Task.Run(() => AtomicFile.WriteAllBytes(path, bytes));
                    host.ShowStatus($"Saved {Path.GetFileName(path)}");
                    break;
                }
                default:
                {
                    var folder = PdfFiles.PickFolder(owner, "Save extracted pages to", Path.GetDirectoryName(tab.Session.FilePath));
                    if (folder is null) return;
                    var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var files = pages.Select(p => (PdfFiles.UniquePath(folder, $"{name} (page {p + 1}).pdf", reserved), (Func<byte[]>)(() => PageEditor.Extract(data, pw, [p])))).ToList();
                    busy.Dispose();
                    busy = tab.BeginBusy("Extracting pages…");
                    await Task.Run(() => PdfFiles.WriteBatch(files));
                    host.ShowStatus($"Saved {Pages(pages.Length)} to {folder}");
                    Shell.Open(folder);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Extract failed: {ex}");
            Prompt.Error(owner, "Extract didn't work", BatchFailure(ex));
            return;
        }
        finally
        {
            busy.Dispose();
        }

        if (delete.IsChecked == true)
            await tab.RunEdit($"Delete {Pages(pages.Length)}", (d, p2) => PageEditor.Delete(d, p2, pages), pages.Min());
    }

    public static async Task Split(Window owner, DocumentTab tab, MainWindow host)
    {
        if (!await tab.SettleAsync()) return;
        var count = tab.PageView.PageCount;
        var outline = tab.Session.Engine.GetOutline();
        var dlg = new FormDialog(owner, "Split Document", "Split", 480);
        var mode = dlg.AddChoice("Split by", ["Number of pages", "Page ranges", "Top-level bookmarks"]);
        var every = dlg.AddText("Pages per file", "1");
        var ranges = dlg.AddText("Page ranges (one file per range, separated by ;)", count > 1 ? $"1-{Math.Max(1, count / 2)}; {Math.Max(1, count / 2) + 1}-{count}" : "1");
        dlg.AddNote(outline.Count == 0 ? "This document has no bookmarks, so splitting by bookmarks isn't available." : $"{outline.Count} top-level bookmarks.");
        var name = Path.GetFileNameWithoutExtension(tab.Session.DisplayName);
        var folderBox = dlg.AddText("Save to folder", Path.GetDirectoryName(tab.Session.FilePath) ?? AppSettings.Current.LastFolder);
        var browse = new Button { Content = "Browse…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        browse.Click += (_, _) => { if (PdfFiles.PickFolder(dlg, "Save split files to", folderBox.Text) is { } f) folderBox.Text = f; };
        dlg.Add(null, browse);
        var pattern = dlg.AddText("File name ({name} = document name, {n} = part number)", "{name} (part {n})");

        List<(int[] Pages, string Label)> parts = [];
        List<string> targets = [];
        dlg.Validate(() =>
        {
            parts = [];
            switch (mode.SelectedIndex)
            {
                case 0:
                    if (!int.TryParse(every.Text.Trim(), out var n) || n < 1 || n > count) return $"Enter a page count from 1 to {count}.";
                    parts = [.. PageEditor.ChunksOf(count, n).Select(c => (c, ""))];
                    break;
                case 1:
                    foreach (var part in ranges.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!PageRange.TryParse(part, count, out var pages, out var err)) return err;
                        parts.Add((pages, ""));
                    }
                    break;
                default:
                    var starts = outline.Select(o => o.PageIndex).Where(p => p >= 0 && p < count).Append(0).Distinct().Order().ToList();
                    if (outline.Count == 0) return "This document has no bookmarks.";
                    for (var i = 0; i < starts.Count; i++)
                    {
                        var end = i + 1 < starts.Count ? starts[i + 1] : count;
                        var title = outline.FirstOrDefault(o => o.PageIndex == starts[i])?.Title ?? "";
                        parts.Add(([.. Enumerable.Range(starts[i], end - starts[i])], title));
                    }
                    break;
            }
            if (parts.Count == 0) return "Nothing to split.";
            if (!Directory.Exists(folderBox.Text)) return "Choose an existing folder.";
            if (!pattern.Text.Contains("{n}") && mode.SelectedIndex != 2) return "The file name must include {n} so each part gets its own file.";

            // Pick every file name before writing anything (long bookmark titles are shortened, duplicates numbered).
            targets = [];
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                for (var i = 0; i < parts.Count; i++)
                {
                    var file = pattern.Text.Replace("{name}", name).Replace("{n}", (i + 1).ToString());
                    var label = parts[i].Label.Trim();
                    if (label.Length > 0 && !pattern.Text.Contains("{n}")) file = $"{name} - {label}";
                    else if (label.Length > 0) file += $" - {label}";
                    targets.Add(PdfFiles.UniquePath(folderBox.Text, file + ".pdf", reserved));
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                return ex.Message;
            }
            return null;
        });
        if (!dlg.ShowModal() || !await tab.SettleAsync() || tab.PageView.PageCount != count) return;
        if (!ConfirmUnprotected(owner, tab, "The split files are saved without its password protection.", "Split anyway")) return;

        var data = tab.Session.Data;
        var pw = tab.Session.Password;
        var folder = folderBox.Text;
        var files = parts.Select((p, i) => (targets[i], (Func<byte[]>)(() => PageEditor.Extract(data, pw, p.Pages)))).ToList();
        using var busy = tab.BeginBusy($"Splitting into {parts.Count} files…");
        try
        {
            await Task.Run(() => PdfFiles.WriteBatch(files));
            host.ShowStatus($"Split into {parts.Count} files in {folder}");
            Shell.Open(folder);
        }
        catch (Exception ex)
        {
            Log.Write($"Split failed: {ex}");
            Prompt.Error(owner, "Split didn't work", BatchFailure(ex));
        }
    }

    /// <summary>The error message, plus which files a partly finished batch did write.</summary>
    private static string BatchFailure(Exception ex)
    {
        if (ex is not BatchWriteException batch) return ex.Message;
        if (batch.Written.Count == 0) return $"{batch.Message} No files were written.";
        var names = batch.Written.Take(5).Select(Path.GetFileName);
        var more = batch.Written.Count > 5 ? $" and {batch.Written.Count - 5} more" : "";
        return $"{batch.Message} These files were written before it stopped: {string.Join(", ", names)}{more}.";
    }

    // Tabs already warned that extracting/splitting drops the password (once per tab, like page edits).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocumentTab, object> ProtectionWarned = new();

    /// <summary>Warns once per tab that output from a password-protected PDF is written without the password.</summary>
    private static bool ConfirmUnprotected(Window owner, DocumentTab tab, string body, string confirm)
    {
        if (!tab.Session.Engine.IsEncrypted || ProtectionWarned.TryGetValue(tab, out _)) return true;
        if (!Prompt.Confirm(owner, "This PDF is password-protected", body, confirm, warning: true)) return false;
        ProtectionWarned.AddOrUpdate(tab, true);
        return true;
    }

    public static async Task Move(Window owner, DocumentTab tab)
    {
        var dlg = new FormDialog(owner, "Move Pages", "Move");
        var range = dlg.AddRange(tab);
        var pos = dlg.AddPosition(tab.PageView.PageCount, tab.PageView.PageCount - 1, "Move to");
        int[] pages = [];
        var before = 0;
        dlg.Validate(() =>
        {
            if (!range.TryGet(out pages, out var err)) return err;
            return pos.TryGet(out before, out err) ? null : err;
        });
        if (!dlg.ShowModal()) return;
        var order = PageEditor.MoveOrder(tab.PageView.PageCount, pages, before);
        if (order.SequenceEqual(Enumerable.Range(0, order.Length))) return;
        var first = Array.IndexOf(order, pages[0]);
        await Reselect(tab, [.. Enumerable.Range(first, pages.Length)],
            tab.RunEdit($"Move {Pages(pages.Length)}", (d, pw) => PageEditor.Reorder(d, pw, order), first));
    }

    public static Task Duplicate(DocumentTab tab)
    {
        var pages = tab.SelectedPages;
        if (pages.Length == 0) return Task.CompletedTask;
        // The copy of the last selected page lands after every earlier copy.
        return tab.RunEdit($"Duplicate {Pages(pages.Length)}", (d, pw) => PageEditor.Duplicate(d, pw, pages), pages[^1] + pages.Length);
    }

    public static Task Reverse(DocumentTab tab)
    {
        var count = tab.PageView.PageCount;
        // The same pages stay selected (at their new, mirrored positions), in the organizer and the thumbnails.
        var selected = tab.SelectedPages;
        return Reselect(tab, [.. selected.Select(p => count - 1 - p).Order()],
            tab.RunEdit("Reverse page order", (d, pw) => PageEditor.Reverse(d, pw, count)));
    }

    public static async Task Crop(Window owner, DocumentTab tab)
    {
        var dlg = new FormDialog(owner, "Crop Pages", "Apply", 460);
        dlg.AddNote("Trims the visible area of each page. Nothing is deleted; Remove crop restores the full page.");
        var units = dlg.AddChoice("Units", ["Inches", "Millimeters", "Points"]);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        TextBox Cell(string label, int row, int col)
        {
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(col == 0 ? 0 : 16, 4, 8, 4) };
            var box = new TextBox { Text = "0", Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, col + 1);
            grid.Children.Add(tb);
            grid.Children.Add(box);
            return box;
        }
        var top = Cell("Top", 0, 0);
        var bottom = Cell("Bottom", 0, 2);
        var left = Cell("Left", 1, 0);
        var right = Cell("Right", 1, 2);
        dlg.Add("Margins to remove", grid);
        var remove = dlg.AddCheck("Remove crop instead (show the full page again)");
        var range = dlg.AddRange(tab);

        int[] pages = [];
        Margins m = default;
        dlg.Validate(() =>
        {
            if (!range.TryGet(out pages, out var err)) return err;
            if (remove.IsChecked == true) return null;
            var factor = units.SelectedIndex switch { 0 => 72.0, 1 => 72 / 25.4, _ => 1.0 };
            double[] v = new double[4];
            var boxes = new[] { left, top, right, bottom };
            for (var i = 0; i < 4; i++)
                if (!FormDialog.TryNumber(boxes[i].Text, 0, 100_000, out v[i])) return "Margins must be numbers of 0 or more.";
            m = new Margins(v[0] * factor, v[1] * factor, v[2] * factor, v[3] * factor);
            return m == default ? "Enter at least one margin." : null;
        });
        if (!dlg.ShowModal()) return;

        if (remove.IsChecked == true)
            await Reselect(tab, pages, tab.RunEdit($"Remove crop from {Pages(pages.Length)}", (d, pw) => PageEditor.Uncrop(d, pw, pages)));
        else
            await Reselect(tab, pages, tab.RunEdit($"Crop {Pages(pages.Length)}", (d, pw) => PageEditor.Crop(d, pw, pages, m)));
    }

    public static async Task Combine(Window owner, MainWindow host, IEnumerable<string>? initial = null)
    {
        var dlg = new CombineDialog(owner, initial ?? []);
        if (dlg.ShowDialog() != true) return;

        var sources = new List<PdfSource>();
        foreach (var f in dlg.Files)
        {
            if (PdfFiles.LoadForImport(owner, f) is not { } s) return;
            sources.Add(s);
        }
        var bookmarks = dlg.BookmarkPerFile;
        host.ShowStatus($"Combining {sources.Count} files…");
        using var taskbar = new TaskbarProgress(host);
        try
        {
            var bytes = await Task.Run(() => PageEditor.Merge(sources, bookmarks));
            host.OpenSession(DocumentSession.FromBytes(bytes, "Combined.pdf"));
            host.ShowStatus($"Combined {sources.Count} files. Save to keep the result.");
        }
        catch (Exception ex)
        {
            Prompt.Error(owner, "Combine didn't work", ex.Message);
        }
    }

    /// <summary>After an edit that keeps page positions, re-select the edited pages in the thumbnails.</summary>
    private static async Task Reselect(DocumentTab tab, int[] pages, Task<bool> edit)
    {
        if (await edit) tab.SelectPages(pages);
    }

    internal static string Inches(double pt) => (pt / 72).ToString("0.##");
}
