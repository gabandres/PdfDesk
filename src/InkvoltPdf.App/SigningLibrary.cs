using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using InkvoltPdf.Core;

namespace InkvoltPdf.App;

/// <summary>A saved handwritten signature or initials (a transparent PNG in the user data folder).</summary>
public sealed record SavedSignature(string Path, bool Initials)
{
    public string Name => Initials ? "Initials" : $"Signature {System.IO.Path.GetFileNameWithoutExtension(Path).Replace("signature", "", StringComparison.Ordinal)}";
    public BitmapSource Load() => SignatureImages.LoadPng(Path);
}

/// <summary>Up to three saved signatures and one set of initials, under the data folder's "signatures".</summary>
public static class SignatureLibrary
{
    public const int MaxSignatures = 3;
    public static string Folder => Path.Combine(AppPaths.DataDirectory, "signatures");

    public static List<SavedSignature> List()
    {
        if (!Directory.Exists(Folder)) return [];
        return [.. Directory.EnumerateFiles(Folder, "*.png")
            .Select(p => new SavedSignature(p, Path.GetFileName(p).StartsWith("initials", StringComparison.OrdinalIgnoreCase)))
            .Where(s => s.Initials || Path.GetFileName(s.Path).StartsWith("signature", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Initials).ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Saves a new signature (in the first free slot) or replaces the initials. Null when all slots are taken.</summary>
    public static SavedSignature? Save(BitmapSource image, bool initials)
    {
        Directory.CreateDirectory(Folder);
        string? path = initials ? Path.Combine(Folder, "initials.png") : null;
        for (var i = 1; path is null && i <= MaxSignatures; i++)
        {
            var p = Path.Combine(Folder, $"signature{i}.png");
            if (!File.Exists(p)) path = p;
        }
        if (path is null) return null;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        var temp = path + ".tmp";
        using (var fs = File.Create(temp)) enc.Save(fs);
        File.Move(temp, path, overwrite: true);
        return new SavedSignature(path, initials);
    }

    public static void Delete(SavedSignature s)
    {
        if (File.Exists(s.Path)) File.Delete(s.Path);
    }
}

/// <summary>Bitmap helpers: cropping to the ink, white-to-transparent, and conversion to <see cref="StampImage"/>.</summary>
public static class SignatureImages
{
    public static BitmapSource LoadPng(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static (int W, int H, byte[] Px) Bgra(BitmapSource src)
    {
        var conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var w = conv.PixelWidth;
        var h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        return (w, h, px);
    }

    private static BitmapSource FromBgra(int w, int h, byte[] px)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Crops to the non-transparent pixels (plus a small margin); scales down to at most <paramref name="maxSide"/> px.</summary>
    public static BitmapSource? Trim(BitmapSource src, int maxSide = 1200)
    {
        var (w, h, px) = Bgra(src);
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] > 8)
                {
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y);
                }
        if (x1 < 0) return null;
        var m = 4;
        x0 = Math.Max(0, x0 - m);
        y0 = Math.Max(0, y0 - m);
        x1 = Math.Min(w - 1, x1 + m);
        y1 = Math.Min(h - 1, y1 + m);
        BitmapSource cropped = new CroppedBitmap(FromBgra(w, h, px), new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(cropped.PixelWidth, cropped.PixelHeight));
        if (scale < 1) cropped = new TransformedBitmap(cropped, new ScaleTransform(scale, scale));
        var (cw, ch, cpx) = Bgra(cropped);
        return FromBgra(cw, ch, cpx);
    }

    /// <summary>Near-white pixels become transparent (scanned or photographed signatures on paper).</summary>
    public static BitmapSource WhiteToTransparent(BitmapSource src)
    {
        var (w, h, px) = Bgra(src);
        for (var i = 0; i < px.Length; i += 4)
        {
            var light = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
            if (light >= 200) px[i + 3] = (byte)(px[i + 3] * Math.Clamp((255 - light) / 55.0, 0, 1));
        }
        return FromBgra(w, h, px);
    }

    /// <summary>An image file to put on a page (a watermark, a picture, a stamp), scaled down to at most <paramref name="maxSide"/> px.</summary>
    public static BitmapSource LoadImage(string path, int maxSide = 2000)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return Fit(bmp, maxSide);
    }

