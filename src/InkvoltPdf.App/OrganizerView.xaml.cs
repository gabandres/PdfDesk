using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InkvoltPdf.App.Viewer;
using InkvoltPdf.Core;

namespace InkvoltPdf.App;

/// <summary>One page card in the organizer grid. Its image renders lazily when a visible card binds to it.</summary>
public sealed class OrganizerItem(int index, double pageW, double pageH, Action<OrganizerItem> request) : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private bool _requested;
    private bool _selected;
    private bool _focused;
    private double _box = 160;

    public int Index { get; } = index;
    public double PageWidth { get; } = pageW;
    public double PageHeight { get; } = pageH;
    public string Label => (Index + 1).ToString();
    public string AccessibleName => $"Page {Index + 1}";
    public string Tooltip => $"Page {Index + 1} · {Organize.Inches(PageWidth)} × {Organize.Inches(PageHeight)} in · double-click to open";
    public double Box => _box;
    public double CellWidth => _box + 20;
    public double Width => Math.Round(PageWidth * Math.Min(_box / PageWidth, _box / PageHeight));
    public double Height => Math.Round(PageHeight * Math.Min(_box / PageWidth, _box / PageHeight));

    public void SetBox(double box)
    {
        if (Math.Abs(box - _box) < 0.5) return;
        var grew = box > _box * 1.25;
        _box = box;
        Raise(nameof(Box), nameof(CellWidth), nameof(Width), nameof(Height));
        if (grew) Release(); // re-render sharper when the cards get much bigger
    }

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
            Raise(nameof(Image));
        }
    }

    public void Release()
    {
        _requested = false;
        Raise(nameof(Image));
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Raise(nameof(IsSelected));
        }
    }

    public bool IsFocused
    {
        get => _focused;
        set
        {
            if (_focused == value) return;
            _focused = value;
            Raise(nameof(IsFocused));
        }
    }

    private void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class OrganizerRow(IReadOnlyList<OrganizerItem> items)
{
    public IReadOnlyList<OrganizerItem> Items { get; } = items;
}

/// <summary>
/// "Organize pages": a grid of large page cards with click / Ctrl / Shift selection, keyboard navigation,
/// drag to reorder (or onto another tab to copy), hover actions, and the same context menu as the thumbnails.
/// Rows are virtualized, so a thousand-page document stays responsive.
/// </summary>
public partial class OrganizerView : UserControl
{
    private const string PagesFormat = "InkvoltPdf.Pages";

    private readonly List<OrganizerItem> _items = [];
    private readonly ObservableCollection<OrganizerRow> _rows = [];
    private DocumentTab? _tab;
    private RenderScheduler? _scheduler;
    private int _generation;
    private int _columns = 1;
    private int _anchor = -1;
    private int _focus = -1;
    private Point _dragStart;
    private OrganizerItem? _pressed;
    private bool _pressedWasSelected;
    private InsertionAdorner? _insertion;
    private ScrollViewer? _scroller;

    public event EventHandler? SelectionChanged;
    public event EventHandler<int>? OpenPage;
    public event EventHandler? Done;

