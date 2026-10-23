using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PdfDesk.Core;

namespace PdfDesk.App;

/// <summary>A bookmark in the Bookmarks pane: what it shows, where it leads, and its place in the tree.</summary>
public sealed class BookmarkNode : INotifyPropertyChanged
{
    private string _title;
    private bool _expanded;
    private bool _selected;
    private bool _editing;
    private string _editText = "";

    public BookmarkNode(string title, int pageIndex, double? top, BookmarkNode? parent, bool bold = false, bool italic = false)
    {
        _title = title;
        PageIndex = pageIndex;
        Top = top;
        Parent = parent;
        Weight = bold ? FontWeights.SemiBold : FontWeights.Normal;
        Style = italic ? FontStyles.Italic : FontStyles.Normal;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public int PageIndex { get; }
    public double? Top { get; }
    public BookmarkNode? Parent { get; }
    public FontWeight Weight { get; }
    public FontStyle Style { get; }
    public ObservableCollection<BookmarkNode> Children { get; } = [];

    /// <summary>Not in the file yet: added with Ctrl+B and waiting for its title (Enter commits, Esc drops it).</summary>
    public bool IsNew { get; init; }

    public bool IsExpanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }

    public bool IsSelected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public bool IsEditing
    {
        get => _editing;
        set
        {
            if (!Set(ref _editing, value)) return;
            OnChanged(nameof(TitleVisibility));
            OnChanged(nameof(EditorVisibility));
        }
    }

    public string EditText
    {
        get => _editText;
        set => Set(ref _editText, value);
    }

    public Visibility TitleVisibility => _editing ? Visibility.Hidden : Visibility.Visible;
    public Visibility EditorVisibility => _editing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Index path from the top level ([2, 0] = first child of the third top-level bookmark).</summary>
    public int[] PathIn(IList<BookmarkNode> roots)
    {
        var path = new List<int>();
        for (var n = this; n is not null; n = n.Parent)
            path.Insert(0, (n.Parent?.Children ?? roots).IndexOf(n));
        return [.. path];
    }

    public IEnumerable<BookmarkNode> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);

    public override string ToString() => Title;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name!);
        return true;
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The Bookmarks pane: the outline as an editable tree. Shown at once from MuPDF's outline, then replaced by the
/// document's own bookmarks (read with <see cref="Bookmarks.Read"/>), which can be edited: add for the current view
/// (Ctrl+B), rename (F2 or double-click, inline), delete (Del), drag to reorder or nest, set the destination to the current
/// view. Each change is one undoable edit, made on the bookmarks read from the bytes being edited (by index path).
/// </summary>
public partial class DocumentTab
{
    private const string BookmarkFormat = "PdfDesk.Bookmark";

    private readonly ObservableCollection<BookmarkNode> _bookmarks = [];
    /// <summary>The bytes the tree was read from (edits need the tree to match the document); null = read-only outline.</summary>
    private byte[]? _bookmarksFrom;
    private int _outlineGeneration;
    private int[]? _selectAfterLoad;
    private bool _outlineJustNavigated;
    private bool _suppressNavigate;
    private Point _bookmarkDragStart;
    private BookmarkNode? _bookmarkDragNode;
    private BookmarkDropAdorner? _bookmarkDrop;

    private bool BookmarksEditable => Session.IsPdf && _bookmarksFrom is not null && ReferenceEquals(_bookmarksFrom, Session.Data);

