using System.Runtime.InteropServices.WindowsRuntime;
using InkvoltPdf.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrWord = InkvoltPdf.Core.OcrWord;

namespace InkvoltPdf.App;

/// <summary>
/// Text recognition with Windows' built-in OCR (Windows.Media.Ocr, on the device, nothing leaves the PC). Pages are
/// rendered at 300 dpi and recognized in tiles no larger than the engine's limit. The language is the first of the
/// user's Windows display languages that has an OCR pack, else English, else any installed OCR language.
/// </summary>
internal static class PageOcr
{
    private const double Dpi = 300;
    private const long MaxRenderPixels = 60_000_000; // ~180 MB of BGR for very large pages; smaller pages render at 300 dpi
    private const int Overlap = 160; // px shared by neighbouring tiles, so a word cut by one tile is whole in the other

    private static readonly Lock Gate = new();
    private static OcrEngine? _engine;
    private static bool _tried;

    /// <summary>The OCR engine, or null when no OCR language is installed on this PC.</summary>
    public static OcrEngine? Engine
    {
        get
        {
            lock (Gate)
            {
                if (_tried) return _engine;
                _tried = true;
                try
                {
                    _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                    if (_engine is null)
                    {
                        var english = new Windows.Globalization.Language("en-US");
                        if (OcrEngine.IsLanguageSupported(english)) _engine = OcrEngine.TryCreateFromLanguage(english);
                    }
                    if (_engine is null && OcrEngine.AvailableRecognizerLanguages.FirstOrDefault() is { } any)
                        _engine = OcrEngine.TryCreateFromLanguage(any);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Write($"OCR isn't available: {ex.Message}");
                    _engine = null;
                }
                return _engine;
            }
        }
    }

    public const string Unavailable =
        "Windows has no text recognition (OCR) language installed. Add one in Settings > Time & language > Language & region (a language's options list \"Optical character recognition\").";

    /// <summary>The words on a page (page space: points, top-left of the displayed page), in reading order per tile.</summary>
    public static List<OcrWord> Recognize(OcrEngine engine, PdfEngine pdf, int page, CancellationToken cancel)
    {
        var g = pdf.GetPage(page);
        var scale = Dpi / 72;
        if (g.Width * g.Height * scale * scale > MaxRenderPixels) scale = Math.Sqrt(MaxRenderPixels / (g.Width * g.Height));
        var image = pdf.Render(page, scale, null, cancel);

        var max = (int)Math.Max(Overlap * 2 + 64, OcrEngine.MaxImageDimension);
        var words = new List<OcrWord>();
        foreach (var (x0, x1, coreX0, coreX1) in Tiles(image.Width, max))
        foreach (var (y0, y1, coreY0, coreY1) in Tiles(image.Height, max))
        {
            cancel.ThrowIfCancellationRequested();
            using var bitmap = TileBitmap(image, x0, y0, x1 - x0, y1 - y0);
            var result = engine.RecognizeAsync(bitmap).AsTask(cancel).GetAwaiter().GetResult();
            foreach (var line in result.Lines)
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                double cx = x0 + r.X + r.Width / 2, cy = y0 + r.Y + r.Height / 2;
                if (cx < coreX0 || cx >= coreX1 || cy < coreY0 || cy >= coreY1) continue; // the neighbouring tile has it
                words.Add(new OcrWord(word.Text, new RectD((x0 + r.X) / scale, (y0 + r.Y) / scale, r.Width / scale, r.Height / scale)));
            }
        }
        return words;
    }

    /// <summary>Overlapping spans of at most <paramref name="max"/> px covering 0..<paramref name="size"/>, each with the part it owns.</summary>
    private static IEnumerable<(int Start, int End, int CoreStart, int CoreEnd)> Tiles(int size, int max)
    {
        if (size <= max)
        {
            yield return (0, size, 0, size);
            yield break;
        }
        var step = max - Overlap;
        for (var start = 0; ; start += step)
        {
            var end = Math.Min(size, start + max);
            var last = end == size;
            yield return (start, end, start == 0 ? 0 : start + Overlap / 2, last ? size : end - Overlap / 2);
            if (last) yield break;
        }
    }

    private static SoftwareBitmap TileBitmap(RenderedImage image, int x, int y, int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
        {
            var src = (y + row) * image.Stride + x * 3;
            var dst = row * w * 4;
            for (var col = 0; col < w; col++, src += 3, dst += 4)
            {
                bgra[dst] = image.Pixels[src];
                bgra[dst + 1] = image.Pixels[src + 1];
                bgra[dst + 2] = image.Pixels[src + 2];
                bgra[dst + 3] = 255;
            }
        }
        return SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
    }
}
