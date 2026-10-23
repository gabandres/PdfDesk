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
    public string Label { get; private set; } = (index + 1).ToString();
    public string AccessibleName { get; private set; } = $"Page {index + 1}";
    public override string ToString() => AccessibleName;

    /// <summary>Shows the page's label (e.g. "iv") instead of its number.</summary>
    public void SetLabel(string label, int pageCount)
    {
        if (label == Label) return;
        Label = label;
        AccessibleName = label == (Index + 1).ToString() ? $"Page {Index + 1}" : $"Page {label} ({Index + 1} of {pageCount})";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
    }
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

    /// <summary>The render failed: ask again the next time a list item binds to it (no immediate retry loop).</summary>
    public void MarkFailed() => _requested = false;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Drag payload for thumbnails: pages of a document, possibly dropped into another tab.</summary>
public sealed record PageDragData(DocumentTab Source, int[] Pages);

/// <summary>A transient message for the toast area, optionally with an action such as Undo.</summary>
public sealed record Toast(string Message, string? ActionLabel = null, Action? Action = null, bool IsError = false);

public enum NavPaneKind { None, Pages, Bookmarks, Search, Comments, Attachments, Layers, Signatures }

/// <summary>How <see cref="DocumentTab.TryEdit"/> ended.</summary>
public enum EditOutcome { Done, NoChange, Busy, Declined, Failed }

public partial class DocumentTab : UserControl, IDisposable
{
    private const double ThumbBox = 130;
    private const string PagesFormat = "PdfDesk.Pages";

    private readonly RenderScheduler _scheduler;
    private readonly ObservableCollection<ThumbItem> _thumbs = [];
    private readonly ObservableCollection<SearchHit> _results = [];
    private int _thumbGeneration;
    private bool _syncingSelection;
    private readonly LinkedList<ThumbItem> _thumbsWithImages = new();
    private const int MaxThumbImages = 300;

    // Search state
    private CancellationTokenSource? _searchCts;
    private (string Query, bool Case, bool Words, bool Accents)? _searched;
    private int _activeHit = -1;
    private bool _jumpWhenFound;
    private bool _searchBackwards;

    // Panes, busy veil, view-state persistence
    private NavPaneKind _pane = NavPaneKind.None;
    private readonly System.Windows.Threading.DispatcherTimer _busyDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly System.Windows.Threading.DispatcherTimer _saveViewDelay = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _updatingPill;

    // Forms and Fill & Sign
    private readonly Dictionary<string, string> _formQueue = [];
    private bool _formSaving;
    private int _annotationLoad;
    private bool _formBarDismissed;
    private bool _contentOnlyCommit;
    private bool _steppingHistory;
    private NavPaneKind _paneBeforeOrganize;

    // Pending writes (form values, Fill & Sign stamps, page edits): Save and Close wait for them.
    private int _pendingWrites;
    private TaskCompletionSource? _writesDone;
    private TaskCompletionSource? _idle;

    // Edit identity, mirroring the session's undo/redo stacks, so a toast's Undo only undoes its own edit.
    private long _editSeq;
    private readonly List<long> _undoIds = [];
    private readonly Stack<long> _redoIds = new();

    // Watching the file on disk for changes by other programs.
    private FileSystemWatcher? _watcher;
    private string? _watchedPath;
    private readonly System.Windows.Threading.DispatcherTimer _diskDelay = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private int _reloadAttempts;
    private bool _diskAcknowledged;
    private Action? _diskPrimary, _diskSecondary;

    // Thumbnail drag state
    private Point _dragStart;
    private bool _dragArmed;
    private InsertionAdorner? _insertion;

    public DocumentSession Session { get; }
    public PageViewer PageView => Viewer;
    /// <summary>True while any long operation (edit, print, export, save, reload, a warning prompt) holds the tab.</summary>
    public bool IsBusy => _busy.Count > 0;
    private readonly List<BusyLease> _busy = [];

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

        // Shortcuts in tooltips come from the commands' gestures (never typed by hand, so they can't drift).
        Cmd.AddShortcutsToTips(this);
        PageBox.ToolTip = Cmd.Tip((string)PageBox.ToolTip, Cmd.GoToPage);
        SearchRail.ToolTip = Cmd.Tip("Search", Cmd.SearchPane);
        ClosePaneButton.ToolTip = Cmd.Tip("Close pane", Cmd.NavPane);
        FindPrevButton.ToolTip = Cmd.Tip("Previous match", Cmd.FindPrevious);
        FindNextButton.ToolTip = Cmd.Tip("Next match", Cmd.FindNext);

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
        Viewer.FieldEdited += (_, e) =>
        {
            _formQueue[e.FieldName] = e.Value;
            _ = FlushFormQueue();
        };
        Viewer.StampEdited += (_, e) => _ = SaveStamp(e);
        InitFormActions();
        FormBarClose.Click += (_, _) =>
        {
            _formBarDismissed = true;
            FormBar.Visibility = Visibility.Collapsed;
        };
        HighlightFieldsBox.Click += (_, _) => Viewer.HighlightFields = HighlightFieldsBox.IsChecked == true;
        _ = LoadAnnotationsAsync();
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
            if (e.Key == Key.Enter && FindPage(PageBox.Text) is { } target)
            {
                Viewer.GoToPage(target);
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
            if (BusyText.Text.Length > 0) BusyOverlay.Visibility = Visibility.Visible;
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

        InitBookmarks(); // the Bookmarks pane: navigation and editing (DocumentTab.Bookmarks.cs)

        DiskBarClose.Click += (_, _) =>
        {
            _diskAcknowledged = true;
            DiskBar.Visibility = Visibility.Collapsed;
        };
        DiskBarPrimary.Click += (_, _) => _diskPrimary?.Invoke();
        DiskBarSecondary.Click += (_, _) => _diskSecondary?.Invoke();
        _diskDelay.Tick += (_, _) =>
        {
            _diskDelay.Stop();
            CheckDisk();
        };
        Session.StateChanged += (_, _) =>
        {
            if (!string.Equals(_watchedPath, Session.FilePath, StringComparison.OrdinalIgnoreCase)) StartWatching();
        };
        StartWatching();

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
        InitReview();
        UpdatePill();
    }