    private void InitBookmarks()
    {
        Outline.ItemsSource = _bookmarks;
        // Screen readers name each bookmark by its title; selection and expansion live in the node.
        var style = new Style(typeof(TreeViewItem), Outline.TryFindResource(typeof(TreeViewItem)) as Style);
        style.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty, new System.Windows.Data.Binding(nameof(BookmarkNode.Title))));
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new System.Windows.Data.Binding(nameof(BookmarkNode.IsExpanded)) { Mode = System.Windows.Data.BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new System.Windows.Data.Binding(nameof(BookmarkNode.IsSelected)) { Mode = System.Windows.Data.BindingMode.TwoWay }));
        Outline.ItemContainerStyle = style;

        // A click that changes the selection navigates once; clicking the already-selected bookmark navigates again.
        Outline.SelectedItemChanged += (_, e) =>
        {
            BookmarkDeleteButton.IsEnabled = e.NewValue is BookmarkNode { IsNew: false } && BookmarksEditable;
            if (e.NewValue is not BookmarkNode n || _suppressNavigate || n.IsEditing) return;
            _outlineJustNavigated = Mouse.LeftButton == MouseButtonState.Pressed;
            NavigateTo(n);
        };
        Outline.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var again = !_outlineJustNavigated;
            _outlineJustNavigated = false;
            _bookmarkDragNode = null;
            // Only a click on the selected bookmark's own header re-navigates (not the expander, the scroll bar or blank space).
            if (again && Outline.SelectedItem is BookmarkNode { IsEditing: false } n && HeaderItemUnder(e.OriginalSource) is { IsSelected: true } item && ReferenceEquals(item.DataContext, n))
                NavigateTo(n);
        };
        Outline.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var item = HeaderItemUnder(e.OriginalSource);
            if (e.ClickCount == 2 && item?.DataContext is BookmarkNode { IsEditing: false } dbl)
            {
                BeginRename(dbl);
                e.Handled = true;
                return;
            }
            _bookmarkDragStart = e.GetPosition(Outline);
            _bookmarkDragNode = item?.DataContext as BookmarkNode;
        };
        Outline.PreviewMouseMove += OnBookmarkDragMove;
        Outline.DragOver += OnBookmarkDragOver;
        Outline.DragLeave += (_, _) => HideBookmarkDrop();
        Outline.Drop += OnBookmarkDrop;
        // Right-click selects the bookmark under the pointer (without jumping to it) for the context menu.
        Outline.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (HeaderItemUnder(e.OriginalSource)?.DataContext is not BookmarkNode n) return;
            _suppressNavigate = true;
            n.IsSelected = true;
            _suppressNavigate = false;
        };
        Outline.KeyDown += (_, e) =>
        {
            if (Outline.SelectedItem is not BookmarkNode n || n.IsEditing || Keyboard.Modifiers != ModifierKeys.None) return;
            switch (e.Key)
            {
                case Key.F2:
                    BeginRename(n);
                    e.Handled = true;
                    break;
                case Key.Delete:
                    _ = DeleteBookmark(n);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    NavigateTo(n);
                    e.Handled = true;
                    break;
            }
        };
        Outline.ContextMenu = BuildBookmarkMenu();
        BookmarkDeleteButton.Click += (_, _) =>
        {
            if (Outline.SelectedItem is BookmarkNode n) _ = DeleteBookmark(n);
        };
        BookmarkExpandButton.Click += (_, _) => ExpandAll(true);
        BookmarkCollapseButton.Click += (_, _) => ExpandAll(false);
        LoadOutline();
    }

    private ContextMenu BuildBookmarkMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action<BookmarkNode?> run, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => run(Outline.SelectedItem as BookmarkNode);
            menu.Items.Add(mi);
            return mi;
        }
        var add = Item("_Add Bookmark", _ => AddBookmarkForView(), Cmd.GestureText(Cmd.AddBookmark));
        var child = Item("Add _Child Bookmark", n => AddBookmarkForView(asChildOf: n));
        var rename = Item("_Rename", n => { if (n is not null) BeginRename(n); }, "F2");
        var delete = Item("_Delete", n => { if (n is not null) _ = DeleteBookmark(n); }, "Del");
        var retarget = Item("Set Destination to Current _View", n => { if (n is not null) _ = SetBookmarkDestination(n); });
        menu.Items.Add(new Separator());
        Item("_Expand All", _ => ExpandAll(true));
        Item("C_ollapse All", _ => ExpandAll(false));
        menu.Opened += (_, _) =>
        {
            var editable = BookmarksEditable && !IsBusy;
            var hasNode = Outline.SelectedItem is BookmarkNode { IsNew: false };
            add.IsEnabled = editable;
            child.IsEnabled = rename.IsEnabled = delete.IsEnabled = retarget.IsEnabled = editable && hasNode;
        };
        return menu;
    }

    // ───────────────────────────── Loading ─────────────────────────────

    /// <summary>
    /// Shows the outline: MuPDF's at once (first load), then the document's bookmarks read in the background, which can
    /// be edited. Expanded bookmarks stay expanded across edits.
    /// </summary>
    private void LoadOutline()
    {
        var generation = ++_outlineGeneration;
        var expanded = _bookmarks.SelectMany(b => b.SelfAndDescendants()).Where(b => b.IsExpanded).Select(b => PathKey(b.PathIn(_bookmarks))).ToHashSet();
        _bookmarksFrom = null;
        BookmarkTools.IsEnabled = false;
        if (_bookmarks.Count == 0 || !Session.IsPdf)
        {
            Fill(Session.Engine.GetOutline().Select(FromEngine).ToList(), expanded);
            if (!Session.IsPdf) return;
        }

        var data = Session.Data;
        var password = Session.Password;
        _ = Task.Run(() => Bookmarks.Read(data, password)).ContinueWith(t =>
        {
            if (_disposed || generation != _outlineGeneration || !ReferenceEquals(data, Session.Data)) return;
            if (t.IsFaulted)
            {
                Log.Write($"Bookmarks unreadable for editing: {t.Exception?.GetBaseException().Message}");
                return; // MuPDF's outline stays, read-only
            }
            Fill(t.Result.Select(b => FromCore(b, null)).ToList(), expanded);
            _bookmarksFrom = data;
            BookmarkTools.IsEnabled = true;
            if (_selectAfterLoad is { } path && Find(path) is { } select)
            {
                for (var p = select.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
                _suppressNavigate = true;
                select.IsSelected = true;
                _suppressNavigate = false;
            }
            _selectAfterLoad = null;
        }, TaskScheduler.FromCurrentSynchronizationContext());

        static BookmarkNode FromEngine(OutlineNode o) => Build(new BookmarkNode(o.Title, o.PageIndex, o.TargetY, null), o.Children, FromEngineChild);
        static BookmarkNode FromEngineChild(OutlineNode o, BookmarkNode parent) => Build(new BookmarkNode(o.Title, o.PageIndex, o.TargetY, parent), o.Children, FromEngineChild);
        static BookmarkNode FromCore(Bookmark b, BookmarkNode? parent)
        {
            var node = new BookmarkNode(b.Title, b.PageIndex, b.Top, parent, b.Bold, b.Italic) { IsExpanded = b.Open };
            foreach (var c in b.Children) node.Children.Add(FromCore(c, node));
            return node;
        }
        static BookmarkNode Build(BookmarkNode node, IReadOnlyList<OutlineNode> children, Func<OutlineNode, BookmarkNode, BookmarkNode> make)
        {
            foreach (var c in children) node.Children.Add(make(c, node));
            return node;
        }
    }

    private static string PathKey(int[] path) => string.Join('/', path);

    private void Fill(List<BookmarkNode> roots, HashSet<string> expanded)
    {
        _bookmarks.Clear();
        foreach (var r in roots) _bookmarks.Add(r);
        if (expanded.Count > 0)
            foreach (var n in _bookmarks.SelectMany(b => b.SelfAndDescendants()))
                if (expanded.Contains(PathKey(n.PathIn(_bookmarks)))) n.IsExpanded = true;
        NoBookmarks.Visibility = _bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoBookmarksText.Text = Session.IsPdf
            ? $"This document doesn't have a table of contents. Press {Cmd.GestureText(Cmd.AddBookmark)} to add a bookmark for the page you're reading."
            : "This document doesn't have a table of contents.";
    }

    private BookmarkNode? Find(int[] path)
    {
        IList<BookmarkNode> list = _bookmarks;
        BookmarkNode? node = null;
        foreach (var i in path)
        {
            if (i < 0 || i >= list.Count) return null;
            node = list[i];
            list = node.Children;
        }
        return node;
    }

    private void NavigateTo(BookmarkNode node)
    {
        if (node.PageIndex >= 0 && node.PageIndex < Viewer.PageCount) Viewer.GoToPage(node.PageIndex, node.Top);
    }

    private void ExpandAll(bool expand)
    {
        foreach (var n in _bookmarks.SelectMany(b => b.SelfAndDescendants()))
            if (n.Children.Count > 0) n.IsExpanded = expand;
    }

    // ───────────────────────────── Editing ─────────────────────────────

    /// <summary>True when the tree matches the document and no edit is running; else says why not.</summary>
    private bool CanEditBookmarks()
    {
        if (BookmarksEditable && !IsBusy) return true;
        Notify(!Session.IsPdf ? "Only PDF bookmarks can be edited." : IsBusy ? "Wait for the current change to finish." : "The bookmarks are still loading. Try again in a moment.");
        return false;
    }

    /// <summary>One undoable edit of the bookmarks, applied to those read from the bytes being edited.</summary>
    private Task<bool> EditBookmarks(string label, Func<IReadOnlyList<Bookmark>, IReadOnlyList<Bookmark>> change, int[]? selectAfter = null)
    {
        _selectAfterLoad = selectAfter;
        return RunEdit(label, (d, pw) => Bookmarks.SetOutline(d, pw, change(Bookmarks.Read(d, pw))), contentOnly: true);
    }

    /// <summary>
    /// Ctrl+B: a new bookmark for the page (and spot) in view, after the selected one (or as its child), titled with the
    /// selected text or the page; its title is typed in place and the bookmark is added on Enter.
    /// </summary>
    public void AddBookmarkForView(BookmarkNode? asChildOf = null)
    {
        if (!Session.IsPdf || Viewer.PageCount == 0) return;
        ShowPane(NavPaneKind.Bookmarks);
        if (!CanEditBookmarks()) return;
        CancelNewBookmarks();
        var (page, top) = Viewer.ViewTarget();
        var title = Viewer.HasSelection && Viewer.SelectionIsOnOnePage ? Viewer.GetSelectedText().ReplaceLineEndings(" ").Trim() : "";
        if (title.Length is 0 or > 120) title = $"Page {page + 1}";

        var selected = Outline.SelectedItem as BookmarkNode;
        BookmarkNode node;
        if (asChildOf is not null)
        {
            node = new BookmarkNode(title, page, top, asChildOf) { IsNew = true };
            asChildOf.Children.Add(node);
            asChildOf.IsExpanded = true;
        }
        else if (selected is { IsNew: false })
        {
            var siblings = selected.Parent?.Children ?? _bookmarks;
            node = new BookmarkNode(title, page, top, selected.Parent) { IsNew = true };
            siblings.Insert(siblings.IndexOf(selected) + 1, node);
        }
        else
        {
            node = new BookmarkNode(title, page, top, null) { IsNew = true };
            _bookmarks.Add(node);
        }
        NoBookmarks.Visibility = Visibility.Collapsed;
        BeginRename(node);
    }

    private void BeginRename(BookmarkNode node)
    {
        if (!node.IsNew && !CanEditBookmarks()) return;
        foreach (var other in _bookmarks.SelectMany(b => b.SelfAndDescendants()).Where(n => n.IsEditing && n != node)) other.IsEditing = false;
        _suppressNavigate = true;
        node.IsSelected = true;
        _suppressNavigate = false;
        node.EditText = node.Title;
        node.IsEditing = true;
        if (Outline.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem tvi) tvi.BringIntoView();
    }

    private void OnBookmarkEditorVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                box.Focus();
                box.SelectAll();
            });
    }

    private void OnBookmarkEditorKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: BookmarkNode node }) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = CommitRename(node);
            Outline.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelRename(node);
            Outline.Focus();
        }
    }

    private void OnBookmarkEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: BookmarkNode { IsEditing: true } node }) _ = CommitRename(node);
    }

    private void CancelRename(BookmarkNode node)
    {
        node.IsEditing = false;
        if (node.IsNew) Detach(node);
    }

    private void CancelNewBookmarks()
    {
        foreach (var n in _bookmarks.SelectMany(b => b.SelfAndDescendants()).Where(n => n.IsNew).ToList()) Detach(n);
    }

    private void Detach(BookmarkNode node)
    {
        (node.Parent?.Children ?? _bookmarks).Remove(node);
        NoBookmarks.Visibility = _bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CommitRename(BookmarkNode node)
    {
        if (!node.IsEditing) return;
        node.IsEditing = false;
        var title = node.EditText.ReplaceLineEndings(" ").Trim();
        if (node.IsNew)
        {
            var path = node.PathIn(_bookmarks);
            Detach(node); // the reload after the edit shows the real one
            if (title.Length == 0 || !CanEditBookmarks()) return;
            var bookmark = new Bookmark(title, node.PageIndex, node.Top);
            await EditBookmarks($"Add bookmark \"{Short(title)}\"", t => BookmarkTree.Insert(t, path[..^1], path[^1], bookmark), path);
            return;
        }
        if (title.Length == 0 || title == node.Title || !CanEditBookmarks()) return;
        var at = node.PathIn(_bookmarks);
        node.Title = title; // shown at once; the reload confirms it
        await EditBookmarks($"Rename bookmark to \"{Short(title)}\"", t => BookmarkTree.Replace(t, at, b => b with { Title = title }), at);
    }

    private async Task DeleteBookmark(BookmarkNode node)
    {
        if (node.IsNew)
        {
            CancelRename(node);
            return;
        }
        if (!CanEditBookmarks()) return;
        var path = node.PathIn(_bookmarks);
        var count = node.SelfAndDescendants().Count();
        var siblings = node.Parent?.Children ?? _bookmarks;
        // Select the next bookmark afterwards (or the previous, or the parent).
        int[]? next = siblings.Count > 1 ? [.. path[..^1], Math.Min(path[^1], siblings.Count - 2)] : path.Length > 1 ? path[..^1] : null;
        await EditBookmarks(count == 1 ? $"Delete bookmark \"{Short(node.Title)}\"" : $"Delete bookmark \"{Short(node.Title)}\" and {count - 1} inside it",
            t => BookmarkTree.Remove(t, path), next);
    }

    private async Task SetBookmarkDestination(BookmarkNode node)
    {
        if (!CanEditBookmarks()) return;
        var (page, top) = Viewer.ViewTarget();
        var path = node.PathIn(_bookmarks);
        await EditBookmarks($"Set destination of \"{Short(node.Title)}\" to page {page + 1}",
            t => BookmarkTree.Replace(t, path, b => b with { PageIndex = page, Top = top }), path);
    }

    private static string Short(string title) => title.Length <= 40 ? title : title[..39] + "…";

    // ───────────────────────────── Dev script ─────────────────────────────

    /// <summary>The tree as "Title[page]{children}", with whether it can be edited.</summary>
    internal string DevBookmarks()
    {
        static string Show(IEnumerable<BookmarkNode> nodes) => string.Join(" ", nodes.Select(n =>
            $"{n.Title}[{n.PageIndex + 1}{(n.Top is { } t ? $"@{t:0}" : "")}]{(n.Children.Count > 0 ? "{" + Show(n.Children) + "}" : "")}"));
        return $"{(BookmarksEditable ? "editable" : "read-only")}: {Show(_bookmarks)}";
    }

    /// <summary>Runs a bookmark command the way the pane does (see <see cref="DevScript"/>: <c>bookmark …</c>).</summary>
    internal async Task<string> DevBookmark(string arg)
    {
        for (var i = 0; i < 40 && !BookmarksEditable; i++) await Task.Delay(100);
        var parts = arg.Split(' ', 3);
        int[] P(string s) => [.. s.Split('.').Select(int.Parse)];
        BookmarkNode N(string s) => Find(P(s)) ?? throw new InvalidOperationException($"no bookmark at {s}");
        switch (parts[0])
        {
            case "add" or "child":
            {
                var title = parts[0] == "add" ? arg[4..] : parts[2];
                AddBookmarkForView(parts[0] == "child" ? N(parts[1]) : null);
                var node = _bookmarks.SelectMany(b => b.SelfAndDescendants()).First(n => n.IsNew);
                node.EditText = title;
                await CommitRename(node);
                break;
            }
            case "rename":
            {
                var node = N(parts[1]);
                BeginRename(node);
                node.EditText = parts[2];
                await CommitRename(node);
                break;
            }
            case "delete": await DeleteBookmark(N(parts[1])); break;
            case "dest": await SetBookmarkDestination(N(parts[1])); break;
            case "move":
            {
                var a = arg.Split(' ');
                var from = P(a[1]);
                int[] parent = a[2] == "-" ? [] : P(a[2]);
                await EditBookmarks("Move bookmark", t => BookmarkTree.Move(t, from, parent, int.Parse(a[3])));
                break;
            }
            default: return $"unknown: {arg}";
        }
        for (var i = 0; i < 40 && !BookmarksEditable; i++) await Task.Delay(100);
        return DevBookmarks();
    }

    // ───────────────────────────── Drag to reorder and nest ─────────────────────────────

    private enum DropPlace { Before, Inside, After }

    private void OnBookmarkDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _bookmarkDragNode is not { IsNew: false, IsEditing: false } node) return;
        var pos = e.GetPosition(Outline);
        if (Math.Abs(pos.X - _bookmarkDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _bookmarkDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _bookmarkDragNode = null;
        if (!BookmarksEditable || IsBusy) return;
        try
        {
            DragDrop.DoDragDrop(Outline, new DataObject(BookmarkFormat, node), DragDropEffects.Move);
        }
        finally
        {
            HideBookmarkDrop();
        }
    }

    /// <summary>The bookmark under the pointer and where a drop there puts the dragged one.</summary>
    private (BookmarkNode Target, DropPlace Place, FrameworkElement Header)? DropTargetAt(DragEventArgs e, BookmarkNode dragged)
    {
        if (HeaderItemUnder(e.OriginalSource) is not { DataContext: BookmarkNode target } item) return null;
        if (dragged.SelfAndDescendants().Contains(target)) return null; // not onto or into itself
        var header = HeaderOf(item);
        var y = e.GetPosition(header).Y / Math.Max(1, header.ActualHeight);
        var place = y < 0.28 ? DropPlace.Before : y > 0.72 ? DropPlace.After : DropPlace.Inside;
        return (target, place, header);
    }

    /// <summary>The element showing an item's own row (not its children).</summary>
    private static FrameworkElement HeaderOf(TreeViewItem item) =>
        item.Template?.FindName("PART_Header", item) as FrameworkElement ?? item;

    private void OnBookmarkDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(BookmarkFormat) is not BookmarkNode dragged || DropTargetAt(e, dragged) is not { } t)
        {
            HideBookmarkDrop();
            return;
        }
        e.Effects = DragDropEffects.Move;
        var layer = AdornerLayer.GetAdornerLayer(Outline);
        if (layer is null) return;
        if (_bookmarkDrop is null)
        {
            _bookmarkDrop = new BookmarkDropAdorner(Outline);
            layer.Add(_bookmarkDrop);
        }
        var box = t.Header.TransformToAncestor(Outline).TransformBounds(new Rect(t.Header.RenderSize));
        _bookmarkDrop.Show(box, t.Place == DropPlace.Inside ? null : t.Place == DropPlace.Before ? box.Top : box.Bottom);
    }

    private void OnBookmarkDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideBookmarkDrop();
        if (e.Data.GetData(BookmarkFormat) is not BookmarkNode dragged || DropTargetAt(e, dragged) is not { } t || !CanEditBookmarks()) return;
        var from = dragged.PathIn(_bookmarks);
        var target = t.Target.PathIn(_bookmarks);
        int[] parent;
        int index;
        switch (t.Place)
        {
            case DropPlace.Inside:
                parent = target;
                index = t.Target.Children.Count;
                break;
            case DropPlace.Before:
                parent = target[..^1];
                index = target[^1];
                break;
            default:
                parent = target[..^1];
                index = target[^1] + 1;
                break;
        }
        var label = t.Place == DropPlace.Inside ? $"Move \"{Short(dragged.Title)}\" into \"{Short(t.Target.Title)}\"" : $"Move bookmark \"{Short(dragged.Title)}\"";
        // Run after the drop completes, so the drag source isn't held while the edit runs.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            _ = EditBookmarks(label, tree => BookmarkTree.Move(tree, from, parent, index)));
    }

    private void HideBookmarkDrop()
    {
        if (_bookmarkDrop is null) return;
        AdornerLayer.GetAdornerLayer(Outline)?.Remove(_bookmarkDrop);
        _bookmarkDrop = null;
    }

    /// <summary>Where a dragged bookmark lands: a line between bookmarks, or a box around the one it goes into.</summary>
    private sealed class BookmarkDropAdorner(UIElement adorned) : Adorner(adorned)
    {
        private Rect _box;
        private double? _line;

        public void Show(Rect box, double? line)
        {
            _box = box;
            _line = line;
            IsHitTestVisible = false;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var brush = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? SystemColors.HighlightBrush;
            if (_line is { } y) dc.DrawLine(new Pen(brush, 2), new Point(_box.Left, y), new Point(AdornedElement.RenderSize.Width - 6, y));
            else dc.DrawRoundedRectangle(null, new Pen(brush, 1.5), _box, 4, 4);
        }
    }
}