    /// <summary>
    /// A clipboard image as it looks: 32-bit bitmaps on the clipboard often come with an alpha channel that is all zero
    /// (the copying app wrote none), which would make the whole picture invisible; those are made opaque.
    /// </summary>
    public static BitmapSource FromClipboard(BitmapSource src)
    {
        if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32) return src;
        // The pixels as stored (B, G, R, A either way): converting first would multiply the color by the zero alpha.
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        for (var i = 3; i < px.Length; i += 4)
            if (px[i] != 0) return src;
        for (var i = 3; i < px.Length; i += 4) px[i] = 255;
        return FromBgra(w, h, px);
    }

    /// <summary>Scales <paramref name="src"/> down to at most <paramref name="maxSide"/> px on its longer side (never up).</summary>
    public static BitmapSource Fit(BitmapSource src, int maxSide)
    {
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(1, Math.Max(src.PixelWidth, src.PixelHeight)));
        if (scale >= 1) return src;
        var scaled = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    public static StampImage ToStampImage(BitmapSource src)
    {
        var (w, h, px) = Bgra(src);
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        for (var i = 0; i < w * h; i++)
        {
            rgb[i * 3] = px[i * 4 + 2];
            rgb[i * 3 + 1] = px[i * 4 + 1];
            rgb[i * 3 + 2] = px[i * 4];
            alpha[i] = px[i * 4 + 3];
        }
        return new StampImage(w, h, rgb, alpha);
    }

    /// <summary>Renders a visual area to a transparent bitmap at 3x.</summary>
    public static BitmapSource Render(Visual visual, Size size)
    {
        const double k = 3;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * k), (int)Math.Ceiling(size.Height * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Installed handwriting-style fonts for typed signatures.</summary>
    public static List<FontFamily> ScriptFonts()
    {
        string[] wanted = ["Segoe Script", "Ink Free", "Lucida Handwriting", "Brush Script MT", "Freestyle Script", "Mistral", "Segoe Print", "Kunstler Script", "Vladimir Script"];
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = wanted.Where(installed.Contains).Select(n => new FontFamily(n)).ToList();
        if (list.Count == 0) list.Add(new FontFamily("Segoe UI"));
        return list;
    }
}

