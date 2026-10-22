using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PdfDesk.App.Viewer;
using PdfDesk.Core;

namespace PdfDesk.App;

/// <summary>A document tab in the title bar.</summary>
public sealed class TabModel(DocumentTab tab) : INotifyPropertyChanged
{
    public DocumentTab Tab { get; } = tab;
    public override string ToString() => Title;
    public string Title => Tab.Session.DisplayName;
    public string ToolTip => Tab.Session.FilePath ?? $"{Tab.Session.DisplayName} (not saved yet)";
    public Visibility DirtyVisibility => Tab.Session.IsDirty ? Visibility.Visible : Visibility.Collapsed;

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DirtyVisibility)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A recent file card on the start page; the first-page preview loads in the background.</summary>
public sealed class RecentItem(string path, string detail) : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;

    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Detail { get; } = detail;

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A command palette entry.</summary>
public sealed record PaletteItem(string Name, string Category, string Gesture, string Glyph, Action Run)
{
    public override string ToString() => Gesture.Length > 0 ? $"{Name}, {Gesture}" : Name;
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TabModel> _tabs = [];
    private WindowState _preFullScreenState;
    private bool _fullScreen;
    private List<PaletteItem> _paletteAll = [];
    private static readonly Dictionary<string, BitmapSource> RecentThumbs = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        TabStrip.ItemsSource = _tabs;
        RestorePlacement();
        BindCommands();
        RefreshRecent();

