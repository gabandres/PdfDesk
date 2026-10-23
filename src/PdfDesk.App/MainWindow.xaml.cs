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
    /// <summary>The file name, or the document title when the file asks to show it (the tooltip keeps the path).</summary>
    public string Title => Tab.DisplayTitle;
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
    /// <summary>Start-page previews, valid while the file keeps this size and last-write time.</summary>
    private static readonly Dictionary<string, (long Size, DateTime Written, BitmapSource Image)> RecentThumbs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Start-page previews open files in place; saves wait for the one in progress so they never collide.</summary>
    private static readonly SemaphoreSlim PreviewGate = new(1, 1);

    private readonly Dictionary<DocumentTab, Task<bool>> _saves = [];
    private readonly HashSet<DocumentTab> _closingTabs = [];
    private readonly HashSet<string> _opening = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _forwardedWhileClosing = [];
    private int _openBusy;
    private bool _closeApproved;
    private bool _closeRunning;
    private bool _closed;
    private bool _finished;
    private bool _sessionEnded;
    private bool _sessionPromptRunning;

    public static readonly DependencyProperty TabMaxWidthProperty =
        DependencyProperty.Register(nameof(TabMaxWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(240.0));

    /// <summary>Widest a title-bar tab may be: tabs share the room left after the + button and a drag strip.</summary>
    public double TabMaxWidth
    {
        get => (double)GetValue(TabMaxWidthProperty);
        set => SetValue(TabMaxWidthProperty, value);
    }

    public MainWindow()
    {
        InitializeComponent();
        // Shortcuts shown on buttons come from the commands' gestures, so they always match the bindings.
        Cmd.AddShortcutsToTips(this);
        PaletteKeyText.Text = Cmd.GestureText(Cmd.CommandPalette);
        AppearanceButton.ToolTip = $"Appearance: dark mode, light mode, dark pages ({Cmd.GestureText(Cmd.ToggleDarkMode)} switches dark mode)";
        TabStrip.ItemsSource = _tabs;
        RestorePlacement();
        BindCommands();
        FillDrawMenu(DrawMenu);
        RefreshRecent();
        _ = ShowPrintDeskMenuAsync();

        TabArea.SizeChanged += (_, _) => LayoutTabs();
        _tabs.CollectionChanged += (_, _) => LayoutTabs();
        Activated += (_, _) => ActiveTab?.ScheduleDiskCheck();
        Application.Current.SessionEnding += OnSessionEnding;
        Closed += (_, _) =>
        {
            _closed = true;
            Application.Current.SessionEnding -= OnSessionEnding;
            if (!_finished) FinishClose();
        };

        TabStrip.SelectionChanged += (_, _) => ShowActiveTab();
        TabStrip.PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle && ItemUnder(e.OriginalSource) is { } t) _ = CloseTabAsync(t);
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
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
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
        PaletteList.MouseUp += (_, e) =>
        {
            // Only a click on an entry runs it (not the scroll bar).
            for (var d = e.OriginalSource as DependencyObject; d is not null && d != PaletteList;
                 d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ListBoxItem)
                {
                    RunPaletteSelection();
                    return;
                }
        };
        Palette.MouseDown += (_, e) =>
        {
            if (e.OriginalSource == Palette) ClosePalette();
        };
        UpdateUi();
    }

    public DocumentTab? ActiveTab => (TabStrip.SelectedItem as TabModel)?.Tab;
    public int TabCount => _tabs.Count;
    private IEnumerable<DocumentTab> Tabs => _tabs.Select(t => t.Tab);

    private static TabModel? ItemUnder(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
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

    /// <summary>Keeps the + button and a strip to drag the window visible: tabs shrink to share the rest, then scroll.</summary>
    private void LayoutTabs()
    {
        const double DragStrip = 56, TabMargin = 2, MinTab = 110, MaxTab = 240;
        if (TabArea.ActualWidth <= 0) return; // not laid out yet; SizeChanged comes next
        var plus = NewTabButton.ActualWidth + NewTabButton.Margin.Left + NewTabButton.Margin.Right;
        var progress = OpenProgress.Visibility == Visibility.Visible ? OpenProgress.Width + OpenProgress.Margin.Left : 0;
        var room = Math.Max(0, TabArea.ActualWidth - plus - progress - DragStrip);
        TabStrip.MaxWidth = room;
        TabMaxWidth = _tabs.Count == 0 ? MaxTab : Math.Clamp(Math.Floor(room / _tabs.Count) - TabMargin, MinTab, MaxTab);
        if (TabStrip.SelectedItem is not null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (TabStrip.SelectedItem is { } sel) TabStrip.ScrollIntoView(sel);
        });
    }

    private const int WM_SETTINGCHANGE = 0x001A, WM_ENDSESSION = 0x0016;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            // Windows switched light/dark. WPF re-themes itself in System mode; the frame (DWM) and our icons need a nudge,
            // a moment later so the registry already holds the new value.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (AppSettings.Current.Theme is "Light" or "Dark") return;
                foreach (Window w in Application.Current.Windows) App.ApplyFrame(w);
                UpdateUi();
            };
            timer.Start();
        }
        else if (msg == WM_ENDSESSION && wParam != IntPtr.Zero)
        {
            _sessionEnded = true; // Windows is ending the session now: close without prompts
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hwnd, string reason);

    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hwnd);

    /// <summary>
    /// Sign-out or shutdown with unsaved work: refuse (WPF ignores Cancel in Closing during session end), tell Windows why,
    /// then ask about each document. When everything is settled the window closes, so signing out again goes through.
    /// </summary>
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        if (!_tabs.Any(t => t.Tab.Session.IsDirty || t.Tab.HasPendingEdits))
        {
            RememberOpenTabs();
            SavePlacement();
            return;
        }
        e.Cancel = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        ShutdownBlockReasonCreate(hwnd, "Unsaved changes in PdfDesk");
        if (_sessionPromptRunning) return;
        _sessionPromptRunning = true;
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                BringToFront();
                if (await PrepareToCloseAll())
                {
                    _closeApproved = true;
                    Close();
                }
            }
            finally
            {
                _sessionPromptRunning = false;
                ShutdownBlockReasonDestroy(hwnd);
                OpenForwardedWhileClosing();
            }
        });
    }

    // ───────────────────────────── Toasts ─────────────────────────────

    /// <summary>
    /// Shows a short message at the bottom; with an action (e.g. Undo) it stays a little longer, and an error stays until
    /// dismissed.
    /// </summary>
    public void ShowToast(Toast toast)
    {
        var text = new TextBlock { Text = toast.Message, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        var icon = new TextBlock
        {
            Text = toast.IsError ? "" : toast.Action is not null ? "" : "",
            Style = (Style)FindResource("Icon"),
            Margin = new Thickness(0, 0, 12, 0),
        };
        // Resource references, not values: a toast still showing when the theme changes takes the new colors.
        icon.SetResourceReference(TextBlock.ForegroundProperty, toast.IsError ? "SystemFillColorCriticalBrush" : "AccentTextFillColorPrimaryBrush");
        var row = new DockPanel();
        var card = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 8, 10),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.22 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorQuarternaryBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        AutomationProperties.SetLiveSetting(card, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(card, toast.Message);

        // Errors stay until dismissed (they may need reading twice, or acting on); other toasts fade on their own.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(toast.Action is null ? 3.5 : 7) };
        var persistent = toast.IsError;
        void Dismiss()
        {
            timer.Stop();
            Toasts.Children.Remove(card);
        }
        timer.Tick += (_, _) => Dismiss();
        card.MouseEnter += (_, _) => timer.Stop();
        card.MouseLeave += (_, _) => { if (!persistent) timer.Start(); };
        if (persistent) AutomationProperties.SetLiveSetting(card, AutomationLiveSetting.Assertive);

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
        if (!persistent) timer.Start();
    }

    public void ShowStatus(string text) => ShowToast(new Toast(text));

    // ───────────────────────────── Open / close ─────────────────────────────

    public void OpenFiles(IEnumerable<string> paths) => _ = OpenFilesAsync([.. paths]);

    public void OpenFile(string path) => _ = OpenFileAsync(path);

    /// <summary>Opens files one after another (folders: the PDFs directly inside them).</summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in ExpandFolders(paths.Where(p => !string.IsNullOrWhiteSpace(p))))
        {
            if (_closed) return;
            await OpenFileAsync(path);
        }
    }

    private List<string> ExpandFolders(IEnumerable<string> paths)
    {
        const int MaxFromFolder = 30;
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
            {
                result.Add(path);
                continue;
            }
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            List<string> pdfs;
            try
            {
                pdfs = [.. Directory.EnumerateFiles(path, "*.pdf", SearchOption.TopDirectoryOnly).Order(StringComparer.CurrentCultureIgnoreCase)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowToast(new Toast($"Couldn't read the folder {name}: {ex.Message}", IsError: true));
                continue;
            }
            if (pdfs.Count == 0)
            {
                ShowToast(new Toast($"The folder {name} has no PDF files in it. Drop the files themselves to open them.", IsError: true));
                continue;
            }
            if (pdfs.Count > MaxFromFolder)
            {
                ShowToast(new Toast($"Opening the first {MaxFromFolder} of {pdfs.Count} PDFs in {name}"));
                pdfs = pdfs[..MaxFromFolder];
            }
            result.AddRange(pdfs);
        }
        return result;
    }

    /// <summary>Opens a file in a new tab, reading it off the UI thread (big files don't freeze the window).</summary>
    public async Task OpenFileAsync(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowToast(new Toast($"Couldn't open {path}: {ex.Message}", IsError: true));
            return;
        }

        if (_tabs.FirstOrDefault(t => string.Equals(t.Tab.Session.FilePath, full, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            TabStrip.SelectedItem = open;
            // Reopening a file that changed on disk: show the new version if nothing here would be lost.
            open.Tab.ScheduleDiskCheck();
            return;
        }

        if (!File.Exists(full))
        {
            var recent = AppSettings.Current.RecentFiles.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
            ShowToast(new Toast(recent ? $"Couldn't find {Path.GetFileName(full)}. It was removed from the recent list." : $"Couldn't find {Path.GetFileName(full)}.", IsError: true));
            if (recent)
            {
                AppSettings.Current.RemoveRecent(full);
                RefreshRecent();
            }
            return;
        }
        if (!_opening.Add(full)) return; // already on its way (e.g. double-clicked twice)

        SetOpenBusy(true);
        try
        {
            string? password = null;
            var wrong = false;
            while (true)
            {
                try
                {
                    var pw = password;
                    var session = await Task.Run(() => DocumentSession.Open(full, pw));
                    if (_closed)
                    {
                        session.Dispose();
                        return;
                    }
                    OpenSession(session);
                    AppSettings.Current.AddRecent(full);
                    RefreshRecent();
                    return;
                }
                catch (PasswordRequiredException ex)
                {
                    if (_closed) return;
                    password = PdfFiles.PromptPassword(this, Path.GetFileName(full), wrong || ex.WrongPassword);
                    if (password is null) return;
                    wrong = true;
                }
                catch (Exception ex)
                {
                    Log.Write($"Open {Path.GetFileName(full)} failed: {ex}");
                    if (_closed) return;
                    Prompt.Error(this, $"Couldn't open {Path.GetFileName(full)}",
                        $"The file may be damaged or in a format PdfDesk can't read.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
                    return;
                }
            }
        }
        finally
        {
            _opening.Remove(full);
            SetOpenBusy(false);
        }
    }

    private void SetOpenBusy(bool on)
    {
        _openBusy = Math.Max(0, _openBusy + (on ? 1 : -1));
        var busy = _openBusy > 0;
        OpenProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Mouse.OverrideCursor = busy ? Cursors.AppStarting : null;
        LayoutTabs();
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
        tab.SignRequested += (_, field) => { if (ActiveTab == tab && !tab.IsBusy) _ = SignDigitallyAsync(tab, field); };
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
        active?.ScheduleDiskCheck();
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
        else if (ctrl && !shift && e.Key is >= Key.D1 and <= Key.D9 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)
                 && !Keyboard.IsKeyDown(Key.RightAlt))
        {
            // Ctrl+Alt+1..9 picks a tab (Ctrl+digits are zoom/layout shortcuts). AltGr reports as Ctrl+Alt and types
            // characters such as @ # [ ] on many layouts, so leave it alone.
            var n = e.Key == Key.D9 ? _tabs.Count - 1 : e.Key - Key.D1;
            if (n < _tabs.Count) TabStrip.SelectedIndex = n;
            e.Handled = true;
        }
        else if (e.Key == Key.F6 && (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            CycleFocus(shift);
            e.Handled = true;
        }
    }

    /// <summary>F6 / Shift+F6: command bar → side pane → document (and back).</summary>
    private void CycleFocus(bool backwards)
    {
        var tab = ActiveTab;
        var zones = tab is null ? 2 : 3; // 0 command bar, 1 side pane, 2 document (no pane without a document)
        var focused = Keyboard.FocusedElement as DependencyObject;
        var current = IsInside(focused, CommandBar) ? 0 : tab?.IsPaneFocused == true ? 1 : zones - 1;
        var next = (current + (backwards ? -1 : 1) + zones) % zones;
        if (tab is null)
        {
            if (next == 0) FocusCommandBar();
            else FocusDocument();
            return;
        }
        switch (next)
        {
            case 0:
                FocusCommandBar();
                break;
            case 1:
                if (tab.IsOrganizing) tab.FocusContent(); // the organizer hides the pane
                else tab.FocusPane();
                break;
            default:
                FocusDocument();
                break;
        }
    }

    private void FocusCommandBar()
    {
        if (FindChild<System.Windows.Controls.Primitives.ButtonBase>(CommandBar) is { } first) first.Focus();
    }

    /// <summary>The "More" (…) button at the end of the command bar: the commands that didn't fit, as a menu.</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var item in DocCommands.OverflowItems)
        {
            if (item == DrawButton)
            {
                var draw = new MenuItem { Header = "_Draw", Icon = new TextBlock { Text = DrawButtonIcon.Text, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14 } };
                FillDrawMenu(draw);
                menu.Items.Add(draw);
                continue;
            }
            if (item is not System.Windows.Controls.Primitives.ButtonBase b)
            {
                if (menu.Items.Count > 0 && menu.Items[^1] is not Separator) menu.Items.Add(new Separator()); // a bar separator
                continue;
            }
            // A command shows its own name and shortcut; the icon is the button's.
            var mi = new MenuItem { Command = b.Command, CommandTarget = b.CommandTarget };
            if (b.Command is not RoutedUICommand) mi.Header = b.ToolTip as string ?? AutomationProperties.GetName(b);
            if (GlyphOf(b) is { } glyph) mi.Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14 };
            if (b is System.Windows.Controls.Primitives.ToggleButton { IsChecked: true }) mi.IsChecked = true;
            if (b.Command is null) mi.Click += (_, _) => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            menu.Items.Add(mi);
        }
        if (menu.Items.Count > 0 && menu.Items[^1] is Separator s) menu.Items.Remove(s);
        menu.IsOpen = true;
    }

    /// <summary>The icon a button shows (a Segoe Fluent Icons character), alone or before its label; null if it has none.</summary>
    private static string? GlyphOf(System.Windows.Controls.Primitives.ButtonBase b)
    {
        static bool IsGlyph(string? s) => s is { Length: 1 } && char.GetUnicodeCategory(s[0]) == System.Globalization.UnicodeCategory.PrivateUse;
        if (b.Content is string text) return IsGlyph(text) ? text : null;
        if (b.Content is Panel panel)
            foreach (var tb in panel.Children.OfType<TextBlock>())
                if (IsGlyph(tb.Text)) return tb.Text;
        return null;
    }

    /// <summary>Each command's icon from the command bar and start page buttons, for the command palette.</summary>
    private Dictionary<ICommand, string> CommandGlyphs()
    {
        var glyphs = new Dictionary<ICommand, string>();
        void Walk(DependencyObject root)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is System.Windows.Controls.Primitives.ButtonBase { Command: { } cmd } b && GlyphOf(b) is { } g) glyphs.TryAdd(cmd, g);
                Walk(child);
            }
        }
        Walk(CommandBar);
        Walk(StartPage);
        foreach (var tool in DrawTools.All) glyphs.TryAdd(Cmd.DrawTool(tool), DrawTools.Glyph(tool));
        return glyphs;
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
        if (ItemUnder(e.OriginalSource) is { } t) _ = CloseTabAsync(t);
        e.Handled = true;
    }

    /// <summary>
    /// Closes a tab: first lets a value still being typed and any edit in flight land, then offers to save. The active tab
    /// stays active when a background tab closes. False if the user cancelled.
    /// </summary>
    private async Task<bool> CloseTabAsync(TabModel model)
    {
        var tab = model.Tab;
        if (!_closingTabs.Add(tab)) return false;
        try
        {
            if (tab.IsBusy && !tab.HasPendingEdits)
            {
                // Printing or exporting: that work holds the document; closing now would pull it away.
                TabStrip.SelectedItem = model;
                ShowStatus($"Wait a moment: {tab.Session.DisplayName} is still busy.");
                return false;
            }
            var previous = TabStrip.SelectedItem as TabModel;
            var wasActive = previous == model;
            await tab.FlushPendingEditsAsync();
            if (!_tabs.Contains(model)) return true;
            if (tab.Session.IsDirty)
            {
                TabStrip.SelectedItem = model;
                var choice = Prompt.SaveChanges(this, tab.Session.DisplayName);
                if (choice == SaveChoice.Cancel || (choice == SaveChoice.Save && !await SaveAsync(tab)))
                {
                    if (!wasActive && previous is not null && _tabs.Contains(previous)) TabStrip.SelectedItem = previous;
                    return false;
                }
                if (!_tabs.Contains(model)) return true;
            }

            var index = _tabs.IndexOf(model);
            RememberClosed(tab);
            Discard(model);
            if (!wasActive && previous is not null && _tabs.Contains(previous)) TabStrip.SelectedItem = previous;
            else if (_tabs.Count > 0) TabStrip.SelectedItem = _tabs[Math.Clamp(index, 0, _tabs.Count - 1)];
            UpdateUi();
            return true;
        }
        finally
        {
            _closingTabs.Remove(tab);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved || _sessionEnded || _finished)
        {
            FinishClose();
            return;
        }
        // Saving may have to wait for a field value to land: say no now, close for real when it's settled. Started a moment
        // later, so when Windows or Application.Shutdown closes the window regardless, no prompt appears over a closed window.
        e.Cancel = true;
        if (_closeRunning) return;
        _closeRunning = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => _ = CloseWindowAsync());
    }

    private async Task CloseWindowAsync()
    {
        _closeRunning = true;
        try
        {
            if (_closed) return;
            if (!await PrepareToCloseAll() || _closed) return;
            if (_forwardedWhileClosing.Count > 0)
            {
                // Another launch asked for a file while the window was closing: keep the window for it. The documents
                // the user agreed to close still close.
                foreach (var m in _tabs.ToList()) Discard(m);
                UpdateUi();
                return;
            }
            _closeApproved = true;
            Close();
        }
        finally
        {
            _closeRunning = false;
            OpenForwardedWhileClosing();
        }
    }

    /// <summary>
    /// Files another launch forwarded ("Open with PdfDesk" while it runs). While the window asks about unsaved changes
    /// before closing they wait, so a new tab can't appear only to be closed unasked; they open once the close is cancelled
    /// or has closed the documents (see <see cref="CloseWindowAsync"/>).
    /// </summary>
    public void OpenForwarded(IReadOnlyList<string> files)
    {
        if (_closeRunning || _sessionPromptRunning)
        {
            _forwardedWhileClosing.AddRange(files);
            return;
        }
        OpenFiles(files);
    }

    private void OpenForwardedWhileClosing()
    {
        if (_forwardedWhileClosing.Count == 0 || _closed || _closeApproved || _closeRunning || _sessionPromptRunning) return;
        var files = _forwardedWhileClosing.ToList();
        _forwardedWhileClosing.Clear();
        OpenFiles(files);
    }

    /// <summary>Flushes every tab's pending edits, then asks about each unsaved document. False if anything should stay open.</summary>
    private async Task<bool> PrepareToCloseAll()
    {
        foreach (var model in _tabs.ToList())
        {
            if (model.Tab.IsBusy && !model.Tab.HasPendingEdits)
            {
                TabStrip.SelectedItem = model;
                ShowStatus($"Wait a moment: {model.Title} is still busy.");
                return false;
            }
            await model.Tab.FlushPendingEditsAsync();
            if (_closed) return false;
        }
        return await ConfirmUnsavedAsync(_tabs.ToList());
    }

    /// <summary>Asks about each unsaved document first; nothing closes unless every answer allows it.</summary>
    private async Task<bool> ConfirmUnsavedAsync(IEnumerable<TabModel> models)
    {
        foreach (var model in models)
        {
            if (!_tabs.Contains(model) || !model.Tab.Session.IsDirty) continue;
            TabStrip.SelectedItem = model;
            switch (Prompt.SaveChanges(this, model.Tab.Session.DisplayName))
            {
                case SaveChoice.Cancel:
                    return false;
                case SaveChoice.Save when !await SaveAsync(model.Tab):
                    return false;
            }
            if (_closed) return false;
        }
        return true;
    }

    /// <summary>Last step of closing the window: remember the open files (after any Save As), release everything.</summary>
    private void FinishClose()
    {
        if (_finished) return;
        _finished = true;
        RememberOpenTabs();
        foreach (var model in _tabs.ToList()) Discard(model);
        SavePlacement();
    }

    private void RememberOpenTabs() =>
        AppSettings.Current.OpenTabs = [.. _tabs.Where(t => t.Tab.Session.FilePath is not null).Select(t => t.Tab.Session.FilePath!)];

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

    // ───────────────────────────── Save ─────────────────────────────

    private bool IsSaving(DocumentTab tab) => _saves.ContainsKey(tab);

    /// <summary>
    /// Saves a document (Save As if it has no file yet). Waits for a value still being typed and any edit in flight first;
    /// a second request while one runs gets the same result instead of saving twice.
    /// </summary>
    private Task<bool> SaveAsync(DocumentTab tab, bool saveAs = false)
    {
        if (_saves.TryGetValue(tab, out var running)) return running;
        var done = new TaskCompletionSource<bool>();
        _saves[tab] = done.Task; // registered before anything runs, so Ctrl+S during the save can't start another
        UpdateUi();
        _ = Run();
        return done.Task;

        async Task Run()
        {
            var ok = false;
            try
            {
                ok = await SaveCoreAsync(tab, saveAs);
            }
            catch (Exception ex) // top-level UI action: report any failure, never lose it
            {
                Log.Write($"Save {tab.Session.DisplayName} failed: {ex}");
                Prompt.Error(this, $"Couldn't save {tab.Session.DisplayName}", ex.Message);
            }
            finally
            {
                _saves.Remove(tab);
                UpdateUi();
                done.TrySetResult(ok);
            }
        }
    }

    private async Task<bool> SaveCoreAsync(DocumentTab tab, bool saveAs)
    {
        try
        {
            if (tab.HasPendingEdits || tab.IsBusy) ShowStatus($"Saving {tab.Session.DisplayName} after the current change is written…");
            await tab.FlushPendingEditsAsync();
            if (tab.IsDisposed) return false;
            var s = tab.Session;
            if (saveAs || s.FilePath is null || !s.IsPdf) return await SaveAsDialogAsync(tab);
            if (!s.IsDirty) return true; // nothing new (e.g. the open field held the value it already had)

            var path = s.FilePath;
            if (!s.IsMissingOnDisk() && s.HasChangedOnDisk())
            {
                switch (AskChangedOnDisk(s.DisplayName))
                {
                    case DiskChoice.Cancel:
                        return false;
                    case DiskChoice.SaveAs:
                        return await SaveAsDialogAsync(tab);
                }
            }
            return await WriteToAsync(tab, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Save {tab.Session.DisplayName} failed: {ex}");
            Prompt.Error(this, $"Couldn't save {tab.Session.DisplayName}", ex.Message);
            return false;
        }
    }

    private enum DiskChoice { Overwrite, SaveAs, Cancel }

    private DiskChoice AskChangedOnDisk(string name)
    {
        var dlg = new FormDialog(this, $"{name} changed on disk", "Overwrite", 460);
        dlg.AddNote("Another program changed this file after you opened it. Saving now replaces those changes with your version.");
        var choice = DiskChoice.Cancel;
        var saveAs = dlg.Add(null, new Button { Content = "Save as a new file instead…", Style = (Style)FindResource("TextButton"), HorizontalAlignment = HorizontalAlignment.Left });
        saveAs.Click += (_, _) =>
        {
            choice = DiskChoice.SaveAs;
            dlg.DialogResult = false;
        };
        if (dlg.ShowModal()) choice = DiskChoice.Overwrite;
        return choice;
    }

    private async Task<bool> SaveAsDialogAsync(DocumentTab tab, string? suggestedName = null)
    {
        var s = tab.Session;
        var ext = s.IsPdf ? ".pdf" : Path.GetExtension(s.DisplayName);
        var dlg = new SaveFileDialog
        {
            Title = "Save As",
            FileName = suggestedName ?? Path.GetFileNameWithoutExtension(s.DisplayName) + ext,
            Filter = s.IsPdf ? PdfFiles.PdfFilter : $"{ext.TrimStart('.').ToUpperInvariant()} files (*{ext})|*{ext}",
            DefaultExt = ext,
            AddExtension = true,
            InitialDirectory = Path.GetDirectoryName(s.FilePath) ?? AppSettings.Current.LastFolder,
        };
        string? picked = DevScript.TakeSavePath(out var scripted) ? scripted : dlg.ShowDialog(this) == true ? dlg.FileName : null;
        if (picked is null || tab.IsDisposed) return false;
        var path = Path.GetFullPath(picked);

        // Two tabs must never share a file: the other tab's copy would silently go stale (and could overwrite this one).
        var other = _tabs.FirstOrDefault(t => t.Tab != tab && string.Equals(t.Tab.Session.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (other is not null)
        {
            if (other.Tab.IsBusy || other.Tab.HasPendingEdits || IsSaving(other.Tab))
            {
                Prompt.Info(this, $"{other.Title} is busy in another tab", "Wait until it's done, or choose another name.");
                return false;
            }
            var lose = other.Tab.Session.IsDirty ? " Its unsaved changes will be lost." : "";
            if (!Prompt.Confirm(this, $"{other.Title} is open in another tab",
                    $"Saving here replaces that file, so its tab will be closed.{lose}", "Replace and close it", warning: true))
                return false;
        }

        if (!await WriteToAsync(tab, path)) return false;
        if (other is not null && _tabs.Contains(other))
        {
            var active = TabStrip.SelectedItem;
            Discard(other);
            if (active is TabModel a && _tabs.Contains(a)) TabStrip.SelectedItem = a;
            UpdateUi();
        }
        return true;
    }

    private async Task<bool> WriteToAsync(DocumentTab tab, string path)
    {
        await PreviewGate.WaitAsync(); // a start-page preview may have this very file open right now
        try
        {
            if (tab.IsDisposed) return false;
            // Written on a worker thread (big files don't freeze the window); busy meanwhile, so no edit or close cuts in.
            using var busy = tab.BeginBusy($"Saving {Path.GetFileName(path)}…");
            await tab.Session.SaveAsAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Save {Path.GetFileName(path)} failed: {ex}");
            Prompt.Error(this, $"Couldn't save {Path.GetFileName(path)}",
                $"{ex.Message}{Environment.NewLine}{Environment.NewLine}If another program has the file open, close it there, or use Save As to save a copy.");
            return false;
        }
        finally
        {
            PreviewGate.Release();
        }
        tab.NoteSaved();
        lock (RecentThumbs) RecentThumbs.Remove(path); // the start page shows the new first page
        AppSettings.Current.AddRecent(path);
        RefreshRecent();
        ShowToast(new Toast($"Saved {Path.GetFileName(path)}", "Show in folder", () => Shell.ShowInFolder(path)));
        return true;
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
            DropText.Text = files.Length > 1 ? $"Drop to open {files.Length} files"
                : Directory.Exists(files[0]) ? $"Drop to open the PDFs in {Path.GetFileName(Path.TrimEndingDirectorySeparator(files[0]))}"
                : $"Drop to open {Path.GetFileName(files[0])}";
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
        // Open after the drop completes: a dialog shown inside the handler would leave Explorer hanging mid-drag.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Activate();
            if (FileTools.HandleWindowDrop(this, files)) return; // images: a new PDF of them, or pages of this one
            OpenFiles(files);
        });
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

    /// <summary>
    /// Renders each recent file's first page in the background (cached while the file is unchanged). Each preview opens the file only
    /// while it renders, one at a time, and never while a save is writing (see <see cref="PreviewGate"/>).
    /// </summary>
    private void LoadRecentThumbnails(List<RecentItem> items)
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        lock (RecentThumbs)
        {
            // Files no longer in the list don't keep a preview in memory.
            var listed = items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in RecentThumbs.Keys.Where(k => !listed.Contains(k)).ToList()) RecentThumbs.Remove(gone);
            // Shown at once; checked against the file below and redrawn if it changed since.
            foreach (var item in items)
                if (RecentThumbs.TryGetValue(item.Path, out var cached)) item.Thumbnail = cached.Image;
        }
        if (items.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var item in items)
            {
                if (_closed) return;
                await PreviewGate.WaitAsync();
                try
                {
                    var info = new FileInfo(item.Path);
                    if (!info.Exists) continue;
                    var (size, written) = (info.Length, info.LastWriteTimeUtc);
                    lock (RecentThumbs)
                        if (RecentThumbs.TryGetValue(item.Path, out var cached) && cached.Size == size && cached.Written == written) continue;
                    BitmapSource bmp;
                    using (var engine = PdfEngine.OpenPreview(item.Path))
                    {
                        if (engine is null || engine.PageCount == 0) continue;
                        var g = engine.GetPage(0);
                        var scale = Math.Min(140 / g.Width, 170 / g.Height) * dpi;
                        bmp = PageViewer.ToBitmap(engine.Render(0, scale));
                    }
                    lock (RecentThumbs) RecentThumbs[item.Path] = (size, written, bmp);
                    _ = Dispatcher.BeginInvoke(() => item.Thumbnail = bmp);
                }
                catch (Exception ex)
                {
                    Log.Write($"Preview of {Path.GetFileName(item.Path)} failed: {ex.Message}");
                }
                finally
                {
                    PreviewGate.Release();
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
        var snapshot = AppSettings.Current.ClearRecent();
        lock (RecentThumbs) RecentThumbs.Clear();
        RefreshRecent();
        ShowToast(new Toast("Recent list cleared", "Undo", () =>
        {
            AppSettings.Current.RestoreRecent(snapshot);
            RefreshRecent();
        }));
    }

    // ───────────────────────────── Commands ─────────────────────────────

    private void Bind(ICommand command, Action<DocumentTab> execute, Func<DocumentTab, bool>? can = null) =>
        CommandBindings.Add(new CommandBinding(command,
            (_, e) => { if (ActiveTab is { } t) execute(t); e.Handled = true; },
            (_, e) => { e.CanExecute = ActiveTab is { IsBusy: false } t && (can?.Invoke(t) ?? true); e.Handled = true; }));

    /// <summary>Like <see cref="Bind"/>, but also available while the tab is busy (the action waits for it).</summary>
    private void BindTab(ICommand command, Action<DocumentTab> execute, Func<DocumentTab, bool>? can = null) =>
        CommandBindings.Add(new CommandBinding(command,
            (_, e) => { if (ActiveTab is { } t) execute(t); e.Handled = true; },
            (_, e) => { e.CanExecute = ActiveTab is { IsDisposed: false } t && (can?.Invoke(t) ?? true); e.Handled = true; }));

    private async Task CloseAllAsync()
    {
        var models = _tabs.ToList(); // a file opened while the prompts are up stays open
        if (!await PrepareToCloseAll()) return;
        foreach (var m in models.Where(_tabs.Contains))
        {
            RememberClosed(m.Tab);
            Discard(m);
        }
        UpdateUi();
    }

    private void BindAlways(ICommand command, Action execute) =>
        CommandBindings.Add(new CommandBinding(command, (_, e) => { execute(); e.Handled = true; }, (_, e) => { e.CanExecute = true; e.Handled = true; }));

    private void BindEdit(ICommand command, Func<DocumentTab, Task> run, Func<DocumentTab, bool>? can = null) =>
        Bind(command, t => _ = run(t), t => t.Session.IsPdf && t.PageView.PageCount > 0 && (can?.Invoke(t) ?? true));

    private void BindCommands()
    {
        BindAlways(Cmd.Open, ShowOpenDialog);
        BindToolCommands(); // MainWindow.Tools.cs
        BindAlways(Cmd.Combine, () => _ = Organize.Combine(this, this));
        BindAlways(Cmd.Exit, Close);
        BindAlways(Cmd.CommandPalette, OpenPalette);
        BindAlways(Cmd.Settings, ShowSettings);
        BindAlways(Cmd.Shortcuts, () => ShortcutsWindow.Show(this, MainMenu));
        BindAlways(Cmd.FullScreen, ToggleFullScreen);
        BindAlways(Cmd.ToggleDarkMode, () => SetTheme(App.IsDark ? "Light" : "Dark"));
        CommandBindings.Add(new CommandBinding(Cmd.NextTab, (_, e) => { SelectTabBy(1); e.Handled = true; },
            (_, e) => { e.CanExecute = _tabs.Count > 1; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(Cmd.PrevTab, (_, e) => { SelectTabBy(-1); e.Handled = true; },
            (_, e) => { e.CanExecute = _tabs.Count > 1; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(Cmd.ReopenClosed, (_, e) => { _ = ReopenClosedAsync(); e.Handled = true; },
            (_, e) => { e.CanExecute = _closedTabs.Count > 0; e.Handled = true; }));
        BindAlways(Cmd.DarkPages, () =>
        {
            AppSettings.Current.DarkPages = !AppSettings.Current.DarkPages;
            AppSettings.Current.Save();
            foreach (var t in Tabs) t.SetDarkPages(AppSettings.Current.DarkPages);
            UpdateUi();
        });

        // Save works while a field value is still being written (it waits for it) and when the only change is the value in
        // the open field editor; a save already running isn't started twice.
        BindTab(Cmd.Save, t => _ = SaveAsync(t),
            t => !IsSaving(t) && (t.Session.IsDirty || t.Session.FilePath is null || t.HasPendingEdits));
        BindTab(Cmd.SaveAs, t => _ = SaveAsync(t, saveAs: true), t => !IsSaving(t));
        Bind(Cmd.ShowInFolder, t => Shell.ShowInFolder(t.Session.FilePath!), t => t.Session.FilePath is not null);
        BindTab(Cmd.CloseDoc, t => { if (_tabs.FirstOrDefault(m => m.Tab == t) is { } m) _ = CloseTabAsync(m); });
        BindTab(Cmd.CloseAll, t => _ = CloseAllAsync());
        Bind(Cmd.ReloadFromDisk, t =>
        {
            if (t.Session.IsMissingOnDisk())
            {
                ShowToast(new Toast($"{t.Session.DisplayName} is no longer on disk; use Save As to keep this copy.", IsError: true));
                return;
            }
            if ((t.Session.IsDirty || t.HasPendingEdits) && !Prompt.Confirm(this, $"Reload {t.Session.DisplayName}?",
                    "Your unsaved changes will be discarded and the file reopened as it is on disk.", "Reload", warning: true))
                return;
            _ = t.ReloadFromDiskAsync($"Reloaded {t.Session.DisplayName}");
        }, t => t.Session.FilePath is not null && !IsSaving(t));
        Bind(Cmd.Print, t => _ = FileActions.Print(this, t), t => t.PageView.PageCount > 0);
        BindAlways(Cmd.Scan, () => Scanning.ScanToNewTab(this));
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
        Bind(Cmd.AddSignature, t => ShowSignatureMenu(t), t => t.Session.IsPdf && t.PageView.PageCount > 0);
        Bind(Cmd.Sign, t => _ = SignDigitallyAsync(t, null), t => t.Session.IsPdf && t.PageView.PageCount > 0 && !_signing);
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
        Bind(Cmd.RotateViewRight, t => t.PageView.RotateView(clockwise: true), t => !t.IsOrganizing && t.PageView.PageCount > 0);
        Bind(Cmd.RotateViewLeft, t => t.PageView.RotateView(clockwise: false), t => !t.IsOrganizing && t.PageView.PageCount > 0);
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
        BindEdit(Cmd.RotateRight, t => Organize.RotateSelected(t, 90), Organize.HasSelection);
        BindEdit(Cmd.RotateLeft, t => Organize.RotateSelected(t, -90), Organize.HasSelection);
        BindEdit(Cmd.RotatePages, t => Organize.RotateDialog(this, t));
        BindEdit(Cmd.DeletePages, t => Organize.DeleteDialog(this, t), t => t.PageView.PageCount > 1);
        BindEdit(Cmd.DeleteSelected, Organize.DeleteSelected, t => Organize.HasSelection(t) && t.SelectedPages.Length < t.PageView.PageCount);
        BindEdit(Cmd.InsertPages, t => Organize.InsertFromFile(this, t));
        BindEdit(Cmd.InsertBlank, t => Organize.InsertBlank(this, t));
        BindEdit(Cmd.InsertFromScanner, t => Scanning.InsertFromScanner(this, t));
        BindEdit(Cmd.ExtractPages, t => Organize.Extract(this, t, this));
        BindEdit(Cmd.SplitDocument, t => Organize.Split(this, t, this), t => t.PageView.PageCount > 1);
        BindEdit(Cmd.MovePages, t => Organize.Move(this, t), t => t.PageView.PageCount > 1);
        BindEdit(Cmd.DuplicatePages, Organize.Duplicate, Organize.HasSelection);
        BindEdit(Cmd.ReversePages, Organize.Reverse, t => t.PageView.PageCount > 1);
        BindEdit(Cmd.CropPages, t => Organize.Crop(this, t));

        // Review: panes, text markup, notes, redaction
        Bind(Cmd.ShowComments, t => t.ShowReviewPane(ReviewPane.Comments), t => t.Session.IsPdf);
        Bind(Cmd.ShowAttachments, t => t.ShowReviewPane(ReviewPane.Attachments), t => t.Session.IsPdf);
        Bind(Cmd.ShowLayers, t => t.ShowReviewPane(ReviewPane.Layers), t => t.LayerList.Count > 0);
        Bind(Cmd.ShowSignatures, t => t.ShowReviewPane(ReviewPane.Signatures), t => t.SignatureResults.Count > 0);
        Bind(Cmd.Highlight, t => _ = t.MarkupSelection(TextMarkupKind.Highlight), t => t.CanMarkup);
        Bind(Cmd.Underline, t => _ = t.MarkupSelection(TextMarkupKind.Underline), t => t.CanMarkup);
        Bind(Cmd.StrikeOut, t => _ = t.MarkupSelection(TextMarkupKind.StrikeOut), t => t.CanMarkup);
        Bind(Cmd.AddNote, t => ToggleTool(t, ViewerTool.AddNote), t => !t.IsOrganizing && t.Session.IsPdf);
        Bind(Cmd.Redact, t => ToggleTool(t, ViewerTool.Redact), t => !t.IsOrganizing && t.Session.IsPdf);
        foreach (var tool in DrawTools.All)
            Bind(Cmd.DrawTool(tool), t => ToggleTool(t, tool), t => !t.IsOrganizing && t.Session.IsPdf);
        Bind(Cmd.RecognizeText, t => _ = t.RecognizeText(), t => t.Session.IsPdf);
    }

    /// <summary>File › Printer Settings (PrintDesk): shown only when PrintDesk is installed (checked off the UI thread).</summary>
    private async Task ShowPrintDeskMenuAsync()
    {
        var installed = await Task.Run(() => Desk.PrintDeskPath is not null);
        PrintDeskMenu.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenPrintDesk(object sender, RoutedEventArgs e)
    {
        if (!Desk.OpenPrintDesk())
        {
            PrintDeskMenu.Visibility = Visibility.Collapsed;
            ShowToast(new Toast("PrintDesk couldn't be started. Is it still installed?", IsError: true));
        }
    }

    private void OnAttachFile(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { IsBusy: false, Session.IsPdf: true } t) _ = t.AddAttachment();
    }

    private async void OnRedactText(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { IsBusy: false } t && await t.RedactSelection() == 0) ShowStatus("Select the text to redact first (Select text tool, T)");
    }

    private void OnRedactHits(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { IsBusy: false } t && t.RedactSearchHits() == 0) ShowStatus("Search first (Ctrl+F or the Search pane), then mark the matches");
    }

    private void OnApplyRedactions(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { IsBusy: false } t) _ = t.ApplyRedactions();
    }

    /// <summary>The drawing tools as menu items (the Review menu's Draw submenu, the Draw button's dropdown, the More menu).</summary>
    private void FillDrawMenu(ItemsControl menu)
    {
        menu.Items.Clear();
        var current = ActiveTab?.PageView.Tool;
        foreach (var tool in DrawTools.All)
        {
            menu.Items.Add(new MenuItem
            {
                Command = Cmd.DrawTool(tool),
                InputGestureText = DrawTools.KeyOf(tool).ToString(),
                Icon = new TextBlock { Text = DrawTools.Glyph(tool), FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14 },
                IsChecked = current == tool,
            });
            if (tool is ViewerTool.Highlighter or ViewerTool.Arrow) menu.Items.Add(new Separator());
        }
    }

    private void OnDrawClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = DrawButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        FillDrawMenu(menu);
        menu.Closed += (_, _) => UpdateUi();
        menu.IsOpen = true;
        UpdateUi(); // the button shows whether a drawing tool is on, not the click
    }

    /// <summary>Fill and Sign tools toggle: clicking the active one goes back to the hand tool.</summary>
    private void ToggleTool(DocumentTab t, ViewerTool tool)
    {
        t.PageView.Tool = t.PageView.Tool == tool ? ViewerTool.Hand : tool;
        if (t.PageView.Tool == tool) ShowStatus(tool switch
        {
            ViewerTool.AddText => "Click on the page where the text should go, then type",
            ViewerTool.AddCheck => "Click where each check mark should go; click a mark again to remove it",
            ViewerTool.AddNote => "Click on the page where the note should go",
            ViewerTool.Redact => "Drag over the areas to black out; nothing is removed until you apply the redactions",
            ViewerTool.Pen or ViewerTool.Highlighter => "Draw on the page with the mouse, pen or a finger; Esc goes back to the hand tool",
            ViewerTool.Rectangle or ViewerTool.Ellipse => "Drag to draw; Shift makes a square or circle. Esc goes back to the hand tool",
            ViewerTool.Line or ViewerTool.Arrow => "Drag to draw; Shift keeps it horizontal, vertical or at 45°. Esc goes back to the hand tool",
            ViewerTool.TextBox => "Click to type a line, or drag a box for wrapped text",
            ViewerTool.Callout => "Drag from what the callout points at to where its text goes",
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
        // Long selections are read in the background; the viewer reports the outcome (a toast via the tab).
        _ = t.PageView.CopySelectionAsync();
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
            return;
        }
        // Tab switching wins over the focused control (the page view scrolls on Ctrl+PgDn, lists move on Ctrl+Tab).
        if (Palette.Visibility == Visibility.Visible) return;
        foreach (var cmd in new[] { Cmd.NextTab, Cmd.PrevTab })
        {
            if (!cmd.InputGestures.OfType<KeyGesture>().Any(g => g.Matches(this, e))) continue;
            if (cmd.CanExecute(null, this)) cmd.Execute(null, this);
            e.Handled = true;
            return;
        }
    }

    private void SelectTabBy(int step)
    {
        if (_tabs.Count < 2) return;
        TabStrip.SelectedIndex = (TabStrip.SelectedIndex + step + _tabs.Count) % _tabs.Count;
    }

    // ───────────────────────────── Reopen closed tabs ─────────────────────────────

    private const int MaxClosedTabs = 10;
    private readonly List<(string Path, int Page)> _closedTabs = [];

    /// <summary>Remembers a tab the user closed (file and page) for Reopen Closed Tab.</summary>
    private void RememberClosed(DocumentTab tab)
    {
        if (tab.Session.FilePath is not { } path) return;
        _closedTabs.RemoveAll(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        _closedTabs.Add((path, tab.PageView.CurrentPage));
        if (_closedTabs.Count > MaxClosedTabs) _closedTabs.RemoveRange(0, _closedTabs.Count - MaxClosedTabs);
    }

    private async Task ReopenClosedAsync()
    {
        if (_closedTabs.Count == 0)
        {
            ShowStatus("No closed tabs to reopen");
            return;
        }
        var (path, page) = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        await OpenFileAsync(path);
        if (_tabs.FirstOrDefault(t => string.Equals(t.Tab.Session.FilePath, path, StringComparison.OrdinalIgnoreCase))?.Tab is not { } tab) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!tab.IsDisposed && page < tab.PageView.PageCount) tab.PageView.GoToPage(page, record: false);
        });
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
        var glyphs = CommandGlyphs();
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
                    // The command's toolbar or start-page icon, else the generic command glyph.
                    items.Add(new PaletteItem(name, category, gesture, glyphs.GetValueOrDefault(cmd, ""),
                        () => { if (cmd.CanExecute(null, this)) cmd.Execute(null, this); }));
                }
                else
                {
                    if (!mi.IsEnabled) continue;
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
        if (ActiveTab is { } t && q.Length > 0)
        {
            // A page label ("iv", "A-3") or a page number; both when they point at different pages.
            var byLabel = t.HasPageLabels ? DocumentFeatures.FindPageByLabel(Enumerable.Range(0, t.PageView.PageCount).Select(t.PageLabel).ToList(), q) : null;
            if (int.TryParse(q, out var number) && number >= 1 && number <= t.PageView.PageCount && byLabel != number - 1)
            {
                var label = t.PageLabel(number - 1);
                result.Insert(0, new PaletteItem(label != number.ToString() ? $"Go to page {number} ({label})" : $"Go to page {number}", "", "", "",
                    () => { t.SetOrganizing(false); t.PageView.GoToPage(number - 1); }));
            }
            if (byLabel is { } p)
                result.Insert(0, new PaletteItem($"Go to page {t.PageLabel(p)} ({p + 1} of {t.PageView.PageCount})", "", "", "",
                    () => { t.SetOrganizing(false); t.PageView.GoToPage(p); }));
        }
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
        // Dark and light show for themselves; following Windows may change nothing visible, so say it took effect.
        if (theme is not ("Dark" or "Light")) ShowStatus("Following your Windows setting");
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
        var author = dlg.AddText("Author name on comments", s.AuthorName is { Length: > 0 } a ? a : Environment.UserName);
        dlg.AddNote("To make PdfDesk your default PDF app: Windows Settings › Apps › Default apps › PdfDesk.");
        var defaults = dlg.Add(null, new Button { Content = "Open Default apps", HorizontalAlignment = HorizontalAlignment.Left });
        defaults.Click += (_, _) => Shell.Open(AppEnvironment.DefaultAppsUri);
        if (!dlg.ShowModal()) return;

        s.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
        s.RestorePosition = restore.IsChecked == true;
        s.ReopenTabs = reopen.IsChecked == true;
        s.DarkPages = dark.IsChecked == true;
        var name = author.Text.Trim();
        s.AuthorName = name == Environment.UserName ? "" : name; // empty = follow the Windows user name
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
        NoDocCombineButton.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        Title = t is null ? "PdfDesk" : $"{(t.Session.IsDirty ? "● " : "")}{t.DisplayTitle} – PdfDesk";

        var v = t?.PageView;
        HandButton.IsChecked = HandMenu.IsChecked = v?.Tool == ViewerTool.Hand;
        SelectButton.IsChecked = SelectMenu.IsChecked = v?.Tool == ViewerTool.SelectText;
        SnapshotButton.IsChecked = SnapshotMenu.IsChecked = v?.Tool == ViewerTool.Snapshot;
        AddTextButton.IsChecked = AddTextMenu.IsChecked = v?.Tool == ViewerTool.AddText;
        CheckButton.IsChecked = CheckMenu.IsChecked = v?.Tool == ViewerTool.AddCheck;
        CrossButton.IsChecked = CrossMenu.IsChecked = v?.Tool == ViewerTool.AddCross;
        AddSignatureButton.IsChecked = v?.Tool is ViewerTool.AddSignature or ViewerTool.PlaceSignature;
        NoteButton.IsChecked = NoteMenu.IsChecked = v?.Tool == ViewerTool.AddNote;
        RedactButton.IsChecked = RedactMenu.IsChecked = v?.Tool == ViewerTool.Redact;
        var drawTool = v is not null && DrawTools.IsDrawTool(v.Tool) ? v.Tool : (ViewerTool?)null;
        DrawButton.IsChecked = drawTool is not null;
        if (drawTool is { } dt) DrawButtonIcon.Text = DrawTools.Glyph(dt);
        foreach (var mi in DrawMenu.Items.OfType<MenuItem>()) mi.IsChecked = drawTool is { } d && mi.Command == Cmd.DrawTool(d);
        var pdf = t is { IsBusy: false, IsOrganizing: false, Session.IsPdf: true };
        AttachMenu.IsEnabled = pdf;
        RedactTextMenu.IsEnabled = pdf && v!.HasSelection;
        RedactHitsMenu.IsEnabled = pdf && t!.HasSearchResults;
        ApplyRedactMenu.IsEnabled = pdf && v!.Redactions.Count > 0;
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
        UndoButton.ToolTip = Cmd.Tip(t?.Session.UndoLabel is { } u2 ? $"Undo {u2}" : "Undo", Cmd.Undo);
        RedoButton.ToolTip = Cmd.Tip(t?.Session.RedoLabel is { } r2 ? $"Redo {r2}" : "Redo", Cmd.Redo);
        CommandManager.InvalidateRequerySuggested();
    }

    private void RestorePlacement()
    {
        var s = AppSettings.Current;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop) && FitToMonitor(new Rect(s.WindowLeft, s.WindowTop, Width, Height)) is { } fit)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = fit.Left;
            Top = fit.Top;
            Width = Math.Max(MinWidth, fit.Width);
            Height = Math.Max(MinHeight, fit.Height);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// The saved window rectangle (DIPs) moved and shrunk to fit the working area of the monitor it mostly lies on, or
    /// null when its title bar wouldn't be reachable on any monitor (one was unplugged, or the layout changed): the gaps
    /// between monitors of different sizes count as off-screen, unlike a check against the bounding box of all of them.
    /// </summary>
    private static Rect? FitToMonitor(Rect saved)
    {
        Rect? best = null;
        var bestArea = 0.0;
        foreach (var work in MonitorWorkAreas())
        {
            // The top strip (title bar) must be on this monitor, enough to grab and drag.
            var caption = Rect.Intersect(new Rect(saved.Left, saved.Top, saved.Width, 40), work);
            if (caption.IsEmpty || caption.Width < 100 || caption.Height < 20) continue;
            var overlap = Rect.Intersect(saved, work);
            var area = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
            if (best is not null && area <= bestArea) continue;
            bestArea = area;
            var w = Math.Min(saved.Width, work.Width);
            var h = Math.Min(saved.Height, work.Height);
            best = new Rect(Math.Clamp(saved.Left, work.Left, work.Right - w), Math.Clamp(saved.Top, work.Top, work.Bottom - h), w, h);
        }
        return best;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor, Work;
        public int Flags;
    }

    /// <summary>Each monitor's working area (without the taskbar) in DIPs at that monitor's own scale.</summary>
    private static List<Rect> MonitorWorkAreas()
    {
        var areas = new List<Rect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0; // MDT_EFFECTIVE_DPI
            var r = info.Work;
            areas.Add(new Rect(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale));
            return true;
        }, IntPtr.Zero);
        return areas;
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

    /// <summary>Where this version's source is published (the AGPL requires offering it with every copy).</summary>
    private const string SourceUrl = "https://github.com/gabandres/PdfDesk";

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        var dlg = new FormDialog(this, "About PdfDesk", "Close", 420);
        dlg.Add(null, new Image { Source = new BitmapImage(new Uri("pack://application:,,,/PdfDeskLogo.png")), Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Left });
        dlg.Add(null, new TextBlock { Text = $"PdfDesk {version?.ToString(3)}", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        dlg.AddNote($"Read, search, print and organize PDFs. Native {RuntimeInformation.ProcessArchitecture} build.");
        dlg.AddNote("Copyright © 2026 Bermudez Systems LLC. Free software under the GNU Affero General Public License v3: you may use, share and change it; it comes with no warranty. Rendering, text and search: MuPDF (AGPL-3.0). Page editing: PDFsharp (MIT). Nothing you open leaves this computer.");
        if (AppEnvironment.IsPackaged) dlg.AddNote("Installed as an app package; Windows keeps it up to date.");
        var notices = dlg.Add(null, new Button { Content = "Third-party notices…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) });
        notices.Click += (_, _) => ShowThirdPartyNotices(dlg);
        var source = dlg.Add(null, new Button { Content = "Source code (GitHub)", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) });
        source.Click += (_, _) => Shell.Open(SourceUrl);
        dlg.ShowModal();
    }

    /// <summary>The licenses of the libraries PdfDesk ships (THIRD-PARTY-NOTICES.md, embedded as a resource).</summary>
    private static void ShowThirdPartyNotices(Window owner)
    {
        string text;
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/THIRD-PARTY-NOTICES.md"));
            using var reader = new StreamReader(info.Stream);
            text = reader.ReadToEnd();
        }
        catch (IOException ex)
        {
            text = $"The notices could not be loaded: {ex.Message}";
        }
        var dlg = new FormDialog(owner, "Third-party notices", "Close", 640);
        dlg.Add(null, new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        });
        dlg.ShowModal();
    }
}
