using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PdfDesk.App.Viewer;
using PdfDesk.Core;

namespace PdfDesk.App;

public sealed class FileNameConverter(bool folder) : IValueConverter
{
    public static FileNameConverter Name { get; } = new(false);
    public static FileNameConverter Folder { get; } = new(true);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string path ? (folder ? Path.GetDirectoryName(path) ?? "" : Path.GetFileName(path)) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private static readonly string[] ZoomPresets = ["Fit Width", "Fit Page", "25%", "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%", "800%", "1600%"];
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _updatingZoomBox;
    private WindowState _preFullScreenState;
    private bool _fullScreen;

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();
        BindCommands();
        RefreshRecent();

        ZoomBox.ItemsSource = ZoomPresets;
        ZoomBox.SelectionChanged += (_, _) =>
        {
            if (!_updatingZoomBox && ZoomBox.SelectedItem is string s) ApplyZoomText(s);
        };
        ZoomBox.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ApplyZoomText(ZoomBox.Text);
            ActiveTab?.PageView.Focus();
            e.Handled = true;
        }), handledEventsToo: true);
        ZoomBox.LostKeyboardFocus += (_, _) => UpdateUi();

        PageBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && ActiveTab is { } t && int.TryParse(PageBox.Text.Trim(), out var n))
            {
                t.PageView.GoToPage(n - 1);
                t.PageView.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                UpdateUi();
                ActiveTab?.PageView.Focus();
            }
        };
        PageBox.GotKeyboardFocus += (_, _) => PageBox.SelectAll();
        PageBox.LostKeyboardFocus += (_, _) => UpdateUi();

        Docs.SelectionChanged += (s, e) =>
        {
            if (e.OriginalSource == Docs) UpdateUi();
        };
        Drop += OnFileDrop;
        DragOver += (_, e) =>
        {
            if (e.Handled) return;
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Closing += OnClosing;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _fullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
        };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusText.Text = "";
        };
        UpdateUi();
    }

    public DocumentTab? ActiveTab => (Docs.SelectedItem as TabItem)?.Content as DocumentTab;
    private IEnumerable<DocumentTab> Tabs => Docs.Items.OfType<TabItem>().Select(t => (DocumentTab)t.Content);

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void ShowStatus(string text)
    {
        StatusText.Text = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ───────────────────────────── Open / close ─────────────────────────────

    public void OpenFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
            OpenFile(path);
    }

    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"File not found:\n{path}", "PdfDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            AppSettings.Current.RecentFiles.Remove(path);
            AppSettings.Current.Save();
            RefreshRecent();
            return;
        }

        var full = Path.GetFullPath(path);
        foreach (TabItem item in Docs.Items)
            if (string.Equals(((DocumentTab)item.Content).Session.FilePath, full, StringComparison.OrdinalIgnoreCase))
            {
                Docs.SelectedItem = item;
                return;
            }

        string? password = null;
        var wrong = false;
        while (true)
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var session = DocumentSession.Open(full, password);
                Mouse.OverrideCursor = null;
                OpenSession(session);
                AppSettings.Current.AddRecent(full);
                RefreshRecent();
                return;
            }
            catch (PasswordRequiredException ex)
            {
                Mouse.OverrideCursor = null;
                password = PdfFiles.PromptPassword(this, Path.GetFileName(full), wrong || ex.WrongPassword);
                if (password is null) return;
                wrong = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MuPDFCore.MuPDFException or InvalidOperationException)
            {
                Mouse.OverrideCursor = null;
                Log.Write($"Open {full} failed: {ex}");
                MessageBox.Show(this, $"Couldn't open {Path.GetFileName(full)}.\n\n{ex.Message}", "PdfDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
    }

    public void OpenSession(DocumentSession session)
    {
        var tab = new DocumentTab(session);
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button
        {
            Content = "",
            FontFamily = (System.Windows.Media.FontFamily)FindResource("Icons"),
            FontSize = 10,
            Padding = new Thickness(4),
            Margin = new Thickness(8, 0, -4, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = "Close (Ctrl+W)",
            Focusable = false,
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(title);
        header.Children.Add(close);
        var item = new TabItem { Header = header, Content = tab, AllowDrop = true, ToolTip = session.FilePath ?? session.DisplayName };

        close.Click += (_, _) => CloseTab(item);
        header.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) CloseTab(item);
        };
        // Hovering a page drag over a tab switches to it, so pages can be dropped into another document.
        item.DragEnter += (_, _) => Docs.SelectedItem = item;

        void Refresh()
        {
            title.Text = tab.Header;
            item.ToolTip = session.FilePath ?? session.DisplayName;
            if (ActiveTab == tab) UpdateUi();
        }
        tab.StateChanged += (_, _) => Refresh();
        tab.Status += (_, m) => ShowStatus(m);
        Refresh();

        Docs.Items.Add(item);
        Docs.SelectedItem = item;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => tab.PageView.Focus());
    }

    /// <summary>Closes a tab, asking to save unsaved changes. False if the user cancelled.</summary>
    private bool CloseTab(TabItem item)
    {
        var tab = (DocumentTab)item.Content;
        if (tab.IsBusy) return false;
        if (tab.Session.IsDirty)
        {
            Docs.SelectedItem = item;
            var answer = MessageBox.Show(this, $"Save changes to \"{tab.Session.DisplayName}\" before closing?", "PdfDesk",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes && !Save(tab)) return false;
        }
        Docs.Items.Remove(item);
        tab.Dispose();
        UpdateUi();
        return true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        foreach (var item in Docs.Items.OfType<TabItem>().ToList())
            if (!CloseTab(item))
            {
                e.Cancel = true;
                return;
            }
        SavePlacement();
    }

    private bool Save(DocumentTab tab)
    {
        if (tab.Session.FilePath is null || !tab.Session.IsPdf) return SaveAs(tab);
        return WriteTo(tab, tab.Session.FilePath);
    }

    private bool SaveAs(DocumentTab tab)
    {
        var s = tab.Session;
        var ext = s.IsPdf ? ".pdf" : Path.GetExtension(s.DisplayName);
        var dlg = new SaveFileDialog
        {
            Title = "Save As",
            FileName = Path.GetFileNameWithoutExtension(s.DisplayName) + ext,
            Filter = s.IsPdf ? PdfFiles.PdfFilter : $"{ext.TrimStart('.').ToUpperInvariant()} files (*{ext})|*{ext}",
            DefaultExt = ext,
            AddExtension = true,
            InitialDirectory = Path.GetDirectoryName(s.FilePath) ?? AppSettings.Current.LastFolder,
        };
        return dlg.ShowDialog(this) == true && WriteTo(tab, dlg.FileName);
    }

    private bool WriteTo(DocumentTab tab, string path)
    {
        try
        {
            tab.Session.SaveAs(path);
            AppSettings.Current.AddRecent(path);
            RefreshRecent();
            ShowStatus($"Saved {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Save {path} failed: {ex}");
            MessageBox.Show(this, $"Couldn't save {Path.GetFileName(path)}.\n\n{ex.Message}\n\nIf another program has the file open, close it or use Save As.",
                "PdfDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        Activate();
        OpenFiles(files);
    }

    private void ShowOpenDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open",
            Filter = PdfFiles.OpenFilter,
            Multiselect = true,
            InitialDirectory = AppSettings.Current.LastFolder,
        };
        if (dlg.ShowDialog(this) == true) OpenFiles(dlg.FileNames);
    }

    private void RefreshRecent()
    {
        var recent = AppSettings.Current.RecentFiles.ToList();
        RecentList.ItemsSource = recent;
        RecentHeader.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RecentMenu.Items.Clear();
        for (var i = 0; i < recent.Count; i++)
        {
            var path = recent[i];
            var mi = new MenuItem { Header = $"_{(i + 1) % 10} {Path.GetFileName(path)}", ToolTip = path };
            mi.Click += (_, _) => OpenFile(path);
            RecentMenu.Items.Add(mi);
        }
        if (recent.Count > 0)
        {
            RecentMenu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "_Clear Recent" };
            clear.Click += (_, _) =>
            {
                AppSettings.Current.RecentFiles.Clear();
                AppSettings.Current.Save();
                RefreshRecent();
            };
            RecentMenu.Items.Add(clear);
        }
        RecentMenu.IsEnabled = recent.Count > 0;
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) OpenFile(path);
    }

    // ───────────────────────────── Commands ─────────────────────────────

    private void Bind(ICommand command, Action<DocumentTab> execute, Func<DocumentTab, bool>? can = null) =>
        CommandBindings.Add(new CommandBinding(command,
            (_, e) => { if (ActiveTab is { } t) execute(t); e.Handled = true; },
            (_, e) => { e.CanExecute = ActiveTab is { IsBusy: false } t && (can?.Invoke(t) ?? true); e.Handled = true; }));

    private void BindAlways(ICommand command, Action execute) =>
        CommandBindings.Add(new CommandBinding(command, (_, e) => { execute(); e.Handled = true; }, (_, e) => { e.CanExecute = true; e.Handled = true; }));

    private void BindEdit(ICommand command, Func<DocumentTab, Task> run, Func<DocumentTab, bool>? can = null) =>
        Bind(command, t => _ = run(t), t => t.Session.IsPdf && (can?.Invoke(t) ?? true));

    private void BindCommands()
    {
        BindAlways(Cmd.Open, ShowOpenDialog);
        BindAlways(Cmd.Combine, () => _ = Organize.Combine(this, this));
        BindAlways(Cmd.Exit, Close);
        Bind(Cmd.Save, t => Save(t), t => t.Session.IsDirty || t.Session.FilePath is null);
        Bind(Cmd.SaveAs, t => SaveAs(t));
        Bind(Cmd.ShowInFolder, t => Shell.ShowInFolder(t.Session.FilePath!), t => t.Session.FilePath is not null);
        Bind(Cmd.CloseDoc, _ => CloseTab((TabItem)Docs.SelectedItem));
        Bind(Cmd.CloseAll, _ => { foreach (var item in Docs.Items.OfType<TabItem>().ToList()) if (!CloseTab(item)) break; });
        Bind(Cmd.Print, t => FileActions.Print(this, t));
        Bind(Cmd.ExportImages, t => _ = FileActions.ExportImages(this, t, this));
        Bind(Cmd.Properties, t => _ = FileActions.ShowProperties(this, t));

        Bind(Cmd.Undo, t => t.Undo(), t => t.Session.CanUndo);
        Bind(Cmd.Redo, t => t.Redo(), t => t.Session.CanRedo);
        Bind(Cmd.Copy, CopySelection, t => t.PageView.HasSelection || Keyboard.FocusedElement is TextBox);
        Bind(Cmd.SelectAll, t =>
        {
            if (Keyboard.FocusedElement is TextBox tb) tb.SelectAll();
            else t.PageView.SelectAll();
        });
        Bind(Cmd.Find, t => t.ShowFindBar());
        Bind(Cmd.FindNext, t => t.FindNext(false));
        Bind(Cmd.FindPrevious, t => t.FindNext(true));
        Bind(Cmd.SearchPane, t => t.ShowSearchPane());
        Bind(Cmd.ToolHand, t => t.PageView.Tool = ViewerTool.Hand);
        Bind(Cmd.ToolSelect, t => t.PageView.Tool = ViewerTool.SelectText);
        Bind(Cmd.ToolSnapshot, t => t.PageView.Tool = ViewerTool.Snapshot);

        Bind(Cmd.ZoomIn, t => t.PageView.ZoomStep(1));
        Bind(Cmd.ZoomOut, t => t.PageView.ZoomStep(-1));
        Bind(Cmd.ActualSize, t => t.PageView.SetZoom(1));
        Bind(Cmd.FitWidth, t => SetZoomMode(t, ZoomMode.FitWidth));
        Bind(Cmd.FitPage, t => SetZoomMode(t, ZoomMode.FitPage));
        Bind(Cmd.LayoutSingle, t => SetLayout(t, ViewLayout.SinglePage));
        Bind(Cmd.LayoutContinuous, t => SetLayout(t, ViewLayout.Continuous));
        Bind(Cmd.LayoutFacing, t => SetLayout(t, ViewLayout.Facing));
        Bind(Cmd.LayoutCover, t => SetLayout(t, ViewLayout.FacingCover));
        Bind(Cmd.FirstPage, t => t.PageView.GoToPage(0), t => t.PageView.CurrentPage > 0);
        Bind(Cmd.PrevPage, t => t.PageView.PreviousPage(), t => t.PageView.CurrentPage > 0);
        Bind(Cmd.NextPage, t => t.PageView.NextPage(), t => t.PageView.CurrentPage < t.PageView.PageCount - 1);
        Bind(Cmd.LastPage, t => t.PageView.GoToPage(t.PageView.PageCount - 1), t => t.PageView.CurrentPage < t.PageView.PageCount - 1);
        Bind(Cmd.GoToPage, _ => { PageBox.Focus(); PageBox.SelectAll(); });
        Bind(Cmd.Back, t => t.PageView.GoBack(), t => t.PageView.CanGoBack);
        Bind(Cmd.Forward, t => t.PageView.GoForward(), t => t.PageView.CanGoForward);
        Bind(Cmd.NavPane, t =>
        {
            var show = !t.NavPaneVisible;
            foreach (var tab in Tabs) tab.SetNavPaneVisible(show);
            AppSettings.Current.NavPaneVisible = show;
            AppSettings.Current.Save();
            UpdateUi();
        });
        BindAlways(Cmd.FullScreen, ToggleFullScreen);

        BindEdit(Cmd.RotateRight, t => Organize.RotateSelected(t, 90));
        BindEdit(Cmd.RotateLeft, t => Organize.RotateSelected(t, -90));
        BindEdit(Cmd.RotatePages, t => Organize.RotateDialog(this, t));
        BindEdit(Cmd.DeletePages, t => Organize.DeleteDialog(this, t), t => t.PageView.PageCount > 1);
        BindEdit(Cmd.DeleteSelected, Organize.DeleteSelected, t => t.SelectedPages.Length < t.PageView.PageCount);
        BindEdit(Cmd.InsertPages, t => Organize.InsertFromFile(this, t));
        BindEdit(Cmd.InsertBlank, t => Organize.InsertBlank(this, t));
        BindEdit(Cmd.ExtractPages, t => Organize.Extract(this, t, this));
        BindEdit(Cmd.SplitDocument, t => Organize.Split(this, t, this), t => t.PageView.PageCount > 1);
        BindEdit(Cmd.MovePages, t => Organize.Move(this, t), t => t.PageView.PageCount > 1);
        BindEdit(Cmd.DuplicatePages, Organize.Duplicate);
        BindEdit(Cmd.ReversePages, Organize.Reverse, t => t.PageView.PageCount > 1);
        BindEdit(Cmd.CropPages, t => Organize.Crop(this, t));
    }

    private void CopySelection(DocumentTab t)
    {
        if (Keyboard.FocusedElement is TextBox tb)
        {
            tb.Copy();
            return;
        }
        var text = t.PageView.GetSelectedText();
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            ShowStatus($"Copied {text.Length:N0} characters");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ShowStatus($"Couldn't copy: {ex.Message}");
        }
    }

    private void SetZoomMode(DocumentTab t, ZoomMode mode)
    {
        t.PageView.SetZoomMode(mode);
        AppSettings.Current.ZoomMode = mode.ToString();
        AppSettings.Current.Save();
        UpdateUi();
    }

    private void SetLayout(DocumentTab t, ViewLayout layout)
    {
        t.PageView.SetLayout(layout);
        AppSettings.Current.Layout = layout.ToString();
        AppSettings.Current.Save();
        UpdateUi();
    }

    private void ApplyZoomText(string text)
    {
        if (ActiveTab is not { } t) return;
        text = text.Trim();
        if (text.Equals("Fit Width", StringComparison.OrdinalIgnoreCase)) SetZoomMode(t, ZoomMode.FitWidth);
        else if (text.Equals("Fit Page", StringComparison.OrdinalIgnoreCase)) SetZoomMode(t, ZoomMode.FitPage);
        else if (double.TryParse(text.TrimEnd('%', ' '), NumberStyles.Float, CultureInfo.CurrentCulture, out var pct) && pct > 0)
        {
            t.PageView.SetZoom(pct / 100);
            AppSettings.Current.ZoomMode = nameof(ZoomMode.Custom);
        }
        UpdateUi();
    }

    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        var chrome = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        MainMenu.Visibility = CommandBar.Visibility = StatusBarBorder.Visibility = chrome;
        foreach (var tab in Tabs) tab.SetNavPaneVisible(!_fullScreen && AppSettings.Current.NavPaneVisible);
        if (_fullScreen)
        {
            _preFullScreenState = WindowState;
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // re-maximize so the taskbar is covered
            WindowState = WindowState.Maximized;
            ShowStatus("Press Esc or F11 to leave full screen");
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _preFullScreenState;
        }
    }

    // ───────────────────────────── UI state ─────────────────────────────

    private void UpdateUi()
    {
        var t = ActiveTab;
        var hasDoc = t is not null;
        Docs.Visibility = Docs.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StartPage.Visibility = Docs.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        Title = t is null ? "PdfDesk" : $"{t.Header} – PdfDesk";

        var v = t?.PageView;
        if (!PageBox.IsKeyboardFocusWithin) PageBox.Text = v is { PageCount: > 0 } ? (v.CurrentPage + 1).ToString() : "";
        PageCountText.Text = v is null ? "" : $"/ {v.PageCount}";
        PageBox.IsEnabled = ZoomBox.IsEnabled = hasDoc;

        if (!ZoomBox.IsKeyboardFocusWithin)
        {
            _updatingZoomBox = true;
            ZoomBox.SelectedItem = null;
            ZoomBox.Text = v is null ? "" : $"{v.Zoom * 100:0}%";
            _updatingZoomBox = false;
        }

        HandButton.IsChecked = HandMenu.IsChecked = v?.Tool == ViewerTool.Hand;
        SelectButton.IsChecked = SelectMenu.IsChecked = v?.Tool == ViewerTool.SelectText;
        SnapshotButton.IsChecked = SnapshotMenu.IsChecked = v?.Tool == ViewerTool.Snapshot;
        FitWidthButton.IsChecked = FitWidthMenu.IsChecked = v?.ZoomMode == ZoomMode.FitWidth;
        FitPageButton.IsChecked = FitPageMenu.IsChecked = v?.ZoomMode == ZoomMode.FitPage;
        SingleMenu.IsChecked = v?.Layout == ViewLayout.SinglePage;
        ContinuousMenu.IsChecked = v?.Layout == ViewLayout.Continuous;
        FacingMenu.IsChecked = v?.Layout == ViewLayout.Facing;
        CoverMenu.IsChecked = v?.Layout == ViewLayout.FacingCover;
        NavPaneButton.IsChecked = NavPaneMenu.IsChecked = t?.NavPaneVisible ?? AppSettings.Current.NavPaneVisible;

        UndoMenu.Header = t?.Session.UndoLabel is { } u ? $"_Undo {u}" : "_Undo";
        RedoMenu.Header = t?.Session.RedoLabel is { } r ? $"_Redo {r}" : "_Redo";
        UndoButton.ToolTip = t?.Session.UndoLabel is { } u2 ? $"Undo {u2} (Ctrl+Z)" : "Undo (Ctrl+Z)";
        RedoButton.ToolTip = t?.Session.RedoLabel is { } r2 ? $"Redo {r2} (Ctrl+Y)" : "Redo (Ctrl+Y)";

        if (v is { PageCount: > 0 } && t is not null)
        {
            var g = t.Session.Engine.GetPage(Math.Clamp(v.CurrentPage, 0, v.PageCount - 1));
            var sel = t.SelectedPages.Length > 1 ? $"{t.SelectedPages.Length} pages selected   ·   " : "";
            PageSizeText.Text = $"{sel}{Organize.Inches(g.Width)} × {Organize.Inches(g.Height)} in";
        }
        else
        {
            PageSizeText.Text = "";
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void RestorePlacement()
    {
        var s = AppSettings.Current;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop) &&
            s.WindowLeft >= SystemParameters.VirtualScreenLeft - 50 && s.WindowTop >= SystemParameters.VirtualScreenTop - 50 &&
            s.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            s.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft;
            Top = s.WindowTop;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var s = AppSettings.Current;
        var bounds = _fullScreen ? RestoreBounds : WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
        }
        s.WindowMaximized = _fullScreen ? _preFullScreenState == WindowState.Maximized : WindowState == WindowState.Maximized;
        s.Save();
    }

    // ───────────────────────────── Help ─────────────────────────────

    private void OnShowBookmarks(object sender, RoutedEventArgs e) => ActiveTab?.ShowBookmarks();

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Log.FilePath)!);
        Shell.Open(Path.GetDirectoryName(Log.FilePath)!);
    }

    private void OnShortcuts(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        """
        Files
          Ctrl+O  Open          Ctrl+S  Save          Ctrl+Shift+S  Save As
          Ctrl+P  Print         Ctrl+W  Close tab     Ctrl+D  Properties

        Navigation
          PgUp / PgDn, Space    Scroll by a screen
          Ctrl+Home / Ctrl+End  First / last page
          Ctrl+G                Go to page
          Alt+Left / Alt+Right  Previous / next view

        Zoom and layout
          Ctrl+= / Ctrl+-       Zoom in / out  (or Ctrl+mouse wheel)
          Ctrl+1  Actual size   Ctrl+2  Fit width   Ctrl+0  Fit page
          Ctrl+4 / 5 / 6 / 7    Single / Continuous / Two pages / Two pages with cover
          F4  Navigation pane   F11  Full screen

        Tools (click the page first)
          H  Hand    T  Select text    G  Snapshot (copy an area as an image)

        Text
          Ctrl+C  Copy          Ctrl+A  Select all text
          Ctrl+F  Find          F3 / Shift+F3  Next / previous match
          Ctrl+Shift+F  Search pane

        Pages
          Ctrl+Shift++ / Ctrl+Shift+-  Rotate selected pages
          Del (in thumbnails)          Delete selected pages
          Ctrl+Shift+I  Insert pages   Ctrl+Shift+E  Extract pages
          Ctrl+Shift+D  Delete pages   Ctrl+Shift+M  Move pages
          Drag thumbnails to reorder; drag them onto another tab to copy pages there.
          Ctrl+Z / Ctrl+Y  Undo / redo page edits
        """, "Keyboard Shortcuts", MessageBoxButton.OK, MessageBoxImage.None);

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        MessageBox.Show(this,
            $"""
            PdfDesk {version?.ToString(3)}
            A fast PDF viewer and page organizer for Windows on ARM.
            Process: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}

            Rendering, text and search: MuPDF (AGPL-3.0) via MuPDFCore.
            Page editing: PDFsharp (MIT).
            Personal use. No data leaves this computer.
            """, "About PdfDesk", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
