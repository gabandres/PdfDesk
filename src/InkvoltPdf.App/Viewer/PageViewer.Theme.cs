using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace InkvoltPdf.App.Viewer;

/// <summary>Colors of the page view's overlays; Windows high-contrast mode switches them to the system colors.</summary>
public sealed partial class PageViewer
{
    private sealed record Palette(
        Brush Background, Brush Shadow, Brush Hit, Brush ActiveHit, Pen ActiveHitPen, Brush Selection, Brush Caret,
        Pen SnapPen, Brush SnapFill, Brush Field, Pen? FieldPen, Pen HoverPen, Pen FocusPen, Pen StampPen)
    {
        public static Palette Normal { get; } = new(
            Background: Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x84, 0x8C))),
            Shadow: Frozen(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0))),
            Hit: Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xD0, 0x00))),
            ActiveHit: Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x80, 0x00))),
            ActiveHitPen: Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x00)), 1.5)),
            Selection: Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x30, 0x80, 0xFF))),
            Caret: Frozen(Brushes.Black),
            SnapPen: Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x20, 0x70, 0xE0)), 1) { DashStyle = DashStyles.Dash }),
            SnapFill: Frozen(new SolidColorBrush(Color.FromArgb(0x20, 0x20, 0x70, 0xE0))),
            Field: Frozen(new SolidColorBrush(Color.FromArgb(0x2C, 0x33, 0x88, 0xFF))),
            FieldPen: null,
            HoverPen: Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)), 1.5)),
            FocusPen: Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0x4E, 0x9A)), 2.5)),
            StampPen: Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0x00, 0x67, 0xC0)), 1) { DashStyle = DashStyles.Dash }));

        /// <summary>From the current system colors (read again whenever they change).</summary>
        public static Palette HighContrast()
        {
            var highlight = SystemColors.HighlightColor;
            var text = SystemColors.WindowTextColor;
            Brush Solid(Color c) => Frozen(new SolidColorBrush(c));
            Brush Tint(Color c, byte alpha) => Frozen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)));
            return new Palette(
                Background: Solid(SystemColors.AppWorkspaceColor),
                Shadow: Solid(text),
                Hit: Tint(highlight, 0x60),
                ActiveHit: Tint(highlight, 0xA0),
                ActiveHitPen: Frozen(new Pen(Solid(SystemColors.HighlightTextColor), 2)),
                Selection: Tint(highlight, 0x80),
                Caret: Solid(highlight),
                SnapPen: Frozen(new Pen(Solid(highlight), 1.5) { DashStyle = DashStyles.Dash }),
                SnapFill: Tint(highlight, 0x30),
                Field: Tint(SystemColors.HotTrackColor, 0x30),
                FieldPen: Frozen(new Pen(Solid(SystemColors.HotTrackColor), 1)),
                HoverPen: Frozen(new Pen(Solid(SystemColors.HotTrackColor), 2)),
                FocusPen: Frozen(new Pen(Solid(highlight), 3)),
                StampPen: Frozen(new Pen(Solid(SystemColors.HotTrackColor), 1.5) { DashStyle = DashStyles.Dash }));
        }
    }

    private Palette _palette = Palette.Normal;
    private bool _highContrast;

    /// <summary>Dev/test: forces the high-contrast palette on or off (null = follow Windows).</summary>
    internal static bool? DevForceHighContrast { get; set; }

    internal void DevApplyContrast() => ApplyContrast();

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or null or "")
            Dispatcher.BeginInvoke(ApplyContrast);
    }

    /// <summary>Picks the palette for the current contrast mode; dark pages are off in high contrast.</summary>
    private void ApplyContrast()
    {
        var was = EffectiveInvert;
        _highContrast = DevForceHighContrast ?? SystemParameters.HighContrast;
        _palette = _highContrast ? Palette.HighContrast() : Palette.Normal;
        Background = _palette.Background;
        if (was != EffectiveInvert) DropPageBitmaps();
        _surface.InvalidateVisual();
    }
}