    public string Header => (Session.IsDirty ? "● " : "") + DisplayTitle;

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
            // In the organizer only explicitly selected pages count (nothing selected = nothing to act on).
            if (IsOrganizing) return Organizer.SelectedPages;
            int[] sel = [.. Thumbs.SelectedItems.Cast<ThumbItem>().Select(t => t.Index).Order()];
            return sel.Length > 0 ? sel : Viewer.PageCount == 0 ? [] : [Math.Clamp(Viewer.CurrentPage, 0, Viewer.PageCount - 1)];
        }
    }

    // ───────────────────────────── Panes ─────────────────────────────

    public bool NavPaneVisible => _pane != NavPaneKind.None;
    public NavPaneKind CurrentPane => _pane;

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
        CommentsPanel.Visibility = kind == NavPaneKind.Comments ? Visibility.Visible : Visibility.Collapsed;
        AttachmentsPanel.Visibility = kind == NavPaneKind.Attachments ? Visibility.Visible : Visibility.Collapsed;
        LayersPanel.Visibility = kind == NavPaneKind.Layers ? Visibility.Visible : Visibility.Collapsed;
        SignaturesPanel.Visibility = kind == NavPaneKind.Signatures ? Visibility.Visible : Visibility.Collapsed;
        PagesRail.IsChecked = kind == NavPaneKind.Pages;
        BookmarksRail.IsChecked = kind == NavPaneKind.Bookmarks;
        SearchRail.IsChecked = kind == NavPaneKind.Search;
        CommentsRail.IsChecked = kind == NavPaneKind.Comments;
        AttachmentsRail.IsChecked = kind == NavPaneKind.Attachments;
        LayersRail.IsChecked = kind == NavPaneKind.Layers;
        SignaturesRail.IsChecked = kind == NavPaneKind.Signatures;
        PaneTitle.Text = kind == NavPaneKind.None ? "" : kind.ToString();
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
            NavPaneKind.Comments => _commentItems.Count > 0 ? $"{_commentItems.Count}" : "",
            NavPaneKind.Attachments => _attachments.Count > 0 ? $"{_attachments.Count}" : "",
            NavPaneKind.Layers => $"{_layers.Count}",
            NavPaneKind.Signatures => $"{_signatures.Count(s => s.Status != SignatureStatus.Unsigned)} signed",
            _ => "",
        };
    }

    /// <summary>Opens one of the review panes (Comments, Attachments, Layers, Signatures).</summary>
    public void ShowReviewPane(ReviewPane pane)
    {
        SetOrganizing(false);
        ShowPane(pane switch
        {
            ReviewPane.Attachments => NavPaneKind.Attachments,
            ReviewPane.Layers => NavPaneKind.Layers,
            ReviewPane.Signatures => NavPaneKind.Signatures,
            _ => NavPaneKind.Comments,
        });
    }

    // ───────────────────────────── Page / zoom pill ─────────────────────────────

    private void UpdatePill()
    {
        var n = Viewer.PageCount;
        var current = Viewer.CurrentPage;
        var label = PageLabel(current);
        if (!PageBox.IsKeyboardFocusWithin) PageBox.Text = n > 0 ? label : "";
        // With page labels: "iii (3 of 120)"; without: "3 of 120".
        PageCountText.Text = n > 0 && label != (current + 1).ToString() ? $"({current + 1} of {n})" : $"of {n}";
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

    // ───────────────────────────── Forms and Fill & Sign ─────────────────────────────

    /// <summary>Reads form fields and Fill &amp; Sign items off the UI thread (the latest request wins).</summary>
    private async Task LoadAnnotationsAsync()
    {
        if (!Session.IsPdf) return;
        var ticket = ++_annotationLoad;
        var data = Session.Data;
        var pw = Session.Password;
        IReadOnlyList<FormWidget> widgets = [];
        IReadOnlyList<TextStamp> stamps = [];
        IReadOnlyList<int>? rotations = null;
        try
        {
            (widgets, stamps, rotations) = await Task.Run(() =>
            {
                var w = FormEditor.Read(data, pw);
                var s = TextStamps.Read(data, pw);
                // Field editors on turned pages follow the page's /Rotate (only needed when there are fields).
                return (w, s, w.Count > 0 ? ReadRotations(data, pw) : null);
            });
        }
        catch (Exception ex)
        {
            Log.Write($"Reading form fields failed: {ex.Message}");
        }
        if (ticket != _annotationLoad || _disposed) return;
        Viewer.SetPageRotations(rotations);
        Viewer.SetFormWidgets(widgets);
        Viewer.SetStamps(stamps);

        var fields = widgets.Where(w => !w.ReadOnly && w.Kind is not (FormFieldKind.PushButton or FormFieldKind.Signature))
            .Select(w => w.FieldName).Distinct().Count();
        FormBar.Visibility = fields > 0 && !_formBarDismissed ? Visibility.Visible : Visibility.Collapsed;
        FormBarText.Text = $"This form has {fields} fillable field{(fields == 1 ? "" : "s")}. Click a field to fill it; Tab moves to the next one. {Cmd.Tip("Save", Cmd.Save)} when you're done.";
        RaiseState();
    }

    /// <summary>Each page's /Rotate, or null if the document can't be read that way.</summary>
    private static int[]? ReadRotations(byte[] data, string? password)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var doc = string.IsNullOrEmpty(password)
                ? PdfSharp.Pdf.IO.PdfReader.Open(stream, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import)
                : PdfSharp.Pdf.IO.PdfReader.Open(stream, password, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            var rotations = new int[doc.PageCount];
            for (var i = 0; i < rotations.Length; i++) rotations[i] = doc.Pages[i].Rotate;
            return rotations;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"Reading page rotations failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes queued field values one batch at a time, so quick clicks and typing never lose a change.</summary>
    private async Task FlushFormQueue()
    {
        if (_formSaving) return;
        _formSaving = true;
        BeginWrite();
        try
        {
            while (_formQueue.Count > 0 && !_disposed)
            {
                if (IsBusy)
                {
                    await WhenIdle();
                    continue;
                }
                var batch = new Dictionary<string, string>(_formQueue);
                _formQueue.Clear();
                var label = batch.Count == 1 ? "Fill form field" : $"Fill {batch.Count} form fields";
                FormFillResult? fill = null;
                var host = FormHost;
                var outcome = await TryEdit(label, (d, pw) => (fill = FormEditor.Fill(d, pw, batch, host)).Data, quiet: true, contentOnly: true);
                if (fill is not null && outcome is EditOutcome.Done or EditOutcome.NoChange) ReportFormScripts(batch, fill);
                if (outcome == EditOutcome.Busy)
                {
                    // Another edit (or its warning prompt) got the tab first: put the values back (newer ones typed
                    // meanwhile win) and write them once it's done.
                    foreach (var (name, value) in batch) _formQueue.TryAdd(name, value);
                    continue;
                }
                if (outcome != EditOutcome.Done)
                {
                    // Declined the password/signature warning, or the write failed: the fields show the document's
                    // values again, so nothing on screen claims a change that isn't in the file.
                    _formQueue.Clear();
                    Viewer.ClearPendingValues();
                    break;
                }
            }
        }
        finally
        {
            _formSaving = false;
            EndWrite();
        }
    }

    /// <summary>
    /// What the form's scripts said about a written batch: refused values go back to the document's value with the
    /// script's message; app.alert texts show as toasts; console output and script errors go to the log.
    /// </summary>
    private void ReportFormScripts(Dictionary<string, string> batch, FormFillResult fill)
    {
        foreach (var line in fill.Log) Log.Write(line);
        foreach (var r in fill.Rejected) Log.Write($"Form script refused the value of {r.FieldName}: {r.Message}");
        if (fill.Rejected.Count > 0)
        {
            Viewer.RevertPendingValues(fill.Rejected.Where(r => batch.ContainsKey(r.FieldName)).ToDictionary(r => r.FieldName, r => batch[r.FieldName]));
            // An empty message: the script already told the user in a dialog.
            foreach (var r in fill.Rejected.Where(r => r.Message.Length > 0)) Notify(r.Message, error: true);
        }
        foreach (var alert in fill.Alerts.Distinct().Take(3)) Notify(alert);
        if (fill.FocusField is { } focus && !Viewer.IsEditing) Viewer.FocusField(focus);
    }

    private async Task SaveStamp(StampEdit e)
    {
        BeginWrite();
        try
        {
            // Retry while another edit (or its warning prompt) holds the tab; a declined warning or a failure ends it
            // (the stamp only shows once it is in the document, so nothing needs reverting).
            while (true)
            {
                while (IsBusy)
                {
                    await WhenIdle();
                    if (_disposed) return;
                }
                var outcome = e.ReplaceId is { } id && e.Text.Length == 0 && e.Kind == StampKind.Text
                    ? await TryEdit("Remove text", (d, pw) => TextStamps.Remove(d, pw, id), contentOnly: true)
                    : e.ReplaceId is { } markId && e.Kind != StampKind.Text
                        ? await TryEdit(e.Kind == StampKind.Check ? "Remove check mark" : "Remove cross", (d, pw) => TextStamps.Remove(d, pw, markId), contentOnly: true)
                        : await TryEdit(e.Kind switch { StampKind.Check => "Add check mark", StampKind.Cross => "Add cross", _ => e.ReplaceId is null ? "Add text" : "Edit text" },
                            (d, pw) => TextStamps.Put(d, pw, e.PageIndex, e.Rect, e.Kind, e.Text, e.FontSize, e.ReplaceId), quiet: e.Kind == StampKind.Text, contentOnly: true);
                if (outcome != EditOutcome.Busy || _disposed) return;
            }
        }
        finally
        {
            EndWrite();
        }
    }

    // ───────────────────────────── Pending writes ─────────────────────────────

    public bool IsDisposed => _disposed;

    /// <summary>An open form/Fill &amp; Sign editor, queued values, or an edit still being written.</summary>
    public bool HasPendingEdits => Viewer.IsEditing || _formQueue.Count > 0 || _pendingWrites > 0;

    private void BeginWrite() => _pendingWrites++;

    private void EndWrite()
    {
        if (--_pendingWrites > 0) return;
        _pendingWrites = 0;
        var done = _writesDone;
        _writesDone = null;
        done?.TrySetResult();
    }

    private Task WhenWritesDone()
    {
        if (_pendingWrites == 0 || _disposed) return Task.CompletedTask;
        _writesDone ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _writesDone.Task;
    }

    private Task WhenIdle()
    {
        if (!IsBusy || _disposed) return Task.CompletedTask;
        _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _idle.Task;
    }

    /// <summary>
    /// Saves what the open field or Fill &amp; Sign editor holds, then waits until every queued value, stamp and page edit
    /// has been written into the document (and any other busy work has finished). Call before saving or closing.
    /// </summary>
    public async Task FlushPendingEditsAsync()
    {
        Viewer.CommitEditor();
        while (!_disposed && (_pendingWrites > 0 || IsBusy))
            await (_pendingWrites > 0 ? WhenWritesDone() : WhenIdle());
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
        _hadViewState = true; // the reader's own position wins over the document's opening settings
        var (zoomMode, rotation) = SplitZoomState(state.ZoomMode);
        if (Enum.TryParse<ZoomMode>(zoomMode, out var mode))
        {
            if (mode == ZoomMode.Custom) Viewer.SetZoom(state.Zoom);
            else Viewer.SetZoomMode(mode);
        }
        Viewer.SetViewRotation(rotation);
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
        state.ZoomMode = Viewer.ZoomMode + (Viewer.ViewRotation != 0 ? $"{RotationTag}{Viewer.ViewRotation}" : "");
        state.Zoom = Viewer.Zoom;
        AppSettings.Current.Save();
    }

    // The view rotation rides along in the saved zoom mode ("FitWidth;rotate=90") until the per-file state gets a
    // field of its own; an unturned view saves the plain mode, so older settings files read the same.
    private const string RotationTag = ";rotate=";

    private static (string ZoomMode, int Rotation) SplitZoomState(string? saved)
    {
        saved ??= "";
        var at = saved.IndexOf(RotationTag, StringComparison.Ordinal);
        if (at < 0) return (saved, 0);
        return (saved[..at], int.TryParse(saved.AsSpan(at + RotationTag.Length), out var r) ? r : 0);
    }

    // ───────────────────────────── Editing ─────────────────────────────

    /// <summary>Runs a page edit off the UI thread and commits it as one undo step. True when it was committed.</summary>
    /// <param name="newPasswords">The edit changes the protection (protect, unprotect): the passwords that open the result.</param>
    public async Task<bool> RunEdit(string label, Func<byte[], string?, byte[]> edit, int? goToPage = null, bool quiet = false, bool contentOnly = false,
        bool signaturesWarned = false, (string? Password, string? OpenPassword)? newPasswords = null) =>
        await TryEdit(label, edit, goToPage, quiet, contentOnly, signaturesWarned, newPasswords) is EditOutcome.Done or EditOutcome.NoChange;

    /// <summary>
    /// <see cref="RunEdit"/> that says why nothing was committed: <see cref="EditOutcome.NoChange"/> (the edit returned the
    /// same bytes), <see cref="EditOutcome.Busy"/> (another edit or its
    /// warning prompt holds the tab: try again once idle), <see cref="EditOutcome.Declined"/> (the user said no to the
    /// password/signature warning) or <see cref="EditOutcome.Failed"/> (the edit threw, or the file isn't a PDF).
    /// </summary>
    /// <param name="signaturesWarned">The caller already said the edit breaks signatures (redaction): don't ask again.</param>
    /// <param name="newPasswords">The edit changes the protection (protect, unprotect): the passwords that open the result.</param>
    public async Task<EditOutcome> TryEdit(string label, Func<byte[], string?, byte[]> edit, int? goToPage = null, bool quiet = false, bool contentOnly = false,
        bool signaturesWarned = false, (string? Password, string? OpenPassword)? newPasswords = null)
    {
        if (!Session.IsPdf)
        {
            Prompt.Info(Window.GetWindow(this), "Page editing works on PDFs", "This file can be viewed and printed, but only PDF pages can be rearranged.");
            return EditOutcome.Failed;
        }
        if (_disposed) return EditOutcome.Failed;
        if (IsBusy) return EditOutcome.Busy;
        BusyLease? hold = null;
        if (NeedsSignedWarning)
        {
            // Hold the tab while the warning is open, so an edit started meanwhile waits (Busy) instead of slipping in
            // or opening a second prompt. No veil: the prompt is the only thing on screen.
            hold = BeginBusy(null);
            var go = false;
            try
            {
                go = ConfirmSignedEdit();
            }
            finally
            {
                if (!go) hold.Dispose();
            }
            if (!go) return EditOutcome.Declined;
            if (_disposed)
            {
                hold.Dispose();
                return EditOutcome.Failed;
            }
        }

        using var busy = BeginBusy($"{label}…");
        hold?.Dispose();
        BeginWrite();
        try
        {
            var data = Session.Data;
            var password = Session.Password;
            byte[] result;
            SignedEditReport report;
            var rewrite = _signatureWarned; // already accepted that edits break the signatures (encrypted signed file)
            // An encrypted document keeps its protection through the edit; AES-256 opened with the owner password needs
            // its open password for that (asked once, see KeptProtection).
            var openPassword = Session.OpenPassword;
            var dropOpenPassword = false;
            while (true)
            {
                try
                {
                    var allowRewrite = rewrite;
                    var (open, drop) = (openPassword, dropOpenPassword);
                    (result, report) = await Task.Run(() => KeptProtection.Run(open, drop, () => SignedEdits.Run(() => edit(data, password), allowRewrite)));
                    break;
                }
                catch (OpenPasswordUnknownException) when (!_disposed)
                {
                    switch (SecurityActions.PromptOpenPassword(Window.GetWindow(this), Session.DisplayName, data))
                    {
                        case null:
                            return EditOutcome.Declined;
                        case { DropIt: true }:
                            dropOpenPassword = true;
                            break;
                        case { Password: var given }:
                            openPassword = given;
                            break;
                    }
                }
                catch (OwnerPasswordRequiredException ex) when (!_disposed)
                {
                    // Opened for viewing without the permissions password: ask for it and try the same edit again.
                    if (PdfFiles.PromptOwnerPassword(Window.GetWindow(this), Session.DisplayName, ex.WrongPassword) is not { } owner)
                        return EditOutcome.Declined;
                    password = owner;
                }
                catch (SignaturesWouldBreakException ex) when (!_disposed && !rewrite)
                {
                    // The edit can't be appended to the signed file: say so before anything is committed, then redo it as a rewrite.
                    Log.Write($"{label}: {ex.Reason}");
                    if (!ConfirmSignatureRewrite(ex.SignatureCount)) return EditOutcome.Declined;
                    rewrite = true;
                }
            }
            if (_disposed) return EditOutcome.Failed;
            if (password is not null && password != Session.Password) Session.UsePassword(password); // only once it worked
            if (openPassword != Session.OpenPassword) Session.UseOpenPassword(openPassword);
            if (ReferenceEquals(result, data)) return EditOutcome.NoChange; // nothing to do: no undo step
            // Signed document: an edit a signature doesn't allow (a page change, or one that couldn't be appended and
            // rewrites the file) shows that signature as invalid from then on. Say so before it happens.
            if (_signedOnOpen && !signaturesWarned && !rewrite)
            {
                var broken = await Task.Run(() => Signatures.BrokenByEdits(Signatures.Verify(data, password), result, password));
                if (_disposed) return EditOutcome.Failed;
                if (broken.Count > 0 && !ConfirmBreakingEdit(label, broken)) return EditOutcome.Declined;
            }
            _contentOnlyCommit = contentOnly;
            try
            {
                if (newPasswords is { } np) Session.Commit(result, label, np.Password, np.OpenPassword);
                else Session.Commit(result, label, Session.Password, dropOpenPassword ? null : Session.OpenPassword);
            }
            finally
            {
                _contentOnlyCommit = false;
            }
            var id = ++_editSeq;
            _undoIds.Add(id);
            _redoIds.Clear();
            if (_undoIds.Count > Session.UndoDepth) _undoIds.RemoveRange(0, _undoIds.Count - Session.UndoDepth);
            if (goToPage is { } p) Viewer.GoToPage(Math.Clamp(p, 0, Viewer.PageCount - 1), record: false);
            if (!quiet)
            {
                Notify(label, "Undo", () =>
                {
                    // Only while this very edit is still the newest undo step (not an older or newer one at the same depth).
                    if (_disposed || IsBusy || _undoIds.Count == 0 || _undoIds[^1] != id || !Session.CanUndo) return;
                    Undo();
                });
            }
            if (report.RemovedItemKept) NotifyRemovedItemKept();
            return EditOutcome.Done;
        }
        catch (OperationCanceledException)
        {
            return EditOutcome.Declined; // the user cancelled a long edit (e.g. text recognition): nothing changed
        }
        catch (Exception ex) // top-level UI action: report any failure, never lose it
        {
            Log.Write($"{label} failed: {ex}");
            Prompt.Error(Window.GetWindow(this), $"{label} didn't work", ex.Message);
            return EditOutcome.Failed;
        }
        finally
        {
            EndWrite();
        }
    }

    public void Undo()
    {
        if (!Session.CanUndo) return;
        Step(() =>
        {
            _steppingHistory = true;
            try { Session.Undo(); } finally { _steppingHistory = false; }
            if (_undoIds.Count > 0)
            {
                _redoIds.Push(_undoIds[^1]);
                _undoIds.RemoveAt(_undoIds.Count - 1);
            }
        }, Session.UndoLabel, "Undo");
    }

    public void Redo()
    {
        if (!Session.CanRedo) return;
        Step(() =>
        {
            _steppingHistory = true;
            try { Session.Redo(); } finally { _steppingHistory = false; }
            _undoIds.Add(_redoIds.TryPop(out var id) ? id : ++_editSeq);
        }, Session.RedoLabel, "Redo");
    }

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

    /// <summary>
    /// Holds the tab busy until the returned lease is disposed (once; disposing again does nothing). Leases nest: the tab is
    /// idle only when every lease has ended, so one operation finishing never clears another's busy state. With
    /// <paramref name="text"/> a veil with that text appears if the work takes noticeably long; null holds without a veil.
    /// </summary>
    public BusyLease BeginBusy(string? text)
    {
        var lease = new BusyLease(this, text);
        _busy.Add(lease);
        UpdateBusy();
        return lease;
    }

    /// <summary>
    /// Before a long operation that asks for options first (print, export, extract): lets a value still being typed land
    /// and any edit in flight finish. Call it before the dialog and again after (opening a dialog commits the open field
    /// editor). False when the tab was closed meanwhile.
    /// </summary>
    public async Task<bool> SettleAsync()
    {
        await FlushPendingEditsAsync();
        return !_disposed;
    }

    private void EndBusy(BusyLease lease)
    {
        if (!_busy.Remove(lease) || _disposed) return;
        UpdateBusy();
    }

    private void UpdateBusy()
    {
        var text = _busy.LastOrDefault(b => b.Text is not null)?.Text;
        BusyText.Text = text ?? "";
        // Most edits finish in a blink; only show the veil if one takes noticeably long.
        if (text is null)
        {
            _busyDelay.Stop();
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
        else if (BusyOverlay.Visibility != Visibility.Visible && !_busyDelay.IsEnabled)
        {
            _busyDelay.Start();
        }
        Cursor = text is not null ? Cursors.AppStarting : null;
        if (!IsBusy)
        {
            var idle = _idle;
            _idle = null;
            idle?.TrySetResult();
        }
        RaiseState();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>One holder of the tab's busy state (see <see cref="BeginBusy"/>).</summary>
    public sealed class BusyLease(DocumentTab tab, string? text) : IDisposable
    {
        public string? Text { get; } = text;
        public void Dispose() => tab.EndBusy(this);
    }

    private void OnContentChanged(object? sender, EventArgs e)
    {
        _readAloud?.Stop(); // it reads the old pages' text
        CancelSearch();
        _results.Clear();
        _searched = null;
        _activeHit = -1;
        Viewer.SetSearchHits([]);
        Viewer.ShowHit(null);
        FindStatus.Text = SearchStatus.Text = "";

        // After undo/redo the same positions may hold different pages (e.g. undoing a Move): don't carry the selection over.
        int[] organizerSelection = IsOrganizing && !_steppingHistory && Organizer.PageCount == Session.Engine.PageCount ? Organizer.SelectedPages : [];
        _scheduler.Clear();
        // A form value or Fill & Sign edit leaves the pages as they were: keep the open field editor (typing continues).
        Viewer.SetEngine(Session.Engine, _scheduler, keepPosition: true, contentOnly: _contentOnlyCommit);
        DropViewEngine(); // made from the old bytes; chosen layers are re-applied once they are re-read
        RebuildThumbs();
        LoadOutline();
        if (IsOrganizing) Organizer.Load(Session.Engine, organizerSelection, organizerSelection.FirstOrDefault(Viewer.CurrentPage));
        _ = LoadAnnotationsAsync();
        _ = LoadReviewAsync();
        UpdateRedactBar();
        RaiseState();
    }

    // ───────────────────────────── Thumbnails ─────────────────────────────

    private void RebuildThumbs()
    {
        _thumbGeneration++;
        _syncingSelection = true;
        _thumbs.Clear();
        _thumbsWithImages.Clear();
        var engine = ViewEngine;
        if (_labels is not null && _labels.Length != engine.PageCount) _labels = null; // pages changed; re-read soon
        for (var i = 0; i < engine.PageCount; i++)
        {
            var g = engine.GetPage(i);
            var dipPerPt = Math.Min(ThumbBox / g.Width, ThumbBox / g.Height);
            var item = new ThumbItem(i, Math.Round(g.Width * dipPerPt), Math.Round(g.Height * dipPerPt), RequestThumb);
            item.SetLabel(PageLabel(i), engine.PageCount);
            _thumbs.Add(item);
        }
        _syncingSelection = false;
        SyncThumbSelection();
    }

    private void RequestThumb(ThumbItem item)
    {
        var engine = ViewEngine;
        var gen = _thumbGeneration;
        var g = engine.GetPage(item.Index);
        var scale = item.Width / g.Width * VisualTreeHelper.GetDpi(this).DpiScaleX;
        var invert = Viewer.InvertColors;
        _scheduler.Enqueue($"t{item.Index}", 1000 + item.Index * 1e-6,
            ct => PageViewer.ToBitmap(engine.Render(item.Index, scale, cancel: ct), invert),
            result =>
            {
                if (gen != _thumbGeneration) return;
                if (result is not BitmapSource bmp)
                {
                    item.MarkFailed();
                    return;
                }
                item.Image = bmp;
                // Keep the most recently rendered thumbnails; older ones re-render if scrolled back into view.
                _thumbsWithImages.AddLast(item);
                while (_thumbsWithImages.Count > MaxThumbImages)
                {
                    _thumbsWithImages.First!.Value.Release();
                    _thumbsWithImages.RemoveFirst();
                }
            },
            // A failed (or interrupted) render asks again when the item scrolls back into view, instead of never.
            failed: ex =>
            {
                if (ex is not (ObjectDisposedException or OperationCanceledException)) Log.Write($"Thumbnail of page {item.Index + 1} failed: {ex.Message}");
                item.MarkFailed();
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
        else if (DroppedPdfs(e).Length > 0 || FileTools.DroppedImages(e).Length > 0)
            e.Effects = DragDropEffects.Copy;
        else
            return;

        var (index, y) = InsertionPoint(e.GetPosition(Thumbs));
        _ = index;
        ShowInsertion(y);
    }

    private void OnThumbDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideInsertion();
        var (before, _) = InsertionPoint(e.GetPosition(Thumbs));
        DropPages(e, before);
    }

    /// <summary>
    /// Handles pages dragged from this or another tab, or PDF files from Explorer, dropped before page <paramref name="before"/>.
    /// Only reads what was dropped here; the work (reading files, password or warning prompts) runs after the drop has
    /// completed, so the drag source (Explorer) isn't left frozen mid-drag.
    /// </summary>
    public void DropPages(DragEventArgs e, int before)
    {
        if (!Session.IsPdf || IsBusy) return;
        var pages = e.Data.GetData(PagesFormat) as PageDragData;
        if (pages is null && FileTools.DroppedImages(e) is { Length: > 0 } images)
        {
            // Image files become pages here (after the drop completes, so Explorer isn't held).
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => _ = FileTools.InsertImages(this, images, before));
            return;
        }
        var files = pages is null ? DroppedPdfs(e) : [];
        if (pages is null && files.Length == 0) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => _ = DropPagesAsync(pages, files, before));
    }

    private async Task DropPagesAsync(PageDragData? d, string[] files, int before)
    {
        if (_disposed || !Session.IsPdf) return;
        if (d is not null)
        {
            if (d.Source == this)
            {
                await MovePagesAsync(d.Pages, before);
            }
            else if (!d.Source.IsDisposed)
            {
                var src = new PdfSource(d.Source.Session.Data, d.Source.Session.Password, d.Source.Session.DisplayName);
                await RunEdit($"Copy {Plural(d.Pages.Length)} from {src.Name}", (data, pw) => PageEditor.Insert(data, pw, src, d.Pages, before), before);
            }
            return;
        }

        foreach (var path in files.Reverse())
        {
            if (_disposed || Window.GetWindow(this) is not { } owner) return;
            if (PdfFiles.LoadForImport(owner, path) is not { } source) continue;
            await RunEdit($"Insert {Path.GetFileName(path)}", (data, pw) => PageEditor.Insert(data, pw, source, null, before), before);
        }
    }

    /// <summary>
    /// Moves <paramref name="pages"/> so they sit before original page <paramref name="before"/> (one undo step) and keeps
    /// them selected. Returns where the first one landed, or null when nothing moved.
    /// </summary>
    public async Task<int?> MovePagesAsync(int[] pages, int before)
    {
        if (pages.Length == 0 || pages.Any(p => p < 0 || p >= Viewer.PageCount)) return null;
        var order = PageEditor.MoveOrder(Viewer.PageCount, pages, before);
        if (order.SequenceEqual(Enumerable.Range(0, order.Length))) return null;
        var newFirst = Array.IndexOf(order, pages.Min());
        if (!await RunEdit($"Move {Plural(pages.Length)}", (data, pw) => PageEditor.Reorder(data, pw, order), newFirst)) return null;
        SelectPages(Enumerable.Range(newFirst, pages.Length));
        return newFirst;
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
            // The theme's accent (the Windows accent color; the highlight color in high contrast).
            var brush = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? SystemColors.HighlightBrush;
            dc.DrawLine(new Pen(brush, 3), new Point(8, _y), new Point(AdornedElement.RenderSize.Width - 8, _y));
        }
    }

    // ───────────────────────────── Bookmarks ─────────────────────────────

    /// <summary>The tree item whose header (not expander, not children, not a scroll bar) is under the mouse, if any.</summary>
    private static TreeViewItem? HeaderItemUnder(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            switch (d)
            {
                case System.Windows.Controls.Primitives.ToggleButton or System.Windows.Controls.Primitives.ScrollBar or ItemsPresenter:
                    return null;
                case TreeViewItem item:
                    return item;
                case TreeView:
                    return null;
            }
        }
        return null;
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

        var key = (query, FindMatchCase.IsChecked == true, FindWholeWords.IsChecked == true, FindIgnoreAccents.IsChecked == true);
        if (FindBar.Visibility != Visibility.Visible && SearchBox.Text.Trim() == query)
            key = (query, SearchMatchCase.IsChecked == true, SearchWholeWords.IsChecked == true, SearchIgnoreAccents.IsChecked == true);

        if (_searched != key)
        {
            _searchBackwards = backwards;
            _jumpWhenFound = true;
            _ = RunSearch(key.query, key.Item2, key.Item3, key.Item4);
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
        FindIgnoreAccents.IsChecked = SearchIgnoreAccents.IsChecked;
        _jumpWhenFound = true;
        _searchBackwards = false;
        return RunSearch(q, SearchMatchCase.IsChecked == true, SearchWholeWords.IsChecked == true, SearchIgnoreAccents.IsChecked == true);
    }

    private async Task RunSearch(string query, bool matchCase, bool wholeWords, bool ignoreAccents)
    {
        CancelSearch();
        var cts = _searchCts = new CancellationTokenSource();
        _searched = (query, matchCase, wholeWords, ignoreAccents);
        _results.Clear();
        _activeHit = -1;
        Viewer.SetSearchHits([]);
        Viewer.ShowHit(null);
        SearchBox.Text = query;
        SearchMatchCase.IsChecked = matchCase;
        SearchWholeWords.IsChecked = wholeWords;
        SearchIgnoreAccents.IsChecked = ignoreAccents;
        var failedPages = new List<int>();

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
                    try
                    {
                        var text = engine.GetText(p, cache: false, cts.Token);
                        foreach (var (s, e) in text.Find(query, matchCase, wholeWords, ignoreAccents))
                            batch.Add(new SearchHit(p, s, e, text.GetRects(s, e), text.Excerpt(s, e)));
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or ObjectDisposedException))
                    {
                        // A damaged page: skip it and keep searching the rest.
                        Log.Write($"Search on page {p + 1} failed: {ex.Message}");
                        lock (failedPages) failedPages.Add(p + 1);
                    }
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
            int[] failed;
            lock (failedPages) failed = [.. failedPages];
            if (failed.Length > 0)
                SearchStatus.Text += failed.Length == 1
                    ? $" Page {failed[0]} couldn't be searched (it may be damaged)."
                    : $" {failed.Length} pages couldn't be searched (they may be damaged): {string.Join(", ", failed.Take(10))}{(failed.Length > 10 ? "…" : "")}.";
            RedactHitsButton.Visibility = _results.Count > 0 && Session.IsPdf ? Visibility.Visible : Visibility.Collapsed;
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
        catch (Exception ex) // never leave "Searching…" up with an unobserved failure
        {
            Log.Write($"Search failed: {ex}");
            if (cts.IsCancellationRequested) return;
            SearchStatus.Text = FindStatus.Text = "Search failed";
            Notify($"Search didn't work: {ex.Message}", error: true);
        }
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
    }

    private bool _disposed;

    /// <summary>Night reading on or off: the page view, thumbnails and organizer cards all follow.</summary>
    public void SetDarkPages(bool on)
    {
        if (Viewer.InvertColors == on) return;
        Viewer.InvertColors = on;
        _scheduler.Clear(k => k[0] is 't' or 'o');
        RebuildThumbs();
        if (IsOrganizing) Organizer.Load(Session.Engine, Organizer.SelectedPages, Viewer.CurrentPage);
    }

    public void FocusContent()
    {
        if (IsOrganizing) Organizer.Focus();
        else Viewer.Focus();
    }

    /// <summary>True when keyboard focus is in the side pane or its icon rail (for F6 cycling).</summary>
    public bool IsPaneFocused => NavPane.IsKeyboardFocusWithin || Rail.IsKeyboardFocusWithin;

    /// <summary>F6: focus the open side pane (its selected item), or the icon rail when no pane is open.</summary>
    public void FocusPane()
    {
        switch (_pane)
        {
            case NavPaneKind.Pages:
                if (Thumbs.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, Thumbs.SelectedIndex)) is UIElement thumb) thumb.Focus();
                else Thumbs.Focus();
                break;
            case NavPaneKind.Bookmarks:
                if (Outline.Items.Count > 0 && Outline.ItemContainerGenerator.ContainerFromIndex(0) is UIElement first && Outline.SelectedItem is null) first.Focus();
                else Outline.Focus();
                break;
            case NavPaneKind.Search:
                SearchBox.Focus();
                break;
            case NavPaneKind.Comments:
                FocusList(CommentList, CommentFilter);
                break;
            case NavPaneKind.Attachments:
                FocusList(AttachmentList, AttachAddButton);
                break;
            case NavPaneKind.Layers:
                if (LayerTree.Items.Count > 0 && LayerTree.ItemContainerGenerator.ContainerFromIndex(0) is UIElement layer) layer.Focus();
                else LayerTree.Focus();
                break;
            case NavPaneKind.Signatures:
                if (FirstFocusable(SignatureList) is { } sig) sig.Focus();
                else PagesRail.Focus();
                break;
            default:
                PagesRail.Focus();
                break;
        }
    }

    private static UIElement? FirstFocusable(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Focusable: true, IsVisible: true } u and (TextBox or Button)) return u;
            if (FirstFocusable(child) is { } found) return found;
        }
        return null;
    }

    private static void FocusList(ListBox list, UIElement fallback)
    {
        var index = Math.Max(0, list.SelectedIndex);
        if (list.Items.Count > 0 && list.ItemContainerGenerator.ContainerFromIndex(index) is UIElement item) item.Focus();
        else fallback.Focus();
    }

    // ───────────────────────────── File on disk ─────────────────────────────

    /// <summary>Watches the file's folder (atomic saves replace the file) so changes by other programs show up.</summary>
    private void StartWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _watchedPath = Session.FilePath;
        if (_disposed || _watchedPath is not { } path || Path.GetDirectoryName(path) is not { } dir) return;
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            void OnEvent(object? sender, FileSystemEventArgs e)
            {
                var hit = string.Equals(e.FullPath, path, StringComparison.OrdinalIgnoreCase)
                          || e is RenamedEventArgs r && string.Equals(r.OldFullPath, path, StringComparison.OrdinalIgnoreCase);
                if (hit) Dispatcher.BeginInvoke(() =>
                {
                    _diskAcknowledged = false; // a new change: ask again even if the last one was dismissed
                    ScheduleDiskCheck();
                });
            }
            w.Changed += OnEvent;
            w.Created += OnEvent;
            w.Deleted += OnEvent;
            w.Renamed += OnEvent;
            w.Error += (_, _) => Dispatcher.BeginInvoke(ScheduleDiskCheck);
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Log.Write($"Can't watch {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    /// <summary>
    /// True while a modal dialog is open: a WPF one (thread modal), or a Windows one such as a file picker or the print
    /// dialog (those disable the window they belong to).
    /// </summary>
    private bool IsDialogOpen()
    {
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) return true;
        var hwnd = Window.GetWindow(this) is { } w ? new System.Windows.Interop.WindowInteropHelper(w).Handle : IntPtr.Zero;
        return hwnd != IntPtr.Zero && !IsWindowEnabled(hwnd);
    }

    /// <summary>After our own save: the copy on disk is ours now, so any "changed on disk" bar is stale.</summary>
    public void NoteSaved()
    {
        _diskAcknowledged = false;
        _reloadAttempts = 0;
        DiskBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>Checks the file shortly (several change notifications from one save collapse into one check).</summary>
    public void ScheduleDiskCheck()
    {
        if (_disposed) return;
        _diskDelay.Stop();
        _diskDelay.Start();
    }

    private void CheckDisk()
    {
        if (_disposed || Session.FilePath is null) return;
        // A modal dialog (an edit's options, a prompt, a file picker) may hold page numbers or the engine of this content:
        // look again once the edit in progress is done and every dialog is closed.
        if (IsBusy || _pendingWrites > 0 || IsDialogOpen())
        {
            ScheduleDiskCheck();
            return;
        }
        bool missing, changed;
        try
        {
            missing = Session.IsMissingOnDisk();
            changed = !missing && Session.HasChangedOnDisk();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Checking {Session.DisplayName} on disk failed: {ex.Message}");
            return;
        }

        var name = Session.DisplayName;
        if (missing)
        {
            if (_diskAcknowledged) return;
            ShowDiskBar($"{name} was moved, renamed or deleted. Your copy is still open here; save it to keep it.",
                "Save As…", () => Cmd.SaveAs.Execute(null, this), null, null);
        }
        else if (!changed)
        {
            _reloadAttempts = 0;
            _diskAcknowledged = false;
            DiskBar.Visibility = Visibility.Collapsed;
        }
        else if (!Session.IsDirty && !HasPendingEdits)
        {
            _ = ReloadFromDiskAsync($"{name} was changed by another program and has been reloaded");
        }
        else if (!_diskAcknowledged)
        {
            ShowDiskBar($"{name} was changed by another program. Reload it to see those changes (yours will be discarded), or keep your version (saving it will overwrite theirs).",
                "Reload", () => _ = ReloadFromDiskAsync($"Reloaded {name}"), "Keep mine", () =>
                {
                    _diskAcknowledged = true;
                    DiskBar.Visibility = Visibility.Collapsed;
                });
        }
    }

    /// <summary>Reopens the file from disk in place (same page and zoom). Discards unsaved changes and undo history.</summary>
    public async Task ReloadFromDiskAsync(string? toast = null)
    {
        // Let an open editor's value and any edit in flight land first, so nothing is written over the reloaded copy.
        await FlushPendingEditsAsync();
        if (_disposed || IsBusy || _pendingWrites > 0) return;
        // Read and parsed on a worker thread (big files don't freeze the window); busy meanwhile, so no edit cuts in.
        var busy = BeginBusy($"Reloading {Session.DisplayName}…");
        try
        {
            var reloaded = await Session.ReloadFromDiskAsync(() =>
            {
                if (_disposed) return false;
                // Values typed while the file was read are part of the copy being replaced.
                _formQueue.Clear();
                Viewer.ClearPendingValues();
                _signatureBaselineStale = true;
                busy.Dispose(); // idle again before the new content arrives, so its handlers see a normal tab
                return true;
            });
            if (!reloaded) return;
            _undoIds.Clear();
            _redoIds.Clear();
            _reloadAttempts = 0;
            DiskBar.Visibility = Visibility.Collapsed;
            if (toast is not null) Notify(toast);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Often the other program is still writing the file: try again a few times, then say so.
            Log.Write($"Reload of {Session.DisplayName} failed: {ex.Message}");
            if (++_reloadAttempts <= 3)
            {
                ScheduleDiskCheck();
                return;
            }
            _reloadAttempts = 0;
            ShowDiskBar($"{Session.DisplayName} was changed by another program, but it couldn't be reloaded: {ex.Message}",
                "Try again", () => _ = ReloadFromDiskAsync(toast), null, null);
        }
        finally
        {
            busy.Dispose();
        }
    }

    private void ShowDiskBar(string text, string primary, Action primaryAction, string? secondary, Action? secondaryAction)
    {
        DiskBarText.Text = text;
        DiskBarPrimary.Content = primary;
        _diskPrimary = primaryAction;
        DiskBarSecondary.Content = secondary;
        DiskBarSecondary.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        _diskSecondary = secondaryAction;
        DiskBar.Visibility = Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(DiskBar, text);
        if (System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(DiskBar) is { } peer)
            peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveViewDelay.Stop();
        _busyDelay.Stop();
        _diskDelay.Stop();
        _watcher?.Dispose();
        _watcher = null;
        // Release anything still waiting on this tab (their loops see _disposed and stop).
        _idle?.TrySetResult();
        _writesDone?.TrySetResult();
        _formQueue.Clear();
        _readAloud?.Dispose();
        SaveViewState();
        CancelSearch();
        _scheduler.Dispose();
        _viewEngine?.Dispose();
        _viewEngine = null;
        Session.Dispose();
    }
}