/// <summary>Creates a handwritten signature: draw it (mouse, pen, touch), type it in a script font, or import an image.</summary>
public sealed class SignatureCaptureDialog : Window
{
    private readonly InkCanvas _ink = new() { Background = Brushes.White, Height = 170, Cursor = System.Windows.Input.Cursors.Pen };
    private readonly TextBox _typed = new();
    private readonly ComboBox _font = new();
    private readonly TextBlock _typedPreview = new() { FontSize = 56, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
    private readonly Image _imported = new() { Height = 150, Stretch = Stretch.Uniform };
    private readonly CheckBox _whiteToClear = new() { Content = "Make a white background transparent", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TabControl _tabs = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private BitmapSource? _importedSource;

    public BitmapSource? Result { get; private set; }

    public SignatureCaptureDialog(Window owner, bool initials, string defaultName)
    {
        Owner = owner;
        Title = initials ? "Add initials" : "Add a signature";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var pen = new ComboBox { ItemsSource = new[] { "Black ink", "Blue ink" }, SelectedIndex = 0, Width = 120 };
        void SetInk() => _ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = pen.SelectedIndex == 1 ? Color.FromRgb(0x10, 0x2A, 0x8C) : Colors.Black,
            Width = 3, Height = 3, FitToCurve = true, IgnorePressure = false,
        };
        SetInk();
        pen.SelectionChanged += (_, _) =>
        {
            SetInk();
            foreach (var s in _ink.Strokes) s.DrawingAttributes.Color = _ink.DefaultDrawingAttributes.Color;
        };
        var clear = new Button { Content = "Clear", Margin = new Thickness(8, 0, 0, 0) };
        clear.Click += (_, _) => _ink.Strokes.Clear();
        var drawTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        drawTools.Children.Add(pen);
        drawTools.Children.Add(clear);
        var draw = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        draw.Children.Add(new Border { Child = _ink, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) });
        draw.Children.Add(drawTools);
        draw.Children.Add(Caption("Sign with the mouse, a pen or your finger."));

        _typed.Text = initials ? new string([.. defaultName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]))]) : defaultName;
        _font.ItemsSource = SignatureImages.ScriptFonts();
        _font.DisplayMemberPath = "Source";
        _font.SelectedIndex = 0;
        void UpdateTyped()
        {
            _typedPreview.Text = _typed.Text;
            if (_font.SelectedItem is FontFamily f) _typedPreview.FontFamily = f;
        }
        _typed.TextChanged += (_, _) => UpdateTyped();
        _font.SelectionChanged += (_, _) => UpdateTyped();
        UpdateTyped();
        var type = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        type.Children.Add(Label(initials ? "Initials" : "Name"));
        type.Children.Add(_typed);
        type.Children.Add(Label("Style"));
        type.Children.Add(_font);
        type.Children.Add(new Border { Child = _typedPreview, Background = Brushes.White, Height = 110, Margin = new Thickness(0, 10, 0, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) });

        var pick = new Button { Content = "Choose an image…", HorizontalAlignment = HorizontalAlignment.Left };
        pick.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                _importedSource = SignatureImages.LoadPng(dlg.FileName);
                _imported.Source = _importedSource;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
            {
                ShowError($"That image can't be read: {ex.Message}");
            }
        };
        var image = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        image.Children.Add(pick);
        image.Children.Add(new Border { Child = _imported, Background = Brushes.White, Height = 150, Margin = new Thickness(0, 10, 0, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) });
        image.Children.Add(_whiteToClear);
        image.Children.Add(Caption("PNG images keep their transparency. A scan or photo works best on plain white paper."));

        _tabs.Items.Add(new TabItem { Header = "Draw", Content = draw });
        _tabs.Items.Add(new TabItem { Header = "Type", Content = type });
        _tabs.Items.Add(new TabItem { Header = "Image", Content = image });

        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 104, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "AccentButtonStyle");
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 104 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        body.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(_tabs);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        body.Children.Add(_error);
        body.Children.Add(buttons);
        Content = body;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) };
    private static TextBlock Caption(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 6, 0, 0) };

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.Visibility = Visibility.Visible;
    }

    /// <summary>Dev/test entry: draws a stroke and accepts.</summary>
    internal void DevDrawAndAccept()
    {
        var pts = new System.Windows.Input.StylusPointCollection();
        for (var x = 10; x < 300; x += 6) pts.Add(new System.Windows.Input.StylusPoint(x, 80 + Math.Sin(x / 15.0) * 40));
        _ink.Strokes.Add(new Stroke(pts, _ink.DefaultDrawingAttributes.Clone()));
        Accept();
    }

    private void Accept()
    {
        BitmapSource? raw = null;
        switch (_tabs.SelectedIndex)
        {
            case 0:
            {
                if (_ink.Strokes.Count == 0)
                {
                    ShowError("Draw your signature first.");
                    return;
                }
                var bounds = _ink.Strokes.GetBounds();
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.PushTransform(new TranslateTransform(-bounds.X + 4, -bounds.Y + 4));
                    _ink.Strokes.Draw(dc);
                }
                raw = SignatureImages.Render(dv, new Size(bounds.Width + 8, bounds.Height + 8));
                break;
            }
            case 1:
            {
                if (string.IsNullOrWhiteSpace(_typed.Text))
                {
                    ShowError("Type your name first.");
                    return;
                }
                var ft = new FormattedText(_typed.Text.Trim(), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface((FontFamily)_font.SelectedItem, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 72, Brushes.Black, 1);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawText(ft, new Point(8, 8));
                raw = SignatureImages.Render(dv, new Size(ft.WidthIncludingTrailingWhitespace + 32, ft.Height + 16));
                break;
            }
            case 2:
                if (_importedSource is null)
                {
                    ShowError("Choose an image first.");
                    return;
                }
                raw = _whiteToClear.IsChecked == true ? SignatureImages.WhiteToTransparent(_importedSource) : _importedSource;
                break;
        }
        Result = raw is null ? null : SignatureImages.Trim(raw);
        if (Result is null)
        {
            ShowError("The signature is empty.");
            return;
        }
        DialogResult = true;
    }
}
