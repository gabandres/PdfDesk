using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using InkvoltPdf.Core;
using PageRange = InkvoltPdf.Core.PageRange;

namespace InkvoltPdf.App;

/// <summary>A small modal form: labeled rows, OK/Cancel, and a validation hook that keeps the dialog open on error.</summary>
public sealed class FormDialog : Window
{
    private readonly StackPanel _rows = new();
    private readonly Border _error = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _errorText = new() { TextWrapping = TextWrapping.Wrap };
    private Func<string?>? _validate;

    public FormDialog(Window owner, string title, string okText = "OK", double width = 440)
    {
        Owner = owner;
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 104, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "AccentButtonStyle");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 104 };
        ok.Click += (_, _) =>
        {
            var err = _validate?.Invoke();
            if (err is null)
            {
                DialogResult = true;
                return;
            }
            _errorText.Text = err;
            _error.Visibility = Visibility.Visible;
        };

        var heading = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) };
        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        body.Children.Add(heading);
        body.Children.Add(_rows);
        body.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 16, 24, 16) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var footer = new Border { Child = buttons, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");

        // Validation errors appear inline, like an InfoBar, instead of a separate message box.
        _error.Child = _errorText;
        _error.SetResourceReference(Border.BackgroundProperty, "SystemFillColorCriticalBackgroundBrush");
        _errorText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;
    }

    public T Add<T>(string? label, T control, double top = 12) where T : UIElement
    {
        if (label is not null)
            _rows.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, _rows.Children.Count == 0 ? 0 : top, 0, 4), FontWeight = FontWeights.SemiBold });
        else if (control is FrameworkElement fe && _rows.Children.Count > 0 && fe.Margin == default)
            fe.Margin = new Thickness(0, 6, 0, 0);
        _rows.Children.Add(control);
        return control;
    }

    public TextBlock AddNote(string text) =>
        Add(null, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.FindResource("Caption") });

    public TextBox AddText(string label, string value = "")
    {
        var box = Add(label, new TextBox { Text = value });
        if (_rows.Children.OfType<TextBox>().Count() == 1) Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return box;
    }

    public CheckBox AddCheck(string text, bool value = false) => Add(null, new CheckBox { Content = text, IsChecked = value });

    public ComboBox AddChoice(string label, IEnumerable<string> items, int selected = 0)
    {
        var combo = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = selected };
        return Add(label, combo);
    }

    public PageRangePicker AddRange(DocumentTab tab, string label = "Pages", bool preferSelection = true) =>
        Add(label, new PageRangePicker(tab, preferSelection));

    public PositionPicker AddPosition(int pageCount, int currentPage, string label = "Position") =>
        Add(label, new PositionPicker(pageCount, currentPage));

    /// <summary>Called on OK; return an error message to keep the dialog open.</summary>
    public void Validate(Func<string?> validate) => _validate = validate;

    public bool ShowModal() => ShowDialog() == true;

    public static bool TryNumber(string text, double min, double max, out double value) =>
        double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out value) && value >= min && value <= max;
}