    public OrganizerView()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        SizeSlider.Value = AppSettings.Current.OrganizerThumbSize;
        SizeSlider.ValueChanged += (_, _) =>
        {
            AppSettings.Current.OrganizerThumbSize = SizeSlider.Value;
            ApplySize();
        };
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) Regroup(); };
        SelectAllButton.Click += (_, _) => SelectAll();
        SelectNoneButton.Click += (_, _) => SetSelection([], keepFocus: true);
        DoneButton.Click += (_, _) => Done?.Invoke(this, EventArgs.Empty);

        Rows.PreviewMouseLeftButtonDown += OnPreviewDown;
        Rows.PreviewMouseMove += OnPreviewMove;
        Rows.PreviewMouseLeftButtonUp += OnPreviewUp;
        Rows.PreviewMouseRightButtonDown += OnRightDown;
        Rows.DragOver += OnDragOver;
        Rows.DragLeave += (_, _) => HideInsertion();
        Rows.Drop += OnDrop;
        Rows.Loaded += (_, _) => _scroller = Rows.Template.FindName("Scroller", Rows) as ScrollViewer;
        PreviewKeyDown += OnKey;
    }

    public int[] SelectedPages => [.. _items.Where(i => i.IsSelected).Select(i => i.Index)];
    public int PageCount => _items.Count;

    /// <summary>The page the organizer's "current page" means: the focused card, else the first selected one, else -1.</summary>
    public int FocusedPage => _focus >= 0 && _focus < _items.Count ? _focus : _items.FirstOrDefault(i => i.IsSelected)?.Index ?? -1;

    public void Attach(DocumentTab tab, RenderScheduler scheduler)
    {
        _tab = tab;
        _scheduler = scheduler;
    }

    /// <summary>Rebuilds the cards for the current document, keeping the given pages selected.</summary>
    public void Load(PdfEngine engine, IEnumerable<int> select, int focus)
    {
        _generation++;
        _items.Clear();
        for (var i = 0; i < engine.PageCount; i++)
        {
            var g = engine.GetPage(i);
            var item = new OrganizerItem(i, g.Width, g.Height, RequestImage);
            item.SetBox(SizeSlider.Value);
            _items.Add(item);
        }
        Regroup();
        SetSelection(select, keepFocus: false);
        _focus = Math.Clamp(focus, -1, _items.Count - 1);
        if (_focus >= 0)
        {
            _items[_focus].IsFocused = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => BringIntoView(_focus));
        }
    }

    private void RequestImage(OrganizerItem item)
    {
        if (_tab is null || _scheduler is null) return;
        var engine = _tab.Session.Engine;
        var gen = _generation;
        var scale = item.Width / item.PageWidth * VisualTreeHelper.GetDpi(this).DpiScaleX;
        var invert = _tab.PageView.InvertColors;
        _scheduler.Enqueue($"o{item.Index}", 900 + item.Index * 1e-6, () => PageViewer.ToBitmap(engine.Render(item.Index, scale), invert), r =>
        {
            if (gen == _generation && r is BitmapSource bmp) item.Image = bmp;
        });
    }

    private void ApplySize()
    {
        foreach (var i in _items) i.SetBox(SizeSlider.Value);
        Regroup();
    }

    private void Regroup()
    {
        var width = Math.Max(1, ActualWidth - 48);
        var cell = SizeSlider.Value + 20 + 8;
        _columns = Math.Max(1, (int)(width / cell));
        _rows.Clear();
        foreach (var chunk in _items.Chunk(_columns)) _rows.Add(new OrganizerRow(chunk));
    }

    // ───────────────────────────── Selection ─────────────────────────────

    private void SetSelection(IEnumerable<int> pages, bool keepFocus)
    {
        var set = pages.ToHashSet();
        foreach (var i in _items) i.IsSelected = set.Contains(i.Index);
        if (!keepFocus && set.Count > 0) _anchor = set.Min();
        UpdateSelectionText();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll() => SetSelection(Enumerable.Range(0, _items.Count), keepFocus: true);

    private void UpdateSelectionText()
    {
        var n = _items.Count(i => i.IsSelected);
        SelectionText.Text = n == 0 ? $"{_items.Count} pages" : $"{n} of {_items.Count} pages selected";
    }

    private void MoveFocus(int index, bool extend, bool toggle = false)
    {
        if (_items.Count == 0) return;
        index = Math.Clamp(index, 0, _items.Count - 1);
        if (_focus >= 0 && _focus < _items.Count) _items[_focus].IsFocused = false;
        _focus = index;
        _items[index].IsFocused = true;

        if (extend && _anchor >= 0)
        {
            var (a, b) = (Math.Min(_anchor, index), Math.Max(_anchor, index));
            SetSelection(Enumerable.Range(a, b - a + 1), keepFocus: true);
        }
        else if (toggle)
        {
            _items[index].IsSelected = !_items[index].IsSelected;
            _anchor = index;
            UpdateSelectionText();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _anchor = index;
            SetSelection([index], keepFocus: true);
        }
        BringIntoView(index);
    }

    private void BringIntoView(int index)
    {
        if (_columns <= 0 || _rows.Count == 0) return;
        var row = Math.Clamp(index / _columns, 0, _rows.Count - 1);
        // Let the virtualizing panel realize and reveal the row (row heights vary with page shapes).
        if (Panel(Rows) is { } panel) panel.BringIndexIntoViewPublic(row);
        if (Rows.ItemContainerGenerator.ContainerFromIndex(row) is FrameworkElement container) container.BringIntoView();
    }

    private static VirtualizingStackPanel? Panel(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is VirtualizingStackPanel v) return v;
            if (Panel(c) is { } found) return found;
        }
        return null;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox or Slider) return;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var f = Math.Max(0, _focus);
        // Alt+Shift+arrows move the selected pages (with Alt held, WPF reports the arrow as a system key).
        if (Keyboard.Modifiers == (ModifierKeys.Alt | ModifierKeys.Shift) && e.Key == Key.System
            && e.SystemKey is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            _ = MoveSelectionAsync(e.SystemKey);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Left: MoveFocus(f - 1, shift); break;
            case Key.Right: MoveFocus(f + 1, shift); break;
            case Key.Up: MoveFocus(f - _columns, shift); break;
            case Key.Down: MoveFocus(f + _columns, shift); break;
            case Key.Home: MoveFocus(0, shift); break;
            case Key.End: MoveFocus(_items.Count - 1, shift); break;
            case Key.Space when ctrl: MoveFocus(f, false, toggle: true); break;
            case Key.A when ctrl: SelectAll(); break;
            case Key.Enter when _focus >= 0: OpenPage?.Invoke(this, _focus); break;
            case Key.Escape: Done?.Invoke(this, EventArgs.Empty); break;
            case Key.Delete when !ctrl && !shift: Cmd.DeleteSelected.Execute(null, this); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Alt+Shift+arrow: moves the selected pages one place (or one row) and tells screen readers where they went.</summary>
    private async Task MoveSelectionAsync(Key key)
    {
        if (_tab is null || _tab.IsBusy || !_tab.Session.IsPdf) return;
        var pages = SelectedPages;
        if (pages.Length == 0 && _focus >= 0 && _focus < _items.Count) pages = [_focus];
        if (pages.Length == 0) return;
        int min = pages.Min(), max = pages.Max(), count = _items.Count;
        int? before = key switch
        {
            Key.Left when min > 0 => min - 1,
            Key.Right when max < count - 1 => max + 2,
            Key.Up when min > 0 => Math.Max(0, min - _columns),
            Key.Down when max < count - 1 => Math.Min(count, max + _columns + 1),
            _ => null,
        };
        if (before is not { } b)
        {
            Announce(key is Key.Left or Key.Up ? "Already at the start" : "Already at the end");
            return;
        }
        if (await _tab.MovePagesAsync(pages, b) is not { } first) return;
        var n = pages.Length;
        Announce(n == 1 ? $"Moved to position {first + 1} of {count}" : $"Moved {n} pages to positions {first + 1}–{first + n} of {count}");
    }

    /// <summary>Speaks <paramref name="message"/> through the selection line (a polite live region).</summary>
    private void Announce(string message)
    {
        SelectionText.Text = message;
        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(SelectionText)?
            .RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    // ───────────────────────────── Mouse ─────────────────────────────

    private static OrganizerItem? ItemFrom(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ButtonBase) return null; // hover action buttons handle their own clicks
            if (d is FrameworkElement { DataContext: OrganizerItem item }) return item;
        }
        return null;
    }

    private void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (e.OriginalSource is DependencyObject d && IsInButton(d)) return;
        var item = ItemFrom(e.OriginalSource);
        _dragStart = e.GetPosition(Rows);
        _pressed = item;
        if (item is null)
        {
            if (e.OriginalSource is not ScrollBar && !IsInScrollBar(e.OriginalSource as DependencyObject)) SetSelection([], keepFocus: true);
            return;
        }
        if (e.ClickCount == 2)
        {
            OpenPage?.Invoke(this, item.Index);
            e.Handled = true;
            return;
        }

        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        _pressedWasSelected = item.IsSelected;
        if (shift) MoveFocus(item.Index, extend: true);
        else if (ctrl) MoveFocus(item.Index, false, toggle: true);
        else if (!item.IsSelected) MoveFocus(item.Index, false);
        else
        {
            // Clicking inside an existing selection keeps it (so it can be dragged); a plain click narrows on mouse up.
            if (_focus >= 0 && _focus < _items.Count) _items[_focus].IsFocused = false;
            _focus = item.Index;
            item.IsFocused = true;
        }
        e.Handled = true;
    }

    private void OnPreviewUp(object sender, MouseButtonEventArgs e)
    {
        var item = ItemFrom(e.OriginalSource);
        if (item is not null && item == _pressed && _pressedWasSelected && Keyboard.Modifiers == ModifierKeys.None)
            MoveFocus(item.Index, false);
        _pressed = null;
    }

    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (ItemFrom(e.OriginalSource) is { } item && !item.IsSelected) MoveFocus(item.Index, false);
    }

    private static bool IsInButton(DependencyObject d)
    {
        for (var x = d; x is not null; x = VisualTreeHelper.GetParent(x))
        {
            if (x is ButtonBase) return true;
            if (x is OrganizerView) return false;
        }
        return false;
    }

    private static bool IsInScrollBar(DependencyObject? d)
    {
        for (var x = d; x is not null; x = VisualTreeHelper.GetParent(x))
            if (x is ScrollBar) return true;
        return false;
    }

    private void OnItemAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OrganizerItem item, Tag: string action } || _tab is null) return;
        // Act on the hovered page; if it is part of a selection, act on the whole selection.
        if (!item.IsSelected) MoveFocus(item.Index, false);
        switch (action)
        {
            case "left": Cmd.RotateLeft.Execute(null, this); break;
            case "right": Cmd.RotateRight.Execute(null, this); break;
            case "delete": Cmd.DeleteSelected.Execute(null, this); break;
        }
        e.Handled = true;
    }

    private void OnPreviewMove(object sender, MouseEventArgs e)
    {
        if (_pressed is null || e.LeftButton != MouseButtonState.Pressed || _tab is null) return;
        var pos = e.GetPosition(Rows);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var pages = SelectedPages;
        _pressed = null;
        if (pages.Length == 0 || !_tab.Session.IsPdf || _tab.IsBusy) return;
        try
        {
            DragDrop.DoDragDrop(Rows, new DataObject(PagesFormat, new PageDragData(_tab, pages)), DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            HideInsertion();
        }
    }

    // ───────────────────────────── Drop ─────────────────────────────

    private (int Before, Rect Marker)? DropTarget(Point p)
    {
        if (Rows.InputHitTest(p) is DependencyObject hit)
        {
            for (var d = hit; d is not null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is Border { DataContext: OrganizerItem item } card && card.Name == "Card")
                {
                    var tl = card.TranslatePoint(new Point(0, 0), Rows);
                    var after = p.X > tl.X + card.ActualWidth / 2;
                    var x = after ? tl.X + card.ActualWidth + 2 : tl.X - 2;
                    return (after ? item.Index + 1 : item.Index, new Rect(x - 1.5, tl.Y + 4, 3, card.ActualHeight - 8));
                }
            }
        }
        // In a gap between cards or rows: use the nearest card, not "the end of the document".
        Border? nearest = null;
        var best = double.MaxValue;
        foreach (var card in Cards(Rows))
        {
            var tl = card.TranslatePoint(new Point(0, 0), Rows);
            var r = new Rect(tl, card.RenderSize);
            var dx = p.X < r.Left ? r.Left - p.X : p.X > r.Right ? p.X - r.Right : 0;
            var dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
            var d = dx * dx + dy * dy * 4; // prefer the same row
            if (d < best)
            {
                best = d;
                nearest = card;
            }
        }
        if (nearest is { DataContext: OrganizerItem near })
        {
            var tl = nearest.TranslatePoint(new Point(0, 0), Rows);
            var after = p.X > tl.X + nearest.ActualWidth / 2;
            var x = after ? tl.X + nearest.ActualWidth + 2 : tl.X - 2;
            return (after ? near.Index + 1 : near.Index, new Rect(x - 1.5, tl.Y + 4, 3, nearest.ActualHeight - 8));
        }
        return _items.Count == 0 ? null : (_items.Count, Rect.Empty);
    }

    private static IEnumerable<Border> Cards(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is Border { Name: "Card" } b) yield return b;
            else
                foreach (var x in Cards(c)) yield return x;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (_tab is null || !_tab.Session.IsPdf || _tab.IsBusy) return;
        if (e.Data.GetData(PagesFormat) is PageDragData d) e.Effects = d.Source == _tab ? DragDropEffects.Move : DragDropEffects.Copy;
        else if (DocumentTab.DroppedPdfs(e).Length > 0 || FileTools.DroppedImages(e).Length > 0) e.Effects = DragDropEffects.Copy;
        else return;

        // Auto-scroll near the edges while dragging.
        var p = e.GetPosition(Rows);
        if (_scroller is not null)
        {
            if (p.Y < 40) _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - 20);
            else if (p.Y > Rows.ActualHeight - 40) _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + 20);
        }
        if (DropTarget(p) is { Marker.IsEmpty: false } t) ShowInsertion(t.Marker);
        else HideInsertion();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideInsertion();
        if (_tab is null || DropTarget(e.GetPosition(Rows)) is not { } t) return;
        _tab.DropPages(e, t.Before); // the work runs after the drop completes
    }

    private void ShowInsertion(Rect marker)
    {
        if (_insertion is null)
        {
            if (AdornerLayer.GetAdornerLayer(Rows) is not { } layer) return;
            _insertion = new InsertionAdorner(Rows);
            layer.Add(_insertion);
        }
        _insertion.Marker = marker;
    }

    private void HideInsertion()
    {
        if (_insertion is null) return;
        AdornerLayer.GetAdornerLayer(Rows)?.Remove(_insertion);
        _insertion = null;
    }

    private sealed class InsertionAdorner(UIElement adorned) : Adorner(adorned)
    {
        private Rect _marker;

        public Rect Marker
        {
            get => _marker;
            set
            {
                _marker = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            IsHitTestVisible = false;
            var brush = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
            dc.DrawRoundedRectangle(brush, null, _marker, 1.5, 1.5);
        }
    }
}
