using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using InkvoltPdf.Core;

namespace InkvoltPdf.App.Viewer;

/// <summary>
/// Keyboard focus inside the page view: Tab / Shift+Tab move through form fields and the links on the visible pages in
/// reading order (a radio group is one stop: arrow keys move within it, Space selects); past the last (or before the
/// first) stop the key is left alone so focus moves on to the rest of the window. Enter follows a focused link.
/// </summary>
public sealed partial class PageViewer
{
    private (int Page, PageLink Link)? _focusLink;
    private ToolTip? _linkTip;
    private PageLink? _tipLink;

    private readonly record struct TabStop(int Page, RectD Rect, FormWidget? Widget, PageLink? Link);

    /// <summary>Drops fields, Fill &amp; Sign items and the focused item (the pages they were on may have changed).</summary>
    private void ClearFormState()
    {
        _widgetsByPage = [];
        _fillOrder = [];
        _stampsByPage = [];
        _focusWidget = _hoverWidget = null;
        _focusLink = null;
        HideLinkTip();
        _surface.InvalidateVisual();
    }

    private void ClearFocusLink()
    {
        if (_focusLink is null) return;
        _focusLink = null;
        HideLinkTip();
        _surface.InvalidateVisual();
    }

    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusWithinChanged(e);
        // Focus coming in from elsewhere in the window starts over at the view itself: the next Tab goes to the first
        // field or link on the visible pages, Shift+Tab to the last one.
        if (e.NewValue is true && _editor is null)
        {
            _focusWidget = null;
            _focusLink = null;
        }
        if (e.NewValue is false) HideLinkTip();
        _surface.InvalidateVisual();
    }

    private static bool SameWidget(FormWidget a, FormWidget b) => a.FieldName == b.FieldName && a.Rect == b.Rect;

    private static (double Row, double X) ReadingKey(RectD r) => (Math.Round(r.Y / 6), r.X);

    /// <summary>Fields (in the document's fill order) merged with the links of the visible pages, page by page.</summary>
    private List<TabStop> TabStops()
    {
        // A radio group is one stop: the focused button, else the chosen one, else the first.
        var radio = new Dictionary<string, FormWidget>();
        foreach (var w in _fillOrder)
            if (w.Kind == FormFieldKind.Radio && (!radio.TryGetValue(w.FieldName, out var cur) || (ValueOf(w) == w.OnState && ValueOf(cur) != cur.OnState)))
                radio[w.FieldName] = w;
        if (_focusWidget is { Kind: FormFieldKind.Radio } fr && _fillOrder.Exists(w => SameWidget(w, fr))) radio[fr.FieldName] = fr;

        var fields = new Dictionary<int, List<TabStop>>();
        foreach (var w in _fillOrder)
        {
            if (w.Kind == FormFieldKind.Radio && !ReferenceEquals(radio[w.FieldName], w)) continue;
            if (!fields.TryGetValue(w.PageIndex, out var list)) fields[w.PageIndex] = list = [];
            list.Add(new TabStop(w.PageIndex, w.Rect, w, null));
        }

        var linkPages = VisiblePages(Viewport).ToHashSet();
        if (_focusLink is { } fl) linkPages.Add(fl.Page);
        var links = new Dictionary<int, List<TabStop>>();
        foreach (var p in linkPages)
            if (_links.TryGetValue(p, out var ls) && ls.Count > 0)
                links[p] = [.. ls.Select(l => new TabStop(p, l.Area, null, l)).OrderBy(s => ReadingKey(s.Rect))];

        var stops = new List<TabStop>();
        foreach (var p in fields.Keys.Concat(links.Keys).Distinct().Order())
        {
            var f = fields.GetValueOrDefault(p) ?? [];
            var l = links.GetValueOrDefault(p) ?? [];
            int i = 0, j = 0;
            while (i < f.Count || j < l.Count)
            {
                if (j >= l.Count || (i < f.Count && ReadingKey(f[i].Rect).CompareTo(ReadingKey(l[j].Rect)) <= 0)) stops.Add(f[i++]);
                else stops.Add(l[j++]);
            }
        }
        return stops;
    }

    /// <summary>
    /// Moves to the next (previous) field or link. False when there is none in that direction, so the caller lets the
    /// key go and focus leaves the page view.
    /// </summary>
    private bool MoveFocusStop(bool backwards, FormWidget? from = null)
    {
        if (from is not null)
        {
            _focusWidget = from;
            _focusLink = null;
        }
        var stops = TabStops();
        if (stops.Count == 0) return false;

        var index = -1;
        if (_focusLink is { } cur)
            index = stops.FindIndex(s => s.Link is not null && s.Page == cur.Page && s.Link == cur.Link);
        else if (_focusWidget is { } fw)
            index = stops.FindIndex(s => s.Widget is { } w && (SameWidget(w, fw) || (fw.Kind == FormFieldKind.Radio && w.Kind == FormFieldKind.Radio && w.FieldName == fw.FieldName)));

        int next;
        if (index < 0)
        {
            var visible = VisiblePages(Viewport).ToList();
            var first = visible.Count > 0 ? visible[0] : _currentPage;
            var last = visible.Count > 0 ? visible[^1] : _currentPage;
            next = backwards ? stops.FindLastIndex(s => s.Page <= last) : stops.FindIndex(s => s.Page >= first);
            if (next < 0) next = backwards ? stops.Count - 1 : 0;
        }
        else
        {
            next = index + (backwards ? -1 : 1);
            if (next < 0 || next >= stops.Count) return false;
        }
        FocusStop(stops[next]);
        return true;
    }

    private void FocusStop(TabStop stop)
    {
        if (!IsKeyboardFocusWithin) Focus(); // first: focus arriving from outside resets the focused item
        if (stop.Widget is { } w)
        {
            _focusLink = null;
            HideLinkTip();
            _focusWidget = w;
            EnsureVisible(w.PageIndex, w.Rect);
            if (w.Kind is FormFieldKind.Text or FormFieldKind.Choice) OpenFieldEditor(w);
            else Focus();
        }
        else if (stop.Link is { } link)
        {
            _focusWidget = null;
            _focusLink = (stop.Page, link);
            EnsureVisible(stop.Page, link.Area);
            Focus();
            ShowLinkTip(stop.Page, link, keyboard: true);
        }
        _surface.InvalidateVisual();
        _peer?.FocusItemChanged();
    }

    /// <summary>Leaves the page view for the next (previous) control in the window.</summary>
    private void LeaveViewer(bool backwards)
    {
        Focus();
        MoveFocus(new TraversalRequest(backwards ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
    }

    /// <summary>Viewer keys for fields and links: Tab / Shift+Tab, arrows in a radio group, Space / Enter, Esc.</summary>
    private bool HandleFormKey(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Tab && (mods & ~ModifierKeys.Shift) == 0)
            return MoveFocusStop((mods & ModifierKeys.Shift) != 0);

        if (mods == ModifierKeys.None && _focusWidget is { Kind: FormFieldKind.Radio } rw && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            var group = _fillOrder.Where(w => w.Kind == FormFieldKind.Radio && w.FieldName == rw.FieldName).ToList();
            var i = group.FindIndex(w => SameWidget(w, rw));
            if (i < 0) return false;
            var next = group[(i + (e.Key is Key.Up or Key.Left ? -1 : 1) + group.Count) % group.Count];
            _focusWidget = next;
            EnsureVisible(next.PageIndex, next.Rect);
            _surface.InvalidateVisual();
            _peer?.FocusItemChanged();
            return true;
        }

        if (mods == ModifierKeys.None && e.Key == Key.Enter && _focusLink is { } fl)
        {
            ActivateLink(fl.Link);
            return true;
        }
        if (e.Key is Key.Space or Key.Enter && mods == ModifierKeys.None && _focusWidget is { } fw && IsFillable(fw))
        {
            ActivateWidget(fw);
            return true;
        }
        if (e.Key == Key.Escape && (_focusWidget is not null || _focusLink is not null))
        {
            _focusWidget = null;
            ClearFocusLink();
            _surface.InvalidateVisual();
            _peer?.FocusItemChanged();
            return true;
        }
        return false;
    }

    // ───────────────────────────── Link tips ─────────────────────────────

    /// <summary>Where a link goes, shown before following it (so a misleading link text can't hide its target).</summary>
    internal static string LinkDescription(PageLink link) =>
        link.Uri is { } u ? (u.Length > 300 ? u[..300] + "…" : u) : $"Go to page {link.PageIndex + 1}";

    private void ShowLinkTip(int page, PageLink link, bool keyboard)
    {
        if (_linkTip is { IsOpen: true } && _tipLink == link) return;
        _linkTip ??= new ToolTip();
        _linkTip.IsOpen = false;
        _tipLink = link;
        _linkTip.Content = LinkDescription(link);
        _linkTip.PlacementTarget = _surface;
        if (keyboard)
        {
            _linkTip.Placement = PlacementMode.Bottom;
            _linkTip.PlacementRectangle = ToSurfaceRect(page, link.Area);
        }
        else
        {
            _linkTip.Placement = PlacementMode.Mouse;
            _linkTip.PlacementRectangle = Rect.Empty;
        }
        _linkTip.IsOpen = true;
    }

    private void HideLinkTip()
    {
        _tipLink = null;
        _tipAnnot = null;
        if (_linkTip is { IsOpen: true }) _linkTip.IsOpen = false;
    }

    /// <summary>What has keyboard focus inside the page (for screen readers).</summary>
    internal string? FocusedItemDescription =>
        _focusLink is { } fl ? $"Link: {LinkDescription(fl.Link)}"
        : _focusWidget is { } w ? $"{w.Kind switch { FormFieldKind.CheckBox => "Check box", FormFieldKind.Radio => "Radio button", FormFieldKind.Choice => "List", _ => "Field" }} {w.FieldName}: {ValueOf(w)}"
        : null;
}