        TabStrip.SelectionChanged += (_, _) => ShowActiveTab();
        TabStrip.PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle && ItemUnder(e.OriginalSource) is { } t) CloseTab(t);
        };
        // Hovering a page drag over a tab switches to it, so pages can be dropped into another document.
        TabStrip.AllowDrop = true;
        TabStrip.DragOver += (_, e) =>
        {
            if (ItemUnder(e.OriginalSource) is { } t) TabStrip.SelectedItem = t;
        };

        DragOver += OnWindowDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnFileDrop;
        Closing += OnClosing;
        StateChanged += (_, _) => FitToScreenWhenMaximized();
        SourceInitialized += (_, _) =>
        {
            App.ApplyFrame(this);
            FitToScreenWhenMaximized();
            MeasureCaptionButtons();
        };
        DpiChanged += (_, _) => MeasureCaptionButtons();
        PreviewKeyDown += OnPreviewKey;

        CommandBar.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase && Mouse.LeftButton == MouseButtonState.Released)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusDocument);
        }));
        KeyDown += OnWindowKey;
        TabStrip.PreviewMouseWheel += (_, e) =>
        {
            if (FindChild<ScrollViewer>(TabStrip) is { } sv) sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };
        PaletteBox.LostKeyboardFocus += (_, e) =>
        {
            if (Palette.Visibility == Visibility.Visible && e.NewFocus is DependencyObject d && !IsInside(d, Palette)) ClosePalette(focus: false);
        };
        PaletteBox.TextChanged += (_, _) => FilterPalette();
        PaletteBox.PreviewKeyDown += OnPaletteKey;
        PaletteList.MouseUp += (_, _) => RunPaletteSelection();
        Palette.MouseDown += (_, e) =>
        {
            if (e.OriginalSource == Palette) ClosePalette();
        };
        UpdateUi();
    }

    public DocumentTab? ActiveTab => (TabStrip.SelectedItem as TabModel)?.Tab;
    private IEnumerable<DocumentTab> Tabs => _tabs.Select(t => t.Tab);

    private static TabModel? ItemUnder(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: TabModel t }) return t;
        return null;
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ───────────────────────────── Title bar ─────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>Reserves exactly the width DWM uses for the minimize/maximize/close buttons.</summary>
    private void MeasureCaptionButtons()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;
        if (hwnd != IntPtr.Zero && DwmGetWindowAttribute(hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, out var r, Marshal.SizeOf<RECT>()) == 0 && r.Right > r.Left)
            CaptionColumn.Width = new GridLength((r.Right - r.Left) / VisualTreeHelper.GetDpi(this).DpiScaleX + 8);
    }

    /// <summary>A maximized window overhangs the screen by its resize frame; pad the content so nothing is cut off.</summary>
    private void FitToScreenWhenMaximized()
    {
        var f = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = WindowState == WindowState.Maximized && !_fullScreen
            ? new Thickness(f.Left + SystemParameters.FixedFrameVerticalBorderWidth, f.Top + SystemParameters.FixedFrameHorizontalBorderHeight,
                f.Right + SystemParameters.FixedFrameVerticalBorderWidth, f.Bottom + SystemParameters.FixedFrameHorizontalBorderHeight)
            : new Thickness(0);
    }

    // ───────────────────────────── Toasts ─────────────────────────────

    /// <summary>Shows a short message at the bottom-left; with an action (e.g. Undo) it stays a little longer.</summary>
    public void ShowToast(Toast toast)
    {
        var text = new TextBlock { Text = toast.Message, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        var icon = new TextBlock
        {
            Text = toast.IsError ? "" : toast.Action is not null ? "" : "",
            Style = (Style)FindResource("Icon"),
            Margin = new Thickness(0, 0, 12, 0),
            Foreground = (Brush)FindResource(toast.IsError ? "SystemFillColorCriticalBrush" : "AccentTextFillColorPrimaryBrush"),
        };
        var row = new DockPanel();
        var card = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 8, 10),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(1),
            Background = (Brush)FindResource("SolidBackgroundFillColorQuarternaryBrush"),
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.22 },
        };
        AutomationProperties.SetLiveSetting(card, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(card, toast.Message);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(toast.Action is null ? 3.5 : 7) };
        void Dismiss()
        {
            timer.Stop();
            Toasts.Children.Remove(card);
        }
        timer.Tick += (_, _) => Dismiss();
        card.MouseEnter += (_, _) => timer.Stop();
        card.MouseLeave += (_, _) => timer.Start();

        var close = new Button { Style = (Style)FindResource("IconButton"), Content = "", FontSize = 10, Height = 26, MinWidth = 26, ToolTip = "Dismiss" };
        close.Click += (_, _) => Dismiss();
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(close);
        if (toast.Action is { } action)
        {
            var act = new Button { Content = toast.ActionLabel ?? "OK", Style = (Style)FindResource("TextButton"), Margin = new Thickness(12, 0, 0, 0), FontWeight = FontWeights.SemiBold };
            act.Click += (_, _) =>
            {
                Dismiss();
                action();
            };
            DockPanel.SetDock(act, Dock.Right);
            row.Children.Add(act);
        }
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        Toasts.Children.Add(card);
        while (Toasts.Children.Count > 3) Toasts.Children.RemoveAt(0);
        card.Loaded += (_, _) => System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(card)?
            .RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
        timer.Start();
    }

    public void ShowStatus(string text) => ShowToast(new Toast(text));

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
            ShowToast(new Toast($"Couldn't find {Path.GetFileName(path)}. It was removed from the recent list.", IsError: true));
            AppSettings.Current.RemoveRecent(path);
            RefreshRecent();
            return;
        }

        var full = Path.GetFullPath(path);
        if (_tabs.FirstOrDefault(t => string.Equals(t.Tab.Session.FilePath, full, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            TabStrip.SelectedItem = open;
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
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                Log.Write($"Open {full} failed: {ex}");
                Prompt.Error(this, $"Couldn't open {Path.GetFileName(full)}",
                    $"The file may be damaged or in a format PdfDesk can't read.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
                return;
            }
        }
    }

    public void OpenSession(DocumentSession session)
    {
        var tab = new DocumentTab(session) { Visibility = Visibility.Collapsed };
        var model = new TabModel(tab);
        tab.StateChanged += (_, _) =>
        {
            model.Refresh();
            if (ActiveTab == tab) UpdateUi();
        };
        tab.Toast += (_, t) => ShowToast(t);
        DocsHost.Children.Add(tab);
        _tabs.Add(model);
        TabStrip.SelectedItem = model;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => tab.PageView.Focus());
    }

    private void ShowActiveTab()
    {
        var active = ActiveTab;
        foreach (var t in Tabs) t.Visibility = t == active ? Visibility.Visible : Visibility.Collapsed;
        if (TabStrip.SelectedItem is not null) TabStrip.ScrollIntoView(TabStrip.SelectedItem);
        UpdateUi();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusDocument);
    }

    /// <summary>Puts keyboard focus where the user works: the organizer grid, the page view, or the window.</summary>
    private void FocusDocument()
    {
        if (ActiveTab is { } t) t.FocusContent();
        else Focus();
    }

    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Key == Key.Escape && _fullScreen)
        {
            // Bubbling: only when nothing inside (find box, editor, organizer) used Esc first.
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Tab && _tabs.Count > 1)
        {
            var i = TabStrip.SelectedIndex;
            TabStrip.SelectedIndex = (i + (shift ? -1 : 1) + _tabs.Count) % _tabs.Count;
            e.Handled = true;
        }
        else if (ctrl && !shift && e.Key is >= Key.D1 and <= Key.D9 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            // Ctrl+Alt+1..9 picks a tab (Ctrl+digits are zoom/layout shortcuts).
            var n = e.Key == Key.D9 ? _tabs.Count - 1 : e.Key - Key.D1;
            if (n < _tabs.Count) TabStrip.SelectedIndex = n;
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            // F6 / Shift+F6: move between the command bar, the side pane and the document.
            if (IsInside(Keyboard.FocusedElement as DependencyObject, CommandBar)) FocusDocument();
            else if (FindChild<System.Windows.Controls.Primitives.ButtonBase>(DocCommands.Visibility == Visibility.Visible ? DocCommands : CommandBar) is { } first)
                first.Focus();
            e.Handled = true;
        }
    }

    private static bool IsInside(DependencyObject? d, DependencyObject container)
    {
        for (var x = d; x is not null; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x == container) return true;
        return false;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t && (c is not UIElement u || u.IsVisible && u.IsEnabled)) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is { } t) CloseTab(t);
        e.Handled = true;
    }

    /// <summary>Closes a tab, offering to save unsaved changes. False if the user cancelled.</summary>
    private bool CloseTab(TabModel model)
    {
        var tab = model.Tab;
        if (tab.IsBusy) return false;
        if (tab.Session.IsDirty)
        {
            TabStrip.SelectedItem = model;
            switch (Prompt.SaveChanges(this, tab.Session.DisplayName))
            {
                case SaveChoice.Cancel:
                    return false;
                case SaveChoice.Save when !Save(tab):
                    return false;
            }
        }
        var index = _tabs.IndexOf(model);
        _tabs.Remove(model);
        DocsHost.Children.Remove(tab);
        tab.Dispose();
        if (_tabs.Count > 0) TabStrip.SelectedItem = _tabs[Math.Clamp(index, 0, _tabs.Count - 1)];
        UpdateUi();
        return true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var open = _tabs.Where(t => t.Tab.Session.FilePath is not null).Select(t => t.Tab.Session.FilePath!).ToList();
        if (_tabs.FirstOrDefault(t => t.Tab.IsBusy) is { } busy)
        {
            TabStrip.SelectedItem = busy;
            ShowStatus($"Wait a moment: {busy.Title} is still being updated.");
            e.Cancel = true;
            return;
        }
        if (!ConfirmUnsaved(_tabs.ToList()))
        {
            e.Cancel = true;
            return;
        }
        foreach (var model in _tabs.ToList()) Discard(model);
        AppSettings.Current.OpenTabs = open;
        SavePlacement();
    }

    /// <summary>Asks about each unsaved document first; nothing closes unless every answer allows it.</summary>
    private bool ConfirmUnsaved(IEnumerable<TabModel> models)
    {
        foreach (var model in models.Where(m => m.Tab.Session.IsDirty))
        {
            TabStrip.SelectedItem = model;
            switch (Prompt.SaveChanges(this, model.Tab.Session.DisplayName))
            {
                case SaveChoice.Cancel:
                    return false;
                case SaveChoice.Save when !Save(model.Tab):
                    return false;
            }
        }
        return true;
    }

    private void Discard(TabModel model)
    {
        _tabs.Remove(model);
        DocsHost.Children.Remove(model.Tab);
        model.Tab.Dispose();
    }

    /// <summary>On startup: reopen last session's documents if the user asked for that.</summary>
    public void ReopenLastSession()
    {
        if (AppSettings.Current.ReopenTabs) OpenFiles(AppSettings.Current.OpenTabs.Where(File.Exists).ToList());
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
            ShowToast(new Toast($"Saved {Path.GetFileName(path)}", "Show in folder", () => Shell.ShowInFolder(path)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Save {path} failed: {ex}");
            Prompt.Error(this, $"Couldn't save {Path.GetFileName(path)}",
                $"{ex.Message}{Environment.NewLine}{Environment.NewLine}If another program has the file open, close it there, or use Save As to save a copy.");
            return false;
        }
    }

    // ───────────────────────────── Drag and drop ─────────────────────────────

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        // Thumbnails and the organizer handle their own drops (insert pages); elsewhere, files open in tabs.
        if (e.Handled)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            e.Effects = DragDropEffects.Copy;
            DropText.Text = files.Length == 1 ? $"Drop to open {Path.GetFileName(files[0])}" : $"Drop to open {files.Length} files";
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
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

    // ───────────────────────────── Recent files ─────────────────────────────

    private void RefreshRecent()
    {
        var recent = AppSettings.Current.RecentFiles.ToList();
        var items = recent.Select(p => new RecentItem(p, Describe(p))).ToList();
        RecentList.ItemsSource = items;
        RecentHeader.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoRecent.Visibility = recent.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        LoadRecentThumbnails(items);

        RecentMenu.Items.Clear();
        for (var i = 0; i < recent.Count; i++)
        {
            var path = recent[i];
            var mi = new MenuItem { Header = $"_{(i + 1) % 10} {Path.GetFileName(path).Replace("_", "__")}", ToolTip = path };
            mi.Click += (_, _) => OpenFile(path);
            RecentMenu.Items.Add(mi);
        }
        if (recent.Count > 0)
        {
            RecentMenu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "_Clear Recent" };
            clear.Click += OnClearRecent;
            RecentMenu.Items.Add(clear);
        }
        RecentMenu.IsEnabled = recent.Count > 0;
    }

    private static string Describe(string path)
    {
        var when = AppSettings.Current.Files.TryGetValue(path, out var s) && s.LastOpened != default ? Ago(s.LastOpened) : null;
        var folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        return when is null ? folder : $"{when} · {folder}";

        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            return d.TotalMinutes < 1 ? "Just now"
                : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
                : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
                : d.TotalDays < 2 ? "Yesterday"
                : d.TotalDays < 7 ? $"{(int)d.TotalDays} days ago"
                : t.ToString("MMM d");
        }
    }

    /// <summary>Renders each recent file's first page in the background (cached for the session).</summary>
    private void LoadRecentThumbnails(List<RecentItem> items)
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var item in items)
            if (RecentThumbs.TryGetValue(item.Path, out var cached)) item.Thumbnail = cached;

        var todo = items.Where(i => i.Thumbnail is null).ToList();
        if (todo.Count == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var item in todo)
            {
                try
                {
                    if (!File.Exists(item.Path)) continue;
                    using var engine = PdfEngine.OpenPreview(item.Path);
                    if (engine is null || engine.PageCount == 0) continue;
                    var g = engine.GetPage(0);
                    var scale = Math.Min(140 / g.Width, 170 / g.Height) * dpi;
                    var bmp = PageViewer.ToBitmap(engine.Render(0, scale));
                    Dispatcher.BeginInvoke(() =>
                    {
                        RecentThumbs[item.Path] = bmp;
                        item.Thumbnail = bmp;
                    });
                }
                catch (Exception ex)
                {
                    Log.Write($"Preview of {item.Path} failed: {ex.Message}");
                }
            }
        });
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) OpenFile(path);
    }

    private void OnRecentMenuOpen(object sender, RoutedEventArgs e) => OnRecentClick(sender, e);

    private void OnRecentMenuShow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) Shell.ShowInFolder(path);
    }

    private void OnRecentMenuRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        AppSettings.Current.RemoveRecent(path);
        RefreshRecent();
    }

    private void OnClearRecent(object sender, RoutedEventArgs e)
    {
        var previous = AppSettings.Current.RecentFiles.ToList();
        AppSettings.Current.RecentFiles.Clear();
        AppSettings.Current.Save();
        RefreshRecent();
        ShowToast(new Toast("Recent list cleared", "Undo", () =>
        {
            AppSettings.Current.RecentFiles = previous;
            AppSettings.Current.Save();
            RefreshRecent();
        }));
    }

    // ───────────────────────────── Commands ─────────────────────────────

    private void Bind(ICommand command, Action<DocumentTab> execute, Func<DocumentTab, bool>? can = null) =>
        CommandBindings.Add(new CommandBinding(command,
            (_, e) => { if (ActiveTab is { } t) execute(t); e.Handled = true; },
            (_, e) => { e.CanExecute = ActiveTab is { IsBusy: false } t && (can?.Invoke(t) ?? true); e.Handled = true; }));

    private void BindAlways(ICommand command, Action execute) =>
        CommandBindings.Add(new CommandBinding(command, (_, e) => { execute(); e.Handled = true; }, (_, e) => { e.CanExecute = true; e.Handled = true; }));

    private void BindEdit(ICommand command, Func<DocumentTab, Task> run, Func<DocumentTab, bool>? can = null) =>
        Bind(command, t => _ = run(t), t => t.Session.IsPdf && t.PageView.PageCount > 0 && (can?.Invoke(t) ?? true));

    private void BindCommands()
    {
        BindAlways(Cmd.Open, ShowOpenDialog);
        BindAlways(Cmd.Combine, () => _ = Organize.Combine(this, this));
        BindAlways(Cmd.Exit, Close);
        BindAlways(Cmd.CommandPalette, OpenPalette);
        BindAlways(Cmd.Settings, ShowSettings);
        BindAlways(Cmd.Shortcuts, () => ShortcutsWindow.Show(this, MainMenu));
        BindAlways(Cmd.FullScreen, ToggleFullScreen);
        BindAlways(Cmd.ToggleDarkMode, () => SetTheme(App.IsDark ? "Light" : "Dark"));
        BindAlways(Cmd.DarkPages, () =>
        {
            AppSettings.Current.DarkPages = !AppSettings.Current.DarkPages;
            AppSettings.Current.Save();
            foreach (var t in Tabs) t.SetDarkPages(AppSettings.Current.DarkPages);
            UpdateUi();
        });

        Bind(Cmd.Save, t => Save(t), t => t.Session.IsDirty || t.Session.FilePath is null);
        Bind(Cmd.SaveAs, t => SaveAs(t));
        Bind(Cmd.ShowInFolder, t => Shell.ShowInFolder(t.Session.FilePath!), t => t.Session.FilePath is not null);
        Bind(Cmd.CloseDoc, _ => { if (TabStrip.SelectedItem is TabModel m) CloseTab(m); });
        Bind(Cmd.CloseAll, _ =>
        {
            if (_tabs.Any(m => m.Tab.IsBusy) || !ConfirmUnsaved(_tabs.ToList())) return;
            foreach (var m in _tabs.ToList()) Discard(m);
            UpdateUi();
        });
        Bind(Cmd.Print, t => _ = FileActions.Print(this, t), t => t.PageView.PageCount > 0);
        Bind(Cmd.ExportImages, t => _ = FileActions.ExportImages(this, t, this), t => t.PageView.PageCount > 0);
        Bind(Cmd.Properties, t => _ = FileActions.ShowProperties(this, t));

        Bind(Cmd.Undo, t => t.Undo(), t => t.Session.CanUndo);
        Bind(Cmd.Redo, t => t.Redo(), t => t.Session.CanRedo);
        Bind(Cmd.Copy, CopySelection, t => t.PageView.HasSelection || Keyboard.FocusedElement is TextBox);
        Bind(Cmd.SelectAll, t =>
        {
            if (Keyboard.FocusedElement is TextBox tb) tb.SelectAll();
            else if (t.IsOrganizing) t.SelectAllPages();
            else t.PageView.SelectAll();
        });
        Bind(Cmd.Find, t => { t.SetOrganizing(false); t.ShowFindBar(); });
        Bind(Cmd.FindNext, t => t.FindNext(false));
        Bind(Cmd.FindPrevious, t => t.FindNext(true));
        Bind(Cmd.SearchPane, t => t.ShowSearchPane());
        Bind(Cmd.ToolHand, t => t.PageView.Tool = ViewerTool.Hand, t => !t.IsOrganizing);
        Bind(Cmd.ToolSelect, t => t.PageView.Tool = ViewerTool.SelectText, t => !t.IsOrganizing);
        Bind(Cmd.ToolSnapshot, t => t.PageView.Tool = ViewerTool.Snapshot, t => !t.IsOrganizing);
        Bind(Cmd.ToolAddText, t => ToggleTool(t, ViewerTool.AddText), t => !t.IsOrganizing && t.Session.IsPdf);
        Bind(Cmd.ToolCheck, t => ToggleTool(t, ViewerTool.AddCheck), t => !t.IsOrganizing && t.Session.IsPdf);
        Bind(Cmd.ToolCross, t => ToggleTool(t, ViewerTool.AddCross), t => !t.IsOrganizing && t.Session.IsPdf);
        Bind(Cmd.HighlightFields, t => t.PageView.HighlightFields = !t.PageView.HighlightFields, t => t.PageView.HasFormFields);

        Bind(Cmd.ZoomIn, t => t.PageView.ZoomStep(1), t => !t.IsOrganizing);
        Bind(Cmd.ZoomOut, t => t.PageView.ZoomStep(-1), t => !t.IsOrganizing);
        Bind(Cmd.ActualSize, t => t.PageView.SetZoom(1), t => !t.IsOrganizing);
        Bind(Cmd.FitWidth, t => t.SetZoomMode(ZoomMode.FitWidth), t => !t.IsOrganizing);
        Bind(Cmd.FitPage, t => t.SetZoomMode(ZoomMode.FitPage), t => !t.IsOrganizing);
        Bind(Cmd.LayoutSingle, t => SetLayout(t, ViewLayout.SinglePage));
        Bind(Cmd.LayoutContinuous, t => SetLayout(t, ViewLayout.Continuous));
        Bind(Cmd.LayoutFacing, t => SetLayout(t, ViewLayout.Facing));
        Bind(Cmd.LayoutCover, t => SetLayout(t, ViewLayout.FacingCover));
        Bind(Cmd.FirstPage, t => t.PageView.GoToPage(0), t => t.PageView.CurrentPage > 0);
        Bind(Cmd.PrevPage, t => t.PageView.PreviousPage(), t => t.PageView.CurrentPage > 0);
        Bind(Cmd.NextPage, t => t.PageView.NextPage(), t => t.PageView.CurrentPage < t.PageView.PageCount - 1);
        Bind(Cmd.LastPage, t => t.PageView.GoToPage(t.PageView.PageCount - 1), t => t.PageView.CurrentPage < t.PageView.PageCount - 1);
        Bind(Cmd.GoToPage, t => { t.SetOrganizing(false); t.FocusPageBox(); });
        Bind(Cmd.Back, t => t.PageView.GoBack(), t => t.PageView.CanGoBack);
        Bind(Cmd.Forward, t => t.PageView.GoForward(), t => t.PageView.CanGoForward);
        Bind(Cmd.NavPane, t =>
        {
            var show = !t.NavPaneVisible;
            foreach (var tab in Tabs) tab.SetNavPaneVisible(show);
            UpdateUi();
        });

        Bind(Cmd.Organize, t => t.SetOrganizing(!t.IsOrganizing), t => t.Session.IsPdf && t.PageView.PageCount > 0);
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

    /// <summary>Fill and Sign tools toggle: clicking the active one goes back to the hand tool.</summary>
    private void ToggleTool(DocumentTab t, ViewerTool tool)
    {
        t.PageView.Tool = t.PageView.Tool == tool ? ViewerTool.Hand : tool;
        if (t.PageView.Tool == tool) ShowStatus(tool switch
        {
            ViewerTool.AddText => "Click on the page where the text should go, then type",
            ViewerTool.AddCheck => "Click where each check mark should go; click a mark again to remove it",
            _ => "Click where each cross should go; click one again to remove it",
        });
        UpdateUi();
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
        catch (COMException ex)
        {
            ShowToast(new Toast($"Couldn't copy: {ex.Message}", IsError: true));
        }
    }

    private void SetLayout(DocumentTab t, ViewLayout layout)
    {
        t.SetOrganizing(false);
        t.PageView.SetLayout(layout);
        AppSettings.Current.Layout = layout.ToString();
        AppSettings.Current.Save();
        UpdateUi();
    }

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Palette.Visibility == Visibility.Visible)
        {
            ClosePalette();
            e.Handled = true;
        }
    }

    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        TitleBar.Visibility = CommandBar.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        Root.RowDefinitions[0].Height = new GridLength(_fullScreen ? 0 : 40);
        // No title-bar drag strip over the page in full screen.
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = _fullScreen ? 0 : 40;
        if (_fullScreen)
        {
            _preFullScreenState = WindowState;
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // re-maximize so the taskbar is covered
            WindowState = WindowState.Maximized;
            ShowStatus("Full screen · press Esc or F11 to leave");
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _preFullScreenState;
        }
        FitToScreenWhenMaximized();
    }

    // ───────────────────────────── Command palette ─────────────────────────────

    private void OpenPalette()
    {
        _paletteAll = BuildPaletteItems();
        Palette.Visibility = Visibility.Visible;
        PaletteBox.Text = "";
        FilterPalette();
        PaletteBox.Focus();
    }

    private void ClosePalette(bool focus = true)
    {
        Palette.Visibility = Visibility.Collapsed;
        if (focus) FocusDocument();
    }

    /// <summary>Every menu command (so the palette never drifts from the menus), plus recent files.</summary>
    private List<PaletteItem> BuildPaletteItems()
    {
        var items = new List<PaletteItem>();
        void Walk(ItemsControl parent, string category)
        {
            foreach (var mi in parent.Items.OfType<MenuItem>())
            {
                if (mi == RecentMenu) continue;
                var name = (mi.Header as string ?? (mi.Command as RoutedUICommand)?.Text ?? "").Replace("_", "").TrimEnd('…', '.');
                if (mi.Items.Count > 0)
                {
                    Walk(mi, name);
                    continue;
                }
                if (name.Length == 0 || items.Exists(i => i.Name == name)) continue;
                if (mi.Command is RoutedUICommand cmd)
                {
                    if (!cmd.CanExecute(null, this)) continue; // only offer what can run right now
                    var gesture = cmd.InputGestures.OfType<KeyGesture>().FirstOrDefault()?.GetDisplayStringForCulture(System.Globalization.CultureInfo.CurrentCulture)
                                  ?? mi.InputGestureText ?? "";
                    items.Add(new PaletteItem(name, category, gesture, "", () => { if (cmd.CanExecute(null, this)) cmd.Execute(null, this); }));
                }
                else
                {
                    var item = mi;
                    items.Add(new PaletteItem(name, category, "", "", () => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent))));
                }
            }
        }
        Walk(MainMenu, "");
        foreach (var path in AppSettings.Current.RecentFiles)
        {
            var p = path;
            items.Add(new PaletteItem(Path.GetFileName(p), "Open recent", "", "", () => OpenFile(p)));
        }
        return items;
    }

    private void FilterPalette()
    {
        var q = PaletteBox.Text.Trim();
        List<PaletteItem> result;
        if (q.Length == 0)
        {
            result = [.. _paletteAll.Where(i => i.Category != "Open recent").Take(60)];
        }
        else
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            result = [.. _paletteAll
                .Select(i => (Item: i, Score: Score(i, words)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Item)
                .Take(60)];
        }
        if (int.TryParse(q, out var page) && ActiveTab is { } t && page >= 1 && page <= t.PageView.PageCount)
            result.Insert(0, new PaletteItem($"Go to page {page}", "", "", "", () => { t.SetOrganizing(false); t.PageView.GoToPage(page - 1); }));
        PaletteList.ItemsSource = result;
        PaletteList.SelectedIndex = result.Count > 0 ? 0 : -1;

        static int Score(PaletteItem i, string[] words)
        {
            var hay = $"{i.Category} {i.Name}";
            var score = 0;
            foreach (var w in words)
            {
                var at = hay.IndexOf(w, StringComparison.CurrentCultureIgnoreCase);
                if (at < 0) return 0;
                score += i.Name.StartsWith(w, StringComparison.CurrentCultureIgnoreCase) ? 30 : at == 0 || hay[at - 1] == ' ' ? 20 : 10;
            }
            return score + (i.Category == "Open recent" ? 0 : 5);
        }
    }

    private void OnPaletteKey(object sender, KeyEventArgs e)
    {
        var count = PaletteList.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                PaletteList.SelectedIndex = (PaletteList.SelectedIndex + 1) % count;
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                break;
            case Key.Up when count > 0:
                PaletteList.SelectedIndex = (PaletteList.SelectedIndex - 1 + count) % count;
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                break;
            case Key.Enter:
                RunPaletteSelection();
                break;
            case Key.Escape:
                ClosePalette();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void RunPaletteSelection()
    {
        if (PaletteList.SelectedItem is not PaletteItem item) return;
        ClosePalette();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, item.Run);
    }

    // ───────────────────────────── Appearance ─────────────────────────────

    /// <summary>The moon/sun button: a small menu for the app theme and dark pages.</summary>
    private void OnAppearanceClick(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        var menu = new ContextMenu { PlacementTarget = AppearanceButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string header, bool check, Action run)
        {
            var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = check };
            mi.Click += (_, _) => run();
            return mi;
        }
        menu.Items.Add(Item("Dark mode", s.Theme == "Dark", () => SetTheme("Dark")));
        menu.Items.Add(Item("Light mode", s.Theme == "Light", () => SetTheme("Light")));
        menu.Items.Add(Item("Use my Windows setting", s.Theme is not ("Light" or "Dark"), () => SetTheme("System")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Dark pages (night reading)", s.DarkPages, () => Cmd.DarkPages.Execute(null, this)));
        menu.IsOpen = true;
    }

    private void OnThemeMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string theme }) SetTheme(theme);
    }

    private void SetTheme(string theme)
    {
        AppSettings.Current.Theme = theme;
        AppSettings.Current.Save();
        App.ApplyTheme();
        UpdateUi();
        ShowStatus(theme switch { "Dark" => "Dark mode on", "Light" => "Light mode on", _ => "Following your Windows setting" });
    }

    // ───────────────────────────── Settings ─────────────────────────────

    private void ShowSettings()
    {
        var s = AppSettings.Current;
        var dlg = new FormDialog(this, "Settings", "Save", 460);
        var theme = dlg.AddChoice("App theme", ["Use my Windows setting", "Light", "Dark"], s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 });
        var restore = dlg.AddCheck("Reopen each file at the page where I left off", s.RestorePosition);
        var reopen = dlg.AddCheck("Reopen the files I had open when I closed PdfDesk", s.ReopenTabs);
        var dark = dlg.AddCheck("Dark pages (inverted colors for night reading)", s.DarkPages);
        dlg.AddNote("To make PdfDesk your default PDF app: Windows Settings › Apps › Default apps › PdfDesk.");
        if (!dlg.ShowModal()) return;

        s.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
        s.RestorePosition = restore.IsChecked == true;
        s.ReopenTabs = reopen.IsChecked == true;
        s.DarkPages = dark.IsChecked == true;
        s.Save();
        App.ApplyTheme();
        foreach (var t in Tabs) t.SetDarkPages(s.DarkPages);
        UpdateUi();
        ShowStatus("Settings saved");
    }

    // ───────────────────────────── UI state ─────────────────────────────

    private void UpdateUi()
    {
        var t = ActiveTab;
        var has = _tabs.Count > 0;
        DocsHost.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        StartPage.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        AppTitle.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        DocCommands.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        Title = t is null ? "PdfDesk" : $"{(t.Session.IsDirty ? "● " : "")}{t.Session.DisplayName} – PdfDesk";

        var v = t?.PageView;
        HandButton.IsChecked = HandMenu.IsChecked = v?.Tool == ViewerTool.Hand;
        SelectButton.IsChecked = SelectMenu.IsChecked = v?.Tool == ViewerTool.SelectText;
        SnapshotButton.IsChecked = SnapshotMenu.IsChecked = v?.Tool == ViewerTool.Snapshot;
        AddTextButton.IsChecked = AddTextMenu.IsChecked = v?.Tool == ViewerTool.AddText;
        CheckButton.IsChecked = CheckMenu.IsChecked = v?.Tool == ViewerTool.AddCheck;
        CrossButton.IsChecked = CrossMenu.IsChecked = v?.Tool == ViewerTool.AddCross;
        HighlightFieldsMenu.IsChecked = v is { HasFormFields: true, HighlightFields: true };
        FitWidthMenu.IsChecked = v?.ZoomMode == ZoomMode.FitWidth;
        FitPageMenu.IsChecked = v?.ZoomMode == ZoomMode.FitPage;
        SingleMenu.IsChecked = v?.Layout == ViewLayout.SinglePage;
        ContinuousMenu.IsChecked = v?.Layout == ViewLayout.Continuous;
        FacingMenu.IsChecked = v?.Layout == ViewLayout.Facing;
        CoverMenu.IsChecked = v?.Layout == ViewLayout.FacingCover;
        NavPaneMenu.IsChecked = t?.NavPaneVisible ?? AppSettings.Current.NavPaneVisible;
        OrganizeButton.IsChecked = OrganizeMenu.IsChecked = t?.IsOrganizing == true;
        DarkPagesMenu.IsChecked = AppSettings.Current.DarkPages;
        ThemeSystemMenu.IsChecked = AppSettings.Current.Theme is not ("Light" or "Dark");
        ThemeLightMenu.IsChecked = AppSettings.Current.Theme == "Light";
        ThemeDarkMenu.IsChecked = AppSettings.Current.Theme == "Dark";
        AppearanceButton.Content = App.IsDark ? "\uE706" : "\uE708"; // sun in dark mode, moon in light mode

        UndoMenu.Header = t?.Session.UndoLabel is { } u ? $"_Undo {u}" : "_Undo";
        RedoMenu.Header = t?.Session.RedoLabel is { } r ? $"_Redo {r}" : "_Redo";
        UndoButton.ToolTip = t?.Session.UndoLabel is { } u2 ? $"Undo {u2} (Ctrl+Z)" : "Undo (Ctrl+Z)";
        RedoButton.ToolTip = t?.Session.RedoLabel is { } r2 ? $"Redo {r2} (Ctrl+Y)" : "Redo (Ctrl+Y)";
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

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        var dlg = new FormDialog(this, "About PdfDesk", "Close", 420);
        dlg.Add(null, new Image { Source = new BitmapImage(new Uri("pack://application:,,,/PdfDeskLogo.png")), Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Left });
        dlg.Add(null, new TextBlock { Text = $"PdfDesk {version?.ToString(3)}", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        dlg.AddNote($"Read, search, print and organize PDFs. Native {RuntimeInformation.ProcessArchitecture} build.");
        dlg.AddNote("Rendering, text and search: MuPDF (AGPL-3.0) via MuPDFCore. Page editing: PDFsharp (MIT). Personal use. Nothing you open leaves this computer.");
        dlg.ShowModal();
    }
}
