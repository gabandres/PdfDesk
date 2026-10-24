using System.Windows.Input;
using InkvoltPdf.Core;

namespace InkvoltPdf.App.Viewer;

/// <summary>The drawing tools: their annotation kind, default look, name, icon and key.</summary>
public static class DrawTools
{
    public static readonly ViewerTool[] All =
        [ViewerTool.Pen, ViewerTool.Highlighter, ViewerTool.Rectangle, ViewerTool.Ellipse, ViewerTool.Line, ViewerTool.Arrow, ViewerTool.TextBox, ViewerTool.Callout];

    public static bool IsDrawTool(ViewerTool tool) => Array.IndexOf(All, tool) >= 0;

    public static bool IsInk(ViewerTool tool) => tool is ViewerTool.Pen or ViewerTool.Highlighter;

    public static bool IsText(ViewerTool tool) => tool is ViewerTool.TextBox or ViewerTool.Callout;

    /// <summary>The PDF subtype the tool creates.</summary>
    public static string Subtype(ViewerTool tool) => tool switch
    {
        ViewerTool.Pen or ViewerTool.Highlighter => "Ink",
        ViewerTool.Rectangle => "Square",
        ViewerTool.Ellipse => "Circle",
        ViewerTool.Line or ViewerTool.Arrow => "Line",
        _ => "FreeText",
    };

    public static AnnotStyle DefaultStyle(ViewerTool tool) => tool switch
    {
        ViewerTool.Pen => AnnotStyle.Pen(new AnnotColor(0.1, 0.4, 0.9), 2),
        ViewerTool.Highlighter => AnnotStyle.Marker(),
        ViewerTool.Rectangle or ViewerTool.Ellipse => new AnnotStyle { Color = AnnotColor.Red, Width = 2 },
        ViewerTool.Line => AnnotStyle.Pen(AnnotColor.Red, 2),
        ViewerTool.Arrow => AnnotStyle.Arrow(AnnotColor.Red, 2),
        ViewerTool.Callout => AnnotStyle.TextBox(12, AnnotColor.Red, new AnnotColor(1, 1, 1)),
        _ => AnnotStyle.Typewriter(12),
    };

    /// <summary>What the tool makes, as a noun ("Rectangle"); also the comment's title.</summary>
    public static string Title(ViewerTool tool) => tool switch
    {
        ViewerTool.Pen => "Drawing",
        ViewerTool.Highlighter => "Highlighter",
        ViewerTool.Rectangle => "Rectangle",
        ViewerTool.Ellipse => "Ellipse",
        ViewerTool.Line => "Line",
        ViewerTool.Arrow => "Arrow",
        ViewerTool.TextBox => "Text box",
        ViewerTool.Callout => "Callout",
        _ => tool.ToString(),
    };

    /// <summary>The tool's name in menus and tooltips.</summary>
    public static string ToolName(ViewerTool tool) => tool switch
    {
        ViewerTool.Pen => "Pen",
        ViewerTool.Highlighter => "Highlighter pen",
        _ => Title(tool),
    };

    /// <summary>The Segoe Fluent Icons glyph of the tool (and of what it makes).</summary>
    public static string Glyph(ViewerTool tool) => tool switch
    {
        ViewerTool.Pen => "",
        ViewerTool.Highlighter => "",
        ViewerTool.Rectangle => "",
        ViewerTool.Ellipse => "",
        ViewerTool.Line => "",
        ViewerTool.Arrow => "",
        ViewerTool.TextBox => "",
        ViewerTool.Callout => "",
        _ => "",
    };

    /// <summary>The page view's key for the tool (a bare letter, ignored while typing).</summary>
    public static Key KeyOf(ViewerTool tool) => tool switch
    {
        ViewerTool.Pen => Key.P,
        ViewerTool.Highlighter => Key.M,
        ViewerTool.Rectangle => Key.R,
        ViewerTool.Ellipse => Key.E,
        ViewerTool.Line => Key.L,
        ViewerTool.Arrow => Key.W,
        ViewerTool.TextBox => Key.B,
        ViewerTool.Callout => Key.C,
        _ => Key.None,
    };

    public static ViewerTool? ToolForKey(Key key) => All.Cast<ViewerTool?>().FirstOrDefault(t => KeyOf(t!.Value) == key);

    /// <summary>The tool that makes annotations like <paramref name="a"/> (for its title, icon and the style bar's options).</summary>
    public static ViewerTool? ToolOf(AnnotationInfo a) => a.Subtype switch
    {
        "Ink" => a.Style?.Highlighter == true ? ViewerTool.Highlighter : ViewerTool.Pen,
        "Square" or "Polygon" => ViewerTool.Rectangle,
        "Circle" => ViewerTool.Ellipse,
        "Line" or "PolyLine" => a.Style is { } s && (s.StartEnding != LineEnding.None || s.EndEnding != LineEnding.None) ? ViewerTool.Arrow : ViewerTool.Line,
        "FreeText" when !a.IsInkvoltPdfStamp => a.Geometry?.Callout.Count > 0 ? ViewerTool.Callout : ViewerTool.TextBox,
        _ => null,
    };

    /// <summary>A drawing annotation Inkvolt PDF can select, move, resize and restyle.</summary>
    public static bool IsDrawing(AnnotationInfo a) => a.Geometry is not null && a.Style is not null && !a.IsInkvoltPdfStamp;
}
