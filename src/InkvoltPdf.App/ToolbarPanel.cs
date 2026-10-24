using System.Windows;
using System.Windows.Controls;

namespace InkvoltPdf.App;

/// <summary>
/// A command bar that always stays one row: children are laid out left to right, and those that don't fit are hidden
/// and listed in <see cref="OverflowItems"/> for the overflow ("More") button, the child marked
/// <see cref="IsOverflowButtonProperty"/>, which shows only when something overflowed. Separators (any non-button child)
/// never end the visible part.
/// </summary>
public sealed class ToolbarPanel : Panel
{
    public static readonly DependencyProperty IsOverflowButtonProperty = DependencyProperty.RegisterAttached(
        "IsOverflowButton", typeof(bool), typeof(ToolbarPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetIsOverflowButton(DependencyObject d) => (bool)d.GetValue(IsOverflowButtonProperty);
    public static void SetIsOverflowButton(DependencyObject d, bool value) => d.SetValue(IsOverflowButtonProperty, value);

    /// <summary>A button's text label (next to its icon): dropped first when room runs short, before anything overflows.</summary>
    public static readonly DependencyProperty IsLabelProperty = DependencyProperty.RegisterAttached(
        "IsLabel", typeof(bool), typeof(ToolbarPanel), new PropertyMetadata(false));

    public static bool GetIsLabel(DependencyObject d) => (bool)d.GetValue(IsLabelProperty);
    public static void SetIsLabel(DependencyObject d, bool value) => d.SetValue(IsLabelProperty, value);

    private readonly HashSet<UIElement> _shown = [];
    private readonly List<UIElement> _overflow = [];
    // Each item's width with its label, remembered while labels show, so the compact decision is stable.
    private readonly Dictionary<UIElement, double> _fullWidth = [];
    private bool _compact;

    /// <summary>The children hidden for lack of room, in bar order.</summary>
    public IReadOnlyList<UIElement> OverflowItems => _overflow;

    protected override Size MeasureOverride(Size availableSize)
    {
        UIElement? more = null;
        var items = new List<UIElement>();
        var height = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
            if (GetIsOverflowButton(child)) more = child;
            else if (child.Visibility != Visibility.Collapsed) items.Add(child);
        }

        // Too narrow for the labels: icons only (the tooltips keep the names), then overflow if still too narrow.
        if (!_compact)
            foreach (var item in items) _fullWidth[item] = item.DesiredSize.Width;
        var compact = items.Sum(c => _fullWidth.GetValueOrDefault(c, c.DesiredSize.Width)) > availableSize.Width;
        if (compact != _compact)
        {
            _compact = compact;
            foreach (var item in items) SetLabelsVisible(item, !compact);
            foreach (var item in items) item.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        }

        _shown.Clear();
        _overflow.Clear();
        var total = items.Sum(c => c.DesiredSize.Width);
        var fit = items.Count;
        if (total > availableSize.Width)
        {
            var room = availableSize.Width - (more?.DesiredSize.Width ?? 0);
            var used = 0.0;
            fit = 0;
            while (fit < items.Count && used + items[fit].DesiredSize.Width <= room) used += items[fit++].DesiredSize.Width;
            while (fit > 0 && items[fit - 1] is not System.Windows.Controls.Primitives.ButtonBase) fit--;
        }
        for (var i = 0; i < items.Count; i++)
            if (i < fit) _shown.Add(items[i]);
            else _overflow.Add(items[i]);

        var width = _shown.Sum(c => c.DesiredSize.Width) + (_overflow.Count > 0 ? more?.DesiredSize.Width ?? 0 : 0);
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    private static void SetLabelsVisible(DependencyObject root, bool visible)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is UIElement label && GetIsLabel(label)) label.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            else SetLabelsVisible(child, visible);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var show = GetIsOverflowButton(child) ? _overflow.Count > 0 : _shown.Contains(child);
            // Hidden, not just squeezed: out of Tab order, hit testing and screen readers' view (the More menu has them).
            var visibility = show ? Visibility.Visible : Visibility.Hidden;
            if (child.Visibility != visibility) child.Visibility = visibility;
            if (!show)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            var size = child.DesiredSize;
            child.Arrange(new Rect(x, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
            x += size.Width;
        }
        return finalSize;
    }
}
