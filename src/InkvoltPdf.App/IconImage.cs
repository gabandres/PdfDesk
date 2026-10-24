using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InkvoltPdf.App;

/// <summary>
/// An .ico shown in an <see cref="Image"/> at the frame that fits. <c>Image Source="x.ico"</c> shows the icon's first
/// frame (16 px in the app's icon), which blurs when scaled up at 125-200% display scaling; <c>app:IconImage.Source</c>
/// picks the smallest frame at least as many pixels wide as the Image is on screen (its size in DIPs times the
/// monitor's scale), and picks again when the window moves to a monitor with another scale.
/// </summary>
public static class IconImage
{
    /// <summary>The icon: a pack path inside the app ("InkvoltPdf.ico").</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(IconImage), new PropertyMetadata(null, OnSourceChanged));

    public static string? GetSource(Image image) => (string?)image.GetValue(SourceProperty);

    public static void SetSource(Image image, string? value) => image.SetValue(SourceProperty, value);

    private static readonly Dictionary<string, BitmapFrame[]> Frames = [];

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        image.Loaded -= OnLoaded;
        image.DpiChanged -= OnDpiChanged;
        image.Loaded += OnLoaded;
        image.DpiChanged += OnDpiChanged;
        Pick(image);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Pick((Image)sender);

    private static void OnDpiChanged(object sender, DpiChangedEventArgs e) => Pick((Image)sender);

    private static void Pick(Image image)
    {
        if (GetSource(image) is not { Length: > 0 } path) return;
        if (!Frames.TryGetValue(path, out var frames))
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/" + path.TrimStart('/')), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            // Largest color depth first among frames of one size.
            frames = [.. decoder.Frames.OrderBy(f => f.PixelWidth).ThenByDescending(f => f.Format.BitsPerPixel)];
            Frames[path] = frames;
        }
        if (frames.Length == 0) return;
        var dips = double.IsNaN(image.Width) ? image.ActualWidth : image.Width;
        var pixels = Math.Ceiling(dips * VisualTreeHelper.GetDpi(image).DpiScaleX);
        var frame = frames.FirstOrDefault(f => f.PixelWidth >= pixels) ?? frames[^1];
        if (!ReferenceEquals(image.Source, frame)) image.Source = frame;
    }
}
