using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using InkvoltPdf.Core;

namespace InkvoltPdf.App.Viewer;

public sealed partial class PageViewer
{
    private PageViewerAutomationPeer? _peer;

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new PageViewerAutomationPeer(this);

    private void RaiseCurrentPageChanged()
    {
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
        _peer?.PageChanged();
    }

    internal string AccessibleName => PageCount == 0 ? "Document" : $"Page {_currentPage + 1} of {PageCount}";

    /// <summary>Visible pages, for screen readers.</summary>
    internal IReadOnlyList<int> AccessiblePages => _engine is null ? [] : VisiblePages(Viewport).ToList();

    /// <summary>A page's text if it is loaded; otherwise starts loading it and returns null.</summary>
    internal string? AccessibleText(int page) => page < PageCount ? TryGetPageText(page)?.Text : null;

    /// <summary>The visible part of a page, in screen pixels.</summary>
    internal Rect PageScreenRect(int page)
    {
        if (page >= _rects.Length || _rects[page].IsEmpty || PresentationSource.FromVisual(this) is null) return Rect.Empty;
        var r = Rect.Intersect(_rects[page], Viewport);
        if (r.IsEmpty) return Rect.Empty;
        r.Offset(-HorizontalOffset, -VerticalOffset);
        return new Rect(PointToScreen(r.TopLeft), PointToScreen(r.BottomRight));
    }
}

/// <summary>
/// The page view for UI Automation: a Document named "Page N of M" whose children are the visible pages; each page's
/// read-only Value is its text, so screen readers can read it.
/// </summary>
public sealed class PageViewerAutomationPeer(PageViewer owner) : ScrollViewerAutomationPeer(owner)
{
    private readonly Dictionary<int, PageAutomationPeer> _pages = [];
    private string _lastName = "";

    private PageViewer Viewer => (PageViewer)Owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetClassNameCore() => nameof(PageViewer);
    protected override string GetNameCore() => Viewer.AccessibleName;
    protected override string GetItemStatusCore() => Viewer.FocusedItemDescription ?? $"Zoom {Viewer.Zoom:P0}";

    protected override string GetHelpTextCore() =>
        "Page Up and Page Down move through the pages. Tab moves through links and form fields; Enter follows a link. " +
        "F7 turns caret browsing on: arrow keys move through the text and Shift with arrows selects it.";

    protected override List<AutomationPeer>? GetChildrenCore()
    {
        var children = base.GetChildrenCore() ?? [];
        var visible = Viewer.AccessiblePages;
        foreach (var gone in _pages.Keys.Except(visible).ToList()) _pages.Remove(gone);
        foreach (var page in visible)
        {
            if (!_pages.TryGetValue(page, out var peer)) _pages[page] = peer = new PageAutomationPeer(Viewer, page);
            children.Add(peer);
        }
        return children;
    }

    internal void PageChanged()
    {
        var name = Viewer.AccessibleName;
        if (name == _lastName) return;
        if (ListenerExists(AutomationEvents.PropertyChanged)) RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, _lastName, name);
        _lastName = name;
        ResetChildrenCache();
    }

    internal void FocusItemChanged()
    {
        if (ListenerExists(AutomationEvents.PropertyChanged))
            RaisePropertyChangedEvent(AutomationElementIdentifiers.ItemStatusProperty, "", GetItemStatusCore());
    }

    internal void TextArrived(int page)
    {
        if (_pages.TryGetValue(page, out var peer) && ListenerExists(AutomationEvents.PropertyChanged))
            peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, "", Viewer.AccessibleText(page) ?? "");
    }
}

/// <summary>One visible page: Name "Page N", Value (read-only) = the page's text.</summary>
public sealed class PageAutomationPeer(PageViewer viewer, int page) : AutomationPeer, IValueProvider
{
    public int Page { get; } = page;

    protected override string GetNameCore() => $"Page {Page + 1}";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    protected override string GetLocalizedControlTypeCore() => "page";
    protected override string GetClassNameCore() => "Page";
    protected override string GetAutomationIdCore() => $"Page{Page + 1}";
    protected override Rect GetBoundingRectangleCore() => viewer.PageScreenRect(Page);
    protected override bool IsOffscreenCore() => viewer.PageScreenRect(Page).IsEmpty;
    protected override Point GetClickablePointCore()
    {
        var r = viewer.PageScreenRect(Page);
        return r.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }
    protected override List<AutomationPeer>? GetChildrenCore() => null;
    protected override string GetAcceleratorKeyCore() => "";
    protected override string GetAccessKeyCore() => "";
    protected override string GetHelpTextCore() => "";
    protected override string GetItemStatusCore() => viewer.AccessibleText(Page) is null ? "Loading text" : "";
    protected override string GetItemTypeCore() => "";
    protected override AutomationPeer? GetLabeledByCore() => null;
    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
    protected override bool HasKeyboardFocusCore() => false;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override bool IsEnabledCore() => true;
    protected override bool IsKeyboardFocusableCore() => false;
    protected override bool IsPasswordCore() => false;
    protected override bool IsRequiredForFormCore() => false;
    protected override void SetFocusCore() { }
    public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Value ? this : null;

    bool IValueProvider.IsReadOnly => true;
    string IValueProvider.Value => viewer.AccessibleText(Page) ?? "";
    void IValueProvider.SetValue(string value) => throw new ElementNotEnabledException();
}