/// <summary>"All / Current / Selected / Range" page choice with an even/odd filter.</summary>
public sealed class PageRangePicker : StackPanel
{
    private readonly RadioButton _all = new() { Content = "All pages" };
    private readonly RadioButton _current;
    private readonly RadioButton _selected;
    private readonly RadioButton _range = new() { Content = "Pages:", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _rangeText = new() { Width = 200, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ComboBox _subset = new() { ItemsSource = new[] { "All pages in range", "Even pages only", "Odd pages only" }, SelectedIndex = 0, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly int _pageCount;
    private readonly int _currentPage;
    private readonly int[] _selection;

    public PageRangePicker(DocumentTab tab, bool preferSelection)
    {
        _pageCount = tab.PageView.PageCount;
        _currentPage = Organize.CurrentPage(tab);
        _selection = tab.SelectedPages;
        var group = $"range{GetHashCode()}";
        _all.GroupName = _range.GroupName = group;
        _current = new RadioButton { Content = $"Current page ({_currentPage + 1})", GroupName = group };
        _selected = new RadioButton { Content = $"Selected pages ({PageRange.Format(_selection)})", GroupName = group };
        _rangeText.Text = PageRange.Format(_selection);
        _rangeText.GotFocus += (_, _) => _range.IsChecked = true;

        Children.Add(_all);
        Children.Add(_current);
        if (_selection.Length > 1) Children.Add(_selected);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        row.Children.Add(_range);
        row.Children.Add(_rangeText);
        Children.Add(row);
        Children.Add(new TextBlock { Text = $"e.g. 1-3, 5, 8-  (document has {_pageCount} pages)", Margin = new Thickness(0, 4, 0, 8), Style = (Style)Application.Current.FindResource("Caption") });
        Children.Add(_subset);

        if (preferSelection && _selection.Length > 1) _selected.IsChecked = true;
        else if (preferSelection) _current.IsChecked = true;
        else _all.IsChecked = true;
    }

    public void SelectAll() => _all.IsChecked = true;

    public bool TryGet(out int[] pages, out string? error)
    {
        error = null;
        if (_all.IsChecked == true) pages = [.. Enumerable.Range(0, _pageCount)];
        else if (_current.IsChecked == true) pages = [_currentPage];
        else if (_selected.IsChecked == true) pages = _selection;
        else if (!PageRange.TryParse(_rangeText.Text, _pageCount, out pages, out error)) return false;

        pages = PageRange.Apply(pages, (PageSubset)_subset.SelectedIndex);
        if (pages.Length == 0)
        {
            error = "No pages match that choice.";
            return false;
        }
        return true;
    }
}

/// <summary>"Before/After page N" (plus First/Last shortcuts), resolved to an insertion index 0..count.</summary>
public sealed class PositionPicker : StackPanel
{
    private readonly ComboBox _where = new() { ItemsSource = new[] { "After page", "Before page" }, SelectedIndex = 0, Width = 130 };
    private readonly TextBox _page = new() { Width = 70, Margin = new Thickness(8, 0, 0, 0) };
    private readonly int _pageCount;

    public PositionPicker(int pageCount, int currentPage)
    {
        _pageCount = pageCount;
        Orientation = Orientation.Horizontal;
        _page.Text = (currentPage + 1).ToString();
        Children.Add(_where);
        Children.Add(_page);
        Children.Add(new TextBlock { Text = $"of {pageCount}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        var first = new Button { Content = "First", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        var last = new Button { Content = "Last", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        first.Click += (_, _) => { _where.SelectedIndex = 1; _page.Text = "1"; };
        last.Click += (_, _) => { _where.SelectedIndex = 0; _page.Text = pageCount.ToString(); };
        Children.Add(first);
        Children.Add(last);
    }

    public bool TryGet(out int before, out string? error)
    {
        before = 0;
        error = null;
        if (!int.TryParse(_page.Text.Trim(), out var page) || page < 1 || page > _pageCount)
        {
            error = $"Enter a page number from 1 to {_pageCount}.";
            return false;
        }
        before = _where.SelectedIndex == 0 ? page : page - 1;
        return true;
    }
}

/// <summary>Opening PDFs for import (insert, combine) with a password prompt when needed.</summary>
public static class PdfFiles
{
    public const string OpenFilter =
        "Supported documents|*.pdf;*.xps;*.oxps;*.epub;*.cbz;*.fb2;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.gif|PDF files (*.pdf)|*.pdf|All files (*.*)|*.*";

    public const string PdfFilter = "PDF files (*.pdf)|*.pdf";

    public static string? PromptPassword(Window owner, string name, bool wrong)
    {
        var dlg = new FormDialog(owner, "Password required", "Open");
        if (wrong) dlg.AddNote("That password is incorrect. Try again.");
        dlg.AddNote($"\"{name}\" is protected. Enter its password to open it.");
        var box = dlg.Add("Password", new PasswordBox());
        dlg.Loaded += (_, _) => box.Focus();
        return dlg.ShowModal() ? box.Password : null;
    }

    /// <summary>Asks for the permissions (owner) password an edit needs. Null if the user cancels.</summary>
    public static string? PromptOwnerPassword(Window owner, string name, bool wrong)
    {
        var dlg = new FormDialog(owner, "Permissions password required", "Edit");
        if (wrong) dlg.AddNote("That password is incorrect. Try again.");
        dlg.AddNote($"\"{name}\" opens without a password, but its author restricted changes. Enter its permissions (owner) password to edit it.");
        var box = dlg.Add("Password", new PasswordBox());
        dlg.Loaded += (_, _) => box.Focus();
        return dlg.ShowModal() ? box.Password : null;
    }

    /// <summary>Reads a PDF and verifies it opens (prompting for a password). Null if the user cancels or it can't be read.</summary>
    public static PdfSource? LoadForImport(Window owner, string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Prompt.Error(owner, $"Couldn't read {Path.GetFileName(path)}", ex.Message);
            return null;
        }

        string? password = null;
        var wrong = false;
        while (true)
        {
            try
            {
                using var engine = PdfEngine.Open(data, MuPDFCore.InputFileTypes.PDF, password);
                return new PdfSource(data, password, Path.GetFileName(path));
            }
            catch (PasswordRequiredException ex)
            {
                password = PromptPassword(owner, Path.GetFileName(path), wrong || ex.WrongPassword);
                if (password is null) return null;
                wrong = true;
            }
            catch (Exception ex) when (ex is MuPDFCore.MuPDFException or InvalidOperationException)
            {
                Prompt.Error(owner, $"{Path.GetFileName(path)} isn't a readable PDF", ex.Message);
                return null;
            }
        }
    }

    public static string? PickPdf(Window owner, string title)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = PdfFilter, InitialDirectory = AppSettings.Current.LastFolder };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    public static string? PickSavePath(Window owner, string suggestedName, string? folder = null)
    {
        if (DevScript.TakeSavePath(out var scripted)) return scripted;
        var dlg = new SaveFileDialog
        {
            Title = "Save As",
            Filter = PdfFilter,
            FileName = suggestedName,
            DefaultExt = ".pdf",
            AddExtension = true,
            InitialDirectory = folder ?? AppSettings.Current.LastFolder,
        };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    public static string? PickFolder(Window owner, string title, string? initial = null)
    {
        var dlg = new OpenFolderDialog { Title = title, InitialDirectory = initial ?? AppSettings.Current.LastFolder };
        return dlg.ShowDialog(owner) == true ? dlg.FolderName : null;
    }

    /// <summary>Longest file name stem Inkvolt PDF generates (bookmark titles can be any length).</summary>
    private const int MaxStem = 120;

    /// <summary>Full paths stay under the classic MAX_PATH so Explorer and older apps can open them.</summary>
    private const int MaxPath = 259;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Makes a file name safe and unique inside <paramref name="folder"/>: invalid characters replaced, device names
    /// avoided, and the name shortened so the full path fits. Names in <paramref name="reserved"/> count as taken (and the
    /// result is added), so a batch can pick all its names before writing anything.
    /// </summary>
    /// <exception cref="IOException">The folder path itself is too long for any file name.</exception>
    public static string UniquePath(string folder, string fileName, ISet<string>? reserved = null)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
        var ext = Path.GetExtension(fileName);
        if (ext.Length > 10) ext = "";
        var stem = (ext.Length > 0 ? fileName[..^ext.Length] : fileName).Trim().TrimEnd('.', ' ');
        if (stem.Length == 0) stem = "Untitled";
        if (ReservedNames.Contains(stem.Split('.')[0])) stem = "_" + stem;

        // Room for the folder, a separator, " (999)" and the extension.
        var room = Math.Min(MaxStem, MaxPath - Path.GetFullPath(folder).TrimEnd('\\').Length - 1 - 6 - ext.Length);
        if (room < 8) throw new IOException("The folder path is too long. Choose a folder with a shorter path.");
        if (stem.Length > room) stem = Truncate(stem, room).TrimEnd('.', ' ');

        var path = Path.Combine(folder, stem + ext);
        for (var n = 2; File.Exists(path) || Directory.Exists(path) || reserved?.Contains(path) == true; n++)
            path = Path.Combine(folder, $"{stem} ({n}){ext}");
        reserved?.Add(path);
        return path;
    }

    /// <summary>Cuts to at most <paramref name="max"/> chars without splitting a surrogate pair.</summary>
    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut];
    }

    /// <summary>
    /// Writes a batch of new files (split, extract per page). Stops at the first failure, removes the partial file, and
    /// throws <see cref="BatchWriteException"/> listing what was already written.
    /// </summary>
    public static void WriteBatch(IReadOnlyList<(string Path, Func<byte[]> Bytes)> files, CancellationToken cancel = default)
    {
        var written = new List<string>();
        foreach (var (path, make) in files)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var bytes = make();
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) fs.Write(bytes);
                written.Add(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is not IOException { HResult: unchecked((int)0x80070050) }) // don't delete a file that already existed
                    try { File.Delete(path); } catch (Exception) { }
                throw new BatchWriteException(written, path, ex);
            }
        }
    }

    /// <summary>True when <paramref name="path"/> is the file behind a document open in any Inkvolt PDF tab.</summary>
    public static bool IsOpenInTab(string path)
    {
        var full = Path.GetFullPath(path);
        return Application.Current.Windows.OfType<MainWindow>()
            .SelectMany(w => w.DocsHost.Children.OfType<DocumentTab>())
            .Any(t => t.Session.FilePath is { } open && string.Equals(Path.GetFullPath(open), full, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A batch write failed partway; <see cref="Written"/> lists the files that were completed.</summary>
public sealed class BatchWriteException(IReadOnlyList<string> written, string failedPath, Exception inner)
    : IOException($"Couldn't write {Path.GetFileName(failedPath)}: {inner.Message}", inner)
{
    public IReadOnlyList<string> Written { get; } = written;
    public string FailedPath { get; } = failedPath;
}

/// <summary>Combine Files: an ordered list of PDFs merged into one new document.</summary>
public sealed class CombineDialog : Window
{
    private readonly ObservableCollection<string> _files = [];
    private readonly ListBox _list;
    private readonly CheckBox _bookmarks = new() { Content = "Add a bookmark for each file", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };

    public IReadOnlyList<string> Files => _files;
    public bool BookmarkPerFile => _bookmarks.IsChecked == true;

    public CombineDialog(Window owner, IEnumerable<string> initial)
    {
        Owner = owner;
        Title = "Combine Files into PDF";
        Width = 620;
        Height = 480;
        MinWidth = 420;
        MinHeight = 320;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var f in initial) _files.Add(f);

        _list = new ListBox { ItemsSource = _files, SelectionMode = SelectionMode.Extended, AllowDrop = true };
        _list.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                foreach (var f in dropped.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))) _files.Add(f);
        };

        Button B(string text, Action onClick)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += (_, _) => onClick();
            return b;
        }

        var side = new StackPanel { Width = 130, Margin = new Thickness(12, 0, 0, 0) };
        side.Children.Add(B("Add Files…", AddFiles));
        side.Children.Add(B("Remove", () => { foreach (var f in _list.SelectedItems.Cast<string>().ToList()) _files.Remove(f); }));
        side.Children.Add(B("Move Up", () => MoveSelected(-1)));
        side.Children.Add(B("Move Down", () => MoveSelected(1)));
        side.Children.Add(B("Sort by Name", () =>
        {
            var sorted = _files.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
            _files.Clear();
            foreach (var f in sorted) _files.Add(f);
        }));

        var ok = new Button { Content = "Combine", IsDefault = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            if (_files.Count == 0)
            {
                Prompt.Info(this, "Nothing to combine yet", "Add at least one PDF with Add Files, or drag PDFs into the list.");
                return;
            }
            DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 96 });

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hint = new TextBlock { Text = "Files are combined in this order. Drag PDFs here to add them.", Margin = new Thickness(0, 0, 0, 8), Style = (Style)Application.Current.FindResource("Caption") };
        Grid.SetColumnSpan(hint, 2);
        grid.Children.Add(hint);
        Grid.SetRow(_list, 1);
        grid.Children.Add(_list);
        Grid.SetRow(side, 1);
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);
        Grid.SetRow(_bookmarks, 2);
        grid.Children.Add(_bookmarks);
        Grid.SetRow(buttons, 3);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;

        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete)
                foreach (var f in _list.SelectedItems.Cast<string>().ToList()) _files.Remove(f);
        };
        if (_files.Count == 0) Loaded += (_, _) => AddFiles();
    }

    private void AddFiles()
    {
        var dlg = new OpenFileDialog { Title = "Add PDF files", Filter = PdfFiles.PdfFilter, Multiselect = true, InitialDirectory = AppSettings.Current.LastFolder };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) _files.Add(f);
    }

    private void MoveSelected(int delta)
    {
        var selected = _list.SelectedItems.Cast<string>().Select(f => _files.IndexOf(f)).Order().ToList();
        if (selected.Count == 0) return;
        if (delta < 0 && selected[0] == 0) return;
        if (delta > 0 && selected[^1] == _files.Count - 1) return;
        foreach (var i in delta < 0 ? selected : Enumerable.Reverse(selected)) _files.Move(i, i + delta);
    }
}
