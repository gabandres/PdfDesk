using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfDesk.App.Viewer;
using PdfDesk.Core;

namespace PdfDesk.App;

/// <summary>A page thumbnail. The image renders lazily the first time a visible list item binds to it.</summary>
public sealed class ThumbItem(int index, double width, double height, Action<ThumbItem> request) : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private bool _requested;

    public int Index { get; } = index;
    public string Label => (Index + 1).ToString();
    public double Width { get; } = width;
    public double Height { get; } = height;

    public BitmapSource? Image
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                request(this);
            }
            return _image;
        }
        set
        {
            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
    }

    /// <summary>Drops the bitmap; it renders again if a visible list item asks for it.</summary>
    public void Release()
    {
        _requested = false;
        Image = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Drag payload for thumbnails: pages of a document, possibly dropped into another tab.</summary>
public sealed record PageDragData(DocumentTab Source, int[] Pages);

/// <summary>A transient message for the toast area, optionally with an action such as Undo.</summary>
public sealed record Toast(string Message, string? ActionLabel = null, Action? Action = null, bool IsError = false);

public enum NavPaneKind { None, Pages, Bookmarks, Search }

public partial class DocumentTab : UserControl, IDisposable
{
    private const double ThumbBox = 130;
    private const string PagesFormat = "PdfDesk.Pages";

    private readonly RenderScheduler _scheduler;
    private readonly ObservableCollection<ThumbItem> _thumbs = [];
    private readonly ObservableCollection<SearchHit> _results = [];
    private int _thumbGeneration;
    private bool _syncingSelection;
    private bool _protectionWarned;
    private bool _outlineJustNavigated;
    private readonly LinkedList<ThumbItem> _thumbsWithImages = new();
    private const int MaxThumbImages = 300;

    // Search state
    private CancellationTokenSource? _searchCts;
    private (string Query, bool Case, bool Words)? _searched;
    private int _activeHit = -1;
    private bool _jumpWhenFound;
    private bool _searchBackwards;

    // Panes, busy veil, view-state persistence
    private NavPaneKind _pane = NavPaneKind.None;
    private readonly System.Windows.Threading.DispatcherTimer _busyDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly System.Windows.Threading.DispatcherTimer _saveViewDelay = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _updatingPill;
    private NavPaneKind _paneBeforeOrganize;

    // Thumbnail drag state
    private Point _dragStart;
    private bool _dragArmed;
    private InsertionAdorner? _insertion;

    public DocumentSession Session { get; }
    public PageViewer PageView => Viewer;
    public bool IsBusy { get; private set; }

    /// <summary>Page, zoom, selection, dirty state or busy state changed (refresh toolbar/status/header).</summary>
    public event EventHandler? StateChanged;
    public event EventHandler<Toast>? Toast;
    public bool IsOrganizing { get; private set; }

    private void Notify(string message, string? actionLabel = null, Action? action = null, bool error = false) =>
        Toast?.Invoke(this, new Toast(message, actionLabel, action, error));

    public DocumentTab(DocumentSession session)
    {
        InitializeComponent();
        Session = session;
        _scheduler = new RenderScheduler(Dispatcher);

        Thumbs.ItemsSource = _thumbs;
        SearchResults.ItemsSource = _results;

        var settings = AppSettings.Current;
        ShowPane(settings.NavPaneVisible ? NavPaneKind.Pages : NavPaneKind.None);
        if (Enum.TryParse<ViewLayout>(settings.Layout, out var layout)) Viewer.SetLayout(layout);
        if (Enum.TryParse<ZoomMode>(settings.ZoomMode, out var zoomMode) && zoomMode != ZoomMode.Custom) Viewer.SetZoomMode(zoomMode);
        Viewer.InvertColors = settings.DarkPages;

        Viewer.SetEngine(session.Engine, _scheduler, keepPosition: false);
        RebuildThumbs();
        LoadOutline();
        RestoreViewState();
        Organizer.Attach(this, _scheduler);
        Organizer.SelectionChanged += (_, _) => RaiseState();
        Organizer.OpenPage += (_, page) =>
        {
            SetOrganizing(false);
            Viewer.GoToPage(page);
        };
        Organizer.Done += (_, _) => SetOrganizing(false);

        // Rail and pane
        PagesRail.Click += (_, _) => TogglePane(NavPaneKind.Pages);
        BookmarksRail.Click += (_, _) => TogglePane(NavPaneKind.Bookmarks);
        SearchRail.Click += (_, _) => TogglePane(NavPaneKind.Search);
        ClosePaneButton.Click += (_, _) => ShowPane(NavPaneKind.None, remember: true);

        // Page / zoom pill
        ZoomBox.ItemsSource = new[] { "Fit width", "Fit page", "25%", "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%", "800%" };
        ZoomBox.SelectionChanged += (_, _) => { if (!_updatingPill && ZoomBox.SelectedItem is string z) ApplyZoomText(z); };
        ZoomBox.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ApplyZoomText(ZoomBox.Text);
            Viewer.Focus();
            e.Handled = true;
        }), handledEventsToo: true);
        ZoomBox.LostKeyboardFocus += (_, _) => UpdatePill();
        PageBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && int.TryParse(PageBox.Text.Trim(), out var n))
            {
                Viewer.GoToPage(n - 1);
                Viewer.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                UpdatePill();
                Viewer.Focus();
                e.Handled = true;
            }
        };
        PageBox.GotKeyboardFocus += (_, _) => PageBox.SelectAll();
        PageBox.LostKeyboardFocus += (_, _) => UpdatePill();

        _busyDelay.Tick += (_, _) =>
        {
            _busyDelay.Stop();
            if (IsBusy) BusyOverlay.Visibility = Visibility.Visible;
        };
        _saveViewDelay.Tick += (_, _) =>
        {
            _saveViewDelay.Stop();
            SaveViewState();
        };

        Viewer.CurrentPageChanged += (_, _) => { SyncThumbSelection(); RaiseState(); _saveViewDelay.Stop(); _saveViewDelay.Start(); };
        Viewer.ZoomChanged += (_, _) => RaiseState();
        Viewer.SelectionChanged += (_, _) => RaiseState();
        Viewer.ToolChanged += (_, _) => RaiseState();
        Viewer.StatusMessage += (_, m) => Notify(m);
        Session.ContentChanged += OnContentChanged;
        Session.StateChanged += (_, _) => RaiseState();

        Thumbs.SelectionChanged += OnThumbSelectionChanged;
        Thumbs.PreviewMouseLeftButtonDown += OnThumbPreviewDown;
        Thumbs.PreviewMouseMove += OnThumbPreviewMove;
        Thumbs.DragOver += OnThumbDragOver;
        Thumbs.DragLeave += (_, _) => HideInsertion();
        Thumbs.Drop += OnThumbDrop;
        Thumbs.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                Cmd.DeleteSelected.Execute(null, this);
                e.Handled = true;
            }
        };

        // A click that changes the selection navigates once; clicking the already-selected bookmark navigates again.
        Outline.SelectedItemChanged += (_, e) =>
        {
            if (e.NewValue is not OutlineNode n) return;
            _outlineJustNavigated = Mouse.LeftButton == MouseButtonState.Pressed;
            NavigateTo(n);
        };
        Outline.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_outlineJustNavigated && Outline.SelectedItem is OutlineNode n) NavigateTo(n);
            _outlineJustNavigated = false;
        };

        FindBox.KeyDown += OnFindKey;
        FindNextButton.Click += (_, _) => FindNext(false);
        FindPrevButton.Click += (_, _) => FindNext(true);
        FindCloseButton.Click += (_, _) => HideFindBar();
        SearchButton.Click += (_, _) => _ = RunSearchFromPane();
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _ = RunSearchFromPane();
            e.Handled = true;
        };
        SearchResults.SelectionChanged += (_, _) =>
        {
            if (SearchResults.SelectedIndex >= 0 && SearchResults.SelectedIndex != _activeHit) ShowHit(SearchResults.SelectedIndex);
        };

        NavSplitter.DragCompleted += (_, _) =>
        {
            AppSettings.Current.NavPaneWidth = NavColumn.ActualWidth;
            AppSettings.Current.Save();
        };
        UpdatePill();
    }

    public string Header => (Session.IsDirty ? "● " : "") + Session.DisplayName;

    private void RaiseState()
    {
        UpdatePill();
        UpdatePaneDetail();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pages the organize commands act on: the organizer or thumbnail selection, else the current page.</summary>
    public int[] SelectedPages
    {
        get
        {
            int[] sel = IsOrganizing ? Organizer.SelectedPages : [.. Thumbs.SelectedItems.Cast<ThumbItem>().Select(t => t.Index).Order()];
            return sel.Length > 0 ? sel : [Math.Clamp(Viewer.CurrentPage, 0, Math.Max(0, Viewer.PageCount - 1))];
        }
    }

    // ───────────────────────────── Panes ─────────────────────────────

    public bool NavPaneVisible => _pane != NavPaneKind.None;

    /// <summary>F4: show the pages pane, or hide the open one.</summary>
    public void SetNavPaneVisible(bool visible) => ShowPane(visible ? NavPaneKind.Pages : NavPaneKind.None, remember: true);

    private void TogglePane(NavPaneKind kind) => ShowPane(_pane == kind ? NavPaneKind.None : kind, remember: true);

    public void ShowPane(NavPaneKind kind, bool remember = false)
    {
        _pane = kind;
        var open = kind != NavPaneKind.None;
        NavPane.Visibility = NavSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        NavColumn.Width = open ? new GridLength(Math.Clamp(AppSettings.Current.NavPaneWidth, 160, 600)) : new GridLength(0);
        Thumbs.Visibility = kind == NavPaneKind.Pages ? Visibility.Visible : Visibility.Collapsed;
        BookmarksPanel.Visibility = kind == NavPaneKind.Bookmarks ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = kind == NavPaneKind.Search ? Visibility.Visible : Visibility.Collapsed;
        PagesRail.IsChecked = kind == NavPaneKind.Pages;
        BookmarksRail.IsChecked = kind == NavPaneKind.Bookmarks;
        SearchRail.IsChecked = kind == NavPaneKind.Search;
        PaneTitle.Text = kind switch { NavPaneKind.Pages => "Pages", NavPaneKind.Bookmarks => "Bookmarks", NavPaneKind.Search => "Search", _ => "" };
        UpdatePaneDetail();
        if (kind == NavPaneKind.Pages) SyncThumbSelection();
        if (remember)
        {
            AppSettings.Current.NavPaneVisible = open;
            AppSettings.Current.Save();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePaneDetail()
    {
        PaneDetail.Text = _pane switch
        {
            NavPaneKind.Pages when Thumbs.SelectedItems.Count > 1 => $"{Thumbs.SelectedItems.Count} selected",
            NavPaneKind.Pages => $"{Viewer.PageCount}",
            _ => "",
        };
    }

    // ───────────────────────────── Page / zoom pill ─────────────────────────────

    private void UpdatePill()
    {
        if (!PageBox.IsKeyboardFocusWithin) PageBox.Text = Viewer.PageCount > 0 ? (Viewer.CurrentPage + 1).ToString() : "";
        PageCountText.Text = $"of {Viewer.PageCount}";
        if (!ZoomBox.IsKeyboardFocusWithin)
        {
            _updatingPill = true;
            ZoomBox.SelectedItem = null;
            ZoomBox.Text = $"{Viewer.Zoom * 100:0}%";
            _updatingPill = false;
        }
        FitWidthButton.IsChecked = Viewer.ZoomMode == ZoomMode.FitWidth;
        FitPageButton.IsChecked = Viewer.ZoomMode == ZoomMode.FitPage;
    }

    private void ApplyZoomText(string text)
    {
        text = text.Trim();
        if (text.Equals("Fit width", StringComparison.OrdinalIgnoreCase)) SetZoomMode(ZoomMode.FitWidth);
        else if (text.Equals("Fit page", StringComparison.OrdinalIgnoreCase)) SetZoomMode(ZoomMode.FitPage);
        else if (double.TryParse(text.TrimEnd('%', ' '), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var pct) && pct > 0)
        {
            Viewer.SetZoom(pct / 100);
            AppSettings.Current.ZoomMode = nameof(ZoomMode.Custom);
        }
        UpdatePill();
    }

    public void SetZoomMode(ZoomMode mode)
    {
        Viewer.SetZoomMode(mode);
        AppSettings.Current.ZoomMode = mode.ToString();
        AppSettings.Current.Save();
        UpdatePill();
    }

    public void FocusPageBox()
    {
        PageBox.Focus();
        PageBox.SelectAll();
    }

    // ───────────────────────────── Organize mode ─────────────────────────────

    public void SelectAllPages() => Organizer.SelectAll();

    public void SetOrganizing(bool on)
    {
        if (on == IsOrganizing) return;
        if (on && !Session.IsPdf)
        {
            Notify("Organizing pages works on PDF files only.");
            return;
        }
        var selection = SelectedPages;
        IsOrganizing = on;
        // The grid needs the room; the side pane comes back when leaving.
        if (on)
        {
            _paneBeforeOrganize = _pane;
            ShowPane(NavPaneKind.None);
        }
        else if (_paneBeforeOrganize != NavPaneKind.None)
        {
            ShowPane(_paneBeforeOrganize);
        }
        ReadingView.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Organizer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            Organizer.Load(Session.Engine, selection.Length > 1 ? selection : [Viewer.CurrentPage], Viewer.CurrentPage);
            Organizer.Focus();
        }
        else
        {
            Viewer.Focus();
        }
        RaiseState();
        CommandManager.InvalidateRequerySuggested();
    }

    // ───────────────────────────── View state ─────────────────────────────

    private void RestoreViewState()
    {
        if (!AppSettings.Current.RestorePosition || Session.FilePath is null) return;
        if (!AppSettings.Current.Files.TryGetValue(Session.FilePath, out var state)) return;
        if (Enum.TryParse<ZoomMode>(state.ZoomMode, out var mode))
        {
            if (mode == ZoomMode.Custom) Viewer.SetZoom(state.Zoom);
            else Viewer.SetZoomMode(mode);
        }
        if (state.Page > 0 && state.Page < Viewer.PageCount)
        {
            var page = state.Page;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => Viewer.GoToPage(page, record: false));
        }
    }

    /// <summary>Remembers page and zoom for this file (reopened there next time).</summary>
    public void SaveViewState()
    {
        if (Session.FilePath is null || Viewer.PageCount == 0) return;
        var state = AppSettings.Current.StateOf(Session.FilePath);
        state.Page = Viewer.CurrentPage;
        state.ZoomMode = Viewer.ZoomMode.ToString();
        state.Zoom = Viewer.Zoom;
        AppSettings.Current.Save();
    }

    // ───────────────────────────── Editing ─────────────────────────────

    /// <summary>Runs a page edit off the UI thread and commits it as one undo step.</summary>
    public async Task<bool> RunEdit(string label, Func<byte[], string?, byte[]> edit, int? goToPage = null)
    {
        if (!Session.IsPdf)
        {
            Prompt.Info(Window.GetWindow(this), "Page editing works on PDFs", "This file can be viewed and printed, but only PDF pages can be rearranged.");
            return false;
        }
        if (IsBusy) return false;
        if (Session.Engine.IsEncrypted && !_protectionWarned)
        {
            if (!Prompt.Confirm(Window.GetWindow(this), "This PDF is password-protected",
                    "After a page edit it is saved without its password protection.", "Edit anyway", warning: true)) return false;
            _protectionWarned = true;
        }

        SetBusy($"{label}…");
        try
        {
            var data = Session.Data;
            var password = Session.Password;
            var result = await Task.Run(() => edit(data, password));
            Session.Commit(result, label);
            if (goToPage is { } p) Viewer.GoToPage(Math.Clamp(p, 0, Viewer.PageCount - 1), record: false);
            Notify(label, "Undo", Undo);
            return true;
        }
        catch (Exception ex) // top-level UI action: report any failure, never lose it
        {
            Log.Write($"{label} failed: {ex}");
            Prompt.Error(Window.GetWindow(this), $"{label} didn't work", ex.Message);
            return false;
        }
        finally
        {
            SetBusy(null);
        }
    }

    public void Undo() => Step(Session.Undo, Session.UndoLabel, "Undo");
    public void Redo() => Step(Session.Redo, Session.RedoLabel, "Redo");

    private void Step(Action action, string? label, string verb)
    {
        try
        {
            action();
            Notify($"{verb}: {label}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or MuPDFCore.MuPDFException)
        {
            Prompt.Error(Window.GetWindow(this), $"{verb} didn't work", ex.Message);
        }
    }

    public void SetBusy(string? text)
    {
        IsBusy = text is not null;
        BusyText.Text = text ?? "";
        // Most edits finish in a blink; only show the veil if one takes noticeably long.
        _busyDelay.Stop();
        if (IsBusy) _busyDelay.Start();
        else BusyOverlay.Visibility = Visibility.Collapsed;
        Cursor = IsBusy ? Cursors.AppStarting : null;
        RaiseState();
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnContentChanged(object? sender, EventArgs e)
    {
        CancelSearch();
        _results.Clear();
        _searched = null;
        _activeHit = -1;
        Viewer.SetSearchHits([]);
        Viewer.ShowHit(null);
        FindStatus.Text = SearchStatus.Text = "";

        int[] organizerSelection = IsOrganizing ? Organizer.SelectedPages : [];
        _scheduler.Clear();
        Viewer.SetEngine(Session.Engine, _scheduler, keepPosition: true);
        RebuildThumbs();
        LoadOutline();
        if (IsOrganizing) Organizer.Load(Session.Engine, organizerSelection, organizerSelection.FirstOrDefault(Viewer.CurrentPage));
        RaiseState();
    }

    // ───────────────────────────── Thumbnails ─────────────────────────────

    private void RebuildThumbs()
    {
        _thumbGeneration++;
        _syncingSelection = true;
        _thumbs.Clear();
        _thumbsWithImages.Clear();
        var engine = Session.Engine;
        for (var i = 0; i < engine.PageCount; i++)
        {
            var g = engine.GetPage(i);
            var dipPerPt = Math.Min(ThumbBox / g.Width, ThumbBox / g.Height);
            _thumbs.Add(new ThumbItem(i, Math.Round(g.Width * dipPerPt), Math.Round(g.Height * dipPerPt), RequestThumb));
        }
        _syncingSelection = false;
        SyncThumbSelection();
    }

    private void RequestThumb(ThumbItem item)
    {
        var engine = Session.Engine;
        var gen = _thumbGeneration;
        var g = engine.GetPage(item.Index);
        var scale = item.Width / g.Width * VisualTreeHelper.GetDpi(this).DpiScaleX;
        _scheduler.Enqueue($"t{item.Index}", 1000 + item.Index * 1e-6, () => PageViewer.ToBitmap(engine.Render(item.Index, scale)), result =>
        {
            if (gen != _thumbGeneration || result is not BitmapSource bmp) return;
            item.Image = bmp;
            // Keep the most recently rendered thumbnails; older ones re-render if scrolled back into view.
            _thumbsWithImages.AddLast(item);
            while (_thumbsWithImages.Count > MaxThumbImages)
            {
                _thumbsWithImages.First!.Value.Release();
                _thumbsWithImages.RemoveFirst();
            }
        });
    }

    private void SyncThumbSelection()
    {
        if (_syncingSelection || Thumbs.SelectedItems.Count > 1) return;
        var page = Viewer.CurrentPage;
        if (page < 0 || page >= _thumbs.Count) return;
        _syncingSelection = true;
        Thumbs.SelectedIndex = page;
        Thumbs.ScrollIntoView(_thumbs[page]);
        _syncingSelection = false;
    }

    /// <summary>Selects several thumbnails (e.g. the pages just rotated), without moving the view.</summary>
    public void SelectPages(IEnumerable<int> pages)
    {
        var list = pages.Where(p => p >= 0 && p < _thumbs.Count).ToList();
        if (IsOrganizing) Organizer.Load(Session.Engine, list, list.FirstOrDefault(-1));
        if (list.Count <= 1) return;
        _syncingSelection = true;
        Thumbs.SelectedItems.Clear();
        foreach (var p in list) Thumbs.SelectedItems.Add(_thumbs[p]);
        _syncingSelection = false;
        RaiseState();
    }

    private void OnThumbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseState();
        if (_syncingSelection || e.AddedItems.Count != 1 || e.AddedItems[0] is not ThumbItem t) return;
        _syncingSelection = true;
        Viewer.GoToPage(t.Index, record: false);
        _syncingSelection = false;
    }

    private void OnThumbPreviewDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Thumbs);
        _dragArmed = ItemAt(_dragStart) is not null;
    }

    private void OnThumbPreviewMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(Thumbs);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragArmed = false;
        if (!Session.IsPdf || IsBusy) return;
        var data = new DataObject(PagesFormat, new PageDragData(this, SelectedPages));
        try
        {
            DragDrop.DoDragDrop(Thumbs, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            HideInsertion();
        }
    }

    private void OnThumbDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!Session.IsPdf || IsBusy) return;

        if (e.Data.GetData(PagesFormat) is PageDragData d)
            e.Effects = d.Source == this ? DragDropEffects.Move : DragDropEffects.Copy;
        else if (DroppedPdfs(e).Length > 0)
            e.Effects = DragDropEffects.Copy;
        else
            return;

        var (index, y) = InsertionPoint(e.GetPosition(Thumbs));
        _ = index;
        ShowInsertion(y);
    }

    private async void OnThumbDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideInsertion();
        var (before, _) = InsertionPoint(e.GetPosition(Thumbs));
        await DropPages(e, before);
    }

    /// <summary>Handles pages dragged from this or another tab, or PDF files from Explorer, dropped before page <paramref name="before"/>.</summary>
    public async Task DropPages(DragEventArgs e, int before)
    {
        if (!Session.IsPdf || IsBusy) return;
        if (e.Data.GetData(PagesFormat) is PageDragData d)
        {
            if (d.Source == this)
            {
                var order = PageEditor.MoveOrder(Viewer.PageCount, d.Pages, before);
                if (order.SequenceEqual(Enumerable.Range(0, order.Length))) return;
                var newFirst = Array.IndexOf(order, d.Pages[0]);
                if (await RunEdit($"Move {Plural(d.Pages.Length)}", (data, pw) => PageEditor.Reorder(data, pw, order), newFirst))
                    SelectPages(Enumerable.Range(newFirst, d.Pages.Length));
            }
            else
            {
                var src = new PdfSource(d.Source.Session.Data, d.Source.Session.Password, d.Source.Session.DisplayName);
                await RunEdit($"Copy {Plural(d.Pages.Length)} from {src.Name}", (data, pw) => PageEditor.Insert(data, pw, src, d.Pages, before), before);
            }
            return;
        }

        foreach (var path in DroppedPdfs(e).Reverse())
        {
            if (PdfFiles.LoadForImport(Window.GetWindow(this)!, path) is not { } source) continue;
            await RunEdit($"Insert {Path.GetFileName(path)}", (data, pw) => PageEditor.Insert(data, pw, source, null, before), before);
        }
    }

    private static string Plural(int n) => n == 1 ? "1 page" : $"{n} pages";

    public static string[] DroppedPdfs(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? [.. files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(f))]
            : [];

    private ListBoxItem? ItemAt(Point p)
    {
        var hit = Thumbs.InputHitTest(p) as DependencyObject;
        while (hit is not null and not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
        return hit as ListBoxItem;
    }

    /// <summary>Index of the page the drop goes before, and the y (in list coordinates) for the insertion line.</summary>
    private (int Before, double Y) InsertionPoint(Point p)
    {
        if (ItemAt(p) is { } item && item.DataContext is ThumbItem t)
        {
            var top = item.TranslatePoint(new Point(0, 0), Thumbs).Y;
            var lower = p.Y > top + item.ActualHeight / 2;
            return lower ? (t.Index + 1, top + item.ActualHeight) : (t.Index, top);
        }

        // Below the last item (or empty space): append.
        if (_thumbs.Count > 0 && Thumbs.ItemContainerGenerator.ContainerFromIndex(_thumbs.Count - 1) is ListBoxItem last)
            return (_thumbs.Count, last.TranslatePoint(new Point(0, last.ActualHeight), Thumbs).Y);
        return (_thumbs.Count, p.Y);
    }

    private void ShowInsertion(double y)
    {
        if (_insertion is null)
        {
            var layer = AdornerLayer.GetAdornerLayer(Thumbs);
            if (layer is null) return;
            _insertion = new InsertionAdorner(Thumbs);
            layer.Add(_insertion);
        }
        _insertion.Y = y;
    }

    private void HideInsertion()
    {
        if (_insertion is null) return;
        AdornerLayer.GetAdornerLayer(Thumbs)?.Remove(_insertion);
        _insertion = null;
    }

    private sealed class InsertionAdorner(UIElement adorned) : Adorner(adorned)
    {
        private static readonly Pen LinePen = new(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)), 3);
        private double _y;

        public double Y
        {
            get => _y;
            set
            {
                _y = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            IsHitTestVisible = false;
            dc.DrawLine(LinePen, new Point(8, _y), new Point(AdornedElement.RenderSize.Width - 8, _y));
        }
    }

    // ───────────────────────────── Bookmarks ─────────────────────────────

    private void LoadOutline()
    {
        var outline = Session.Engine.GetOutline();
        Outline.ItemsSource = outline;
        NoBookmarks.Visibility = outline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavigateTo(OutlineNode node)
    {
        if (node.PageIndex >= 0 && node.PageIndex < Viewer.PageCount) Viewer.GoToPage(node.PageIndex);
    }

    public void ShowBookmarks()
    {
        ShowPane(NavPaneKind.Bookmarks);
    }

    // ───────────────────────────── Search ─────────────────────────────

    public void ShowFindBar()
    {
        FindBar.Visibility = Visibility.Visible;
        if (Viewer.HasSelection && Viewer.SelectionIsOnOnePage)
        {
            var sel = Viewer.GetSelectedText().Trim();
            if (sel.Length is > 0 and < 80 && !sel.Contains('\n')) FindBox.Text = sel;
        }
        FindBox.Focus();
        FindBox.SelectAll();
    }

    public void ShowSearchPane()
    {
        ShowPane(NavPaneKind.Search);
        if (string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = FindBox.Text;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void HideFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        Viewer.Focus();
    }

    private void OnFindKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                FindNext((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                e.Handled = true;
                break;
            case Key.Escape:
                HideFindBar();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Moves to the next (or previous) match, searching the document first if the query changed.</summary>
    public void FindNext(bool backwards)
    {
        var query = (FindBar.Visibility == Visibility.Visible ? FindBox.Text : SearchBox.Text.Length > 0 ? SearchBox.Text : FindBox.Text).Trim();
        if (query.Length == 0)
        {
            ShowFindBar();
            return;
        }

        var key = (query, FindMatchCase.IsChecked == true, FindWholeWords.IsChecked == true);
        if (FindBar.Visibility != Visibility.Visible && SearchBox.Text.Trim() == query)
            key = (query, SearchMatchCase.IsChecked == true, SearchWholeWords.IsChecked == true);

        if (_searched != key)
        {
            _searchBackwards = backwards;
            _jumpWhenFound = true;
            _ = RunSearch(key.query, key.Item2, key.Item3);
            return;
        }
        if (_results.Count == 0) return;

        int next;
        if (_activeHit < 0)
        {
            next = FirstHitFrom(Viewer.CurrentPage, backwards);
        }
        else
        {
            next = backwards ? _activeHit - 1 : _activeHit + 1;
            if (next < 0 || next >= _results.Count)
            {
                next = (next + _results.Count) % _results.Count;
                Notify(backwards ? "Search wrapped to the end" : "Search wrapped to the beginning");
            }
        }
        ShowHit(next);
    }

    private int FirstHitFrom(int page, bool backwards)
    {
        if (backwards)
        {
            for (var i = _results.Count - 1; i >= 0; i--)
                if (_results[i].Page <= page) return i;
            return _results.Count - 1;
        }
        for (var i = 0; i < _results.Count; i++)
            if (_results[i].Page >= page) return i;
        return 0;
    }

    private void ShowHit(int index)
    {
        if (index < 0 || index >= _results.Count) return;
        _activeHit = index;
        Viewer.ShowHit(_results[index]);
        if (SearchResults.SelectedIndex != index)
        {
            SearchResults.SelectedIndex = index;
            SearchResults.ScrollIntoView(_results[index]);
        }
        FindStatus.Text = $"{index + 1} of {_results.Count}";
    }

    private Task RunSearchFromPane()
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) return Task.CompletedTask;
        FindBox.Text = q;
        FindMatchCase.IsChecked = SearchMatchCase.IsChecked;
        FindWholeWords.IsChecked = SearchWholeWords.IsChecked;
        _jumpWhenFound = true;
        _searchBackwards = false;
        return RunSearch(q, SearchMatchCase.IsChecked == true, SearchWholeWords.IsChecked == true);
    }

    private async Task RunSearch(string query, bool matchCase, bool wholeWords)
    {
        CancelSearch();
        var cts = _searchCts = new CancellationTokenSource();
        _searched = (query, matchCase, wholeWords);
        _results.Clear();
        _activeHit = -1;
        Viewer.SetSearchHits([]);
        Viewer.ShowHit(null);
        SearchBox.Text = query;
        SearchMatchCase.IsChecked = matchCase;
        SearchWholeWords.IsChecked = wholeWords;

        var engine = Session.Engine;
        var count = engine.PageCount;
        var startPage = Viewer.CurrentPage;
        var progress = new Progress<(int Page, List<SearchHit> Hits)>(update =>
        {
            if (cts.IsCancellationRequested) return;
            foreach (var h in update.Hits) _results.Add(h);
            if (update.Hits.Count > 0) Viewer.SetSearchHits(_results);
            SearchStatus.Text = FindStatus.Text = $"Searching… page {update.Page + 1} of {count} ({_results.Count} found)";

            // Jump to the first match at/after the current page as soon as it's known (not after the whole scan).
            if (_jumpWhenFound && !_searchBackwards && update.Hits.Exists(h => h.Page >= startPage))
            {
                _jumpWhenFound = false;
                ShowHit(FirstHitFrom(startPage, false));
            }
        });

        try
        {
            await Task.Run(() =>
            {
                var batch = new List<SearchHit>();
                var last = Environment.TickCount64;
                IProgress<(int, List<SearchHit>)> report = progress;
                for (var p = 0; p < count; p++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var text = engine.GetText(p, cache: false);
                    foreach (var (s, e) in text.Find(query, matchCase, wholeWords))
                        batch.Add(new SearchHit(p, s, e, text.GetRects(s, e), text.Excerpt(s, e)));
                    if (batch.Count > 0 || Environment.TickCount64 - last > 150 || p == count - 1)
                    {
                        report.Report((p, batch));
                        batch = [];
                        last = Environment.TickCount64;
                    }
                }
            }, cts.Token);

            // Let the last progress callback land before summarizing.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (cts.IsCancellationRequested) return;
            SearchStatus.Text = _results.Count == 0 ? $"No matches for \"{query}\"." : $"{_results.Count} match(es) on {_results.Select(r => r.Page).Distinct().Count()} page(s).";
            if (_results.Count == 0) FindStatus.Text = "No matches";
            else if (_jumpWhenFound)
            {
                _jumpWhenFound = false;
                ShowHit(FirstHitFrom(startPage, _searchBackwards));
            }
            else if (_activeHit >= 0) FindStatus.Text = $"{_activeHit + 1} of {_results.Count}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // Document edited mid-search; OnContentChanged already reset the results.
        }
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
    }

    public void Dispose()
    {
        _saveViewDelay.Stop();
        _busyDelay.Stop();
        SaveViewState();
        CancelSearch();
        _scheduler.Dispose();
        Session.Dispose();
    }
}
