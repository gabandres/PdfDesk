using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InkvoltPdf.App.Viewer;
using InkvoltPdf.Core;

namespace InkvoltPdf.App;

/// <summary>A custom stamp made from an image (a PNG in the user data folder; its file name is its name).</summary>
public sealed record SavedStamp(string Path)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public BitmapSource Load() => SignatureImages.LoadPng(Path);
}

/// <summary>Custom stamps saved on this PC, under the data folder's "stamps" (like the handwritten signatures).</summary>
public static class StampLibrary
{
    public const int MaxStamps = 20;
    public static string Folder => Path.Combine(AppPaths.DataDirectory, "stamps");

    public static List<SavedStamp> List() =>
        Directory.Exists(Folder) ? [.. Directory.EnumerateFiles(Folder, "*.png").Order(StringComparer.CurrentCultureIgnoreCase).Select(p => new SavedStamp(p))] : [];

    /// <summary>Saves a stamp under <paramref name="name"/> (made safe for a file name; a number added if taken). Null when the library is full.</summary>
    public static SavedStamp? Save(BitmapSource image, string name)
    {
        if (List().Count >= MaxStamps) return null;
        Directory.CreateDirectory(Folder);
        var clean = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim('.', ' ');
        if (clean.Length == 0) clean = "Stamp";
        if (clean.Length > 60) clean = clean[..60];
        var path = Path.Combine(Folder, clean + ".png");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(Folder, $"{clean} ({i}).png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        var temp = path + ".tmp";
        using (var fs = File.Create(temp)) enc.Save(fs);
        File.Move(temp, path, overwrite: true);
        return new SavedStamp(path);
    }

    public static void Delete(SavedStamp s)
    {
        if (File.Exists(s.Path)) File.Delete(s.Path);
    }
}

/// <summary>The Stamp menu (built-in stamps, "Approved by", custom stamps, adding and deleting custom ones).</summary>
public partial class MainWindow
{
    private void ShowStampMenu(DocumentTab t)
    {
        var menu = new ContextMenu();
        foreach (var design in RubberStamps.BuiltIn.Append(DocumentTab.ApprovedByNow()))
        {
            var d = design;
            var item = new MenuItem { Header = StampPreview(d) };
            System.Windows.Automation.AutomationProperties.SetName(item, d.Detail is null ? $"Stamp {d.Label}" : $"Stamp {d.Title} {d.Detail}");
            item.Click += (_, _) => t.BeginPlaceStamp(d);
            menu.Items.Add(item);
        }
        var saved = StampLibrary.List();
        if (saved.Count > 0) menu.Items.Add(new Separator());
        foreach (var s in saved)
        {
            var stamp = s;
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            try
            {
                header.Children.Add(new Border
                {
                    Background = Brushes.White,
                    Padding = new Thickness(4, 2, 4, 2),
                    Child = new Image { Source = s.Load(), Height = 28, MaxWidth = 120, Stretch = Stretch.Uniform },
                });
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                Log.Write($"Loading a custom stamp failed: {ex.Message}");
                continue;
            }
            header.Children.Add(new TextBlock { Text = s.Name, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = header };
            System.Windows.Automation.AutomationProperties.SetName(item, $"Stamp {s.Name}");
            item.Click += (_, _) => t.BeginPlaceStamp(stamp);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "Add a _custom stamp from an image…", IsEnabled = saved.Count < StampLibrary.MaxStamps };
        if (!add.IsEnabled) add.ToolTip = $"Up to {StampLibrary.MaxStamps} custom stamps: delete one first.";
        add.Click += (_, _) => AddCustomStamp(t, null);
        menu.Items.Add(add);
        if (saved.Count > 0)
        {
            var delete = new MenuItem { Header = "_Delete a custom stamp" };
            foreach (var s in saved)
            {
                var stamp = s;
                var d = new MenuItem { Header = s.Name };
                d.Click += (_, _) =>
                {
                    if (!Prompt.Confirm(this, $"Delete the {stamp.Name} stamp?", "It is removed from this PC. Stamps already placed in documents stay.", "Delete")) return;
                    try
                    {
                        StampLibrary.Delete(stamp);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Prompt.Error(this, "Couldn't delete it", ex.Message);
                    }
                };
                delete.Items.Add(d);
            }
            menu.Items.Add(delete);
        }

        menu.PlacementTarget = StampButton.IsVisible ? StampButton : this;
        menu.Placement = StampButton.IsVisible ? PlacementMode.Bottom : PlacementMode.Center;
        menu.Closed += (_, _) => UpdateUi();
        menu.IsOpen = true;
        StampButton.IsChecked = true;
    }

    /// <summary>A stamp as the menu shows it: its title (and detail line) in its color, in a rounded frame.</summary>
    private static Border StampPreview(StampDesign d)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(d.Color.R * 255), (byte)(d.Color.G * 255), (byte)(d.Color.B * 255)));
        brush.Freeze();
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = d.Title, FontWeight = FontWeights.Bold, Foreground = brush, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center });
        if (d.Detail is { } detail) text.Children.Add(new TextBlock { Text = detail, Foreground = brush, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
        return new Border
        {
            BorderBrush = brush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.White,
            Padding = new Thickness(10, 2, 10, 3),
            MinWidth = 150,
            Child = text,
        };
    }

    /// <summary>Makes a custom stamp from an image file (asks for it, and for its name), saves it, and arms it.</summary>
    internal void AddCustomStamp(DocumentTab t, string? path, string? name = null)
    {
        if (path is null)
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Title = "Custom stamp from an image", Filter = FileTools.ImageFilter, InitialDirectory = AppSettings.Current.LastFolder };
            if (pick.ShowDialog(this) != true) return;
            path = pick.FileName;
        }
        BitmapSource bmp;
        try
        {
            bmp = SignatureImages.LoadImage(path, 1200);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Prompt.Error(this, "That image can't be used", ex.Message);
            return;
        }
        var clear = true;
        if (name is null)
        {
            var dlg = new FormDialog(this, "Add a Custom Stamp", "Save");
            dlg.Add(null, new Border { Background = Brushes.White, Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left, Child = new Image { Source = bmp, MaxHeight = 90, MaxWidth = 380, Stretch = Stretch.Uniform } });
            var box = dlg.AddText("Name", Path.GetFileNameWithoutExtension(path));
            var transparent = dlg.AddCheck("Make a white background transparent", true);
            dlg.AddNote("Saved on this PC for use in any document, like your signatures.");
            dlg.Validate(() => box.Text.Trim().Length == 0 ? "Give the stamp a name." : null);
            if (!dlg.ShowModal()) return;
            name = box.Text.Trim();
            clear = transparent.IsChecked == true;
        }
        try
        {
            var image = clear ? SignatureImages.WhiteToTransparent(bmp) : bmp;
            image = SignatureImages.Trim(image) ?? image;
            if (StampLibrary.Save(image, name) is not { } saved)
            {
                Prompt.Info(this, "No room for another stamp", $"Up to {StampLibrary.MaxStamps} custom stamps can be saved: delete one first.");
                return;
            }
            t.BeginPlaceStamp(saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Prompt.Error(this, "Couldn't save the stamp", ex.Message);
        }
    }
}

/// <summary>Stamps on the page: the Stamp tool places the chosen stamp where the page is clicked.</summary>
public partial class DocumentTab
{
    private StampDesign? _pendingStamp;
    private SavedStamp? _pendingCustomStamp;

    /// <summary>The stamp placed last (this session): the S key arms it again.</summary>
    private static object? _lastStamp;

    /// <summary>"Approved by" with the author's name and the time now.</summary>
    public static StampDesign ApprovedByNow() => RubberStamps.ApprovedBy(Author, DateTime.Now);

    private void InitStamps()
    {
        Viewer.ToolChanged += (_, _) =>
        {
            if (Viewer.Tool != ViewerTool.AddStamp) (_pendingStamp, _pendingCustomStamp) = (null, null);
            // The Stamp tool chosen with its key: the stamp used last, else Approved.
            else if (_pendingStamp is null && _pendingCustomStamp is null)
            {
                switch (_lastStamp)
                {
                    case SavedStamp s when File.Exists(s.Path):
                        BeginPlaceStamp(s);
                        break;
                    case StampDesign { Detail: not null }:
                        BeginPlaceStamp(ApprovedByNow());
                        break;
                    case StampDesign d:
                        BeginPlaceStamp(d);
                        break;
                    default:
                        BeginPlaceStamp(RubberStamps.BuiltIn[0]);
                        break;
                }
            }
        };
    }

    /// <summary>Arms the Stamp tool with a built-in stamp: the next click on a page places it.</summary>
    public void BeginPlaceStamp(StampDesign design)
    {
        var (w, h) = RubberStamps.NaturalSize(design);
        Arm(w, h);
        (_pendingStamp, _pendingCustomStamp) = (design, null);
        Notify($"Click where the {design.Label} stamp goes. Drag it to move it, drag its corner to resize, Delete removes it.");
    }

    /// <summary>Arms the Stamp tool with a custom stamp.</summary>
    public void BeginPlaceStamp(SavedStamp stamp)
    {
        BitmapSource bmp;
        try
        {
            bmp = stamp.Load();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            Notify($"The stamp can't be read: {ex.Message}", error: true);
            return;
        }
        var h = 50.0;
        var w = Math.Min(220, h * bmp.PixelWidth / Math.Max(1, bmp.PixelHeight));
        Arm(w, w * bmp.PixelHeight / Math.Max(1, bmp.PixelWidth));
        (_pendingStamp, _pendingCustomStamp) = (null, stamp);
        Notify($"Click where the {stamp.Name} stamp goes. Drag it to move it, drag its corner to resize, Delete removes it.");
    }

    private void Arm(double w, double h)
    {
        SetOrganizing(false);
        Viewer.SignatureStampSize = new Size(w, h);
        // Set before the tool, so the tool change doesn't pick a stamp of its own.
        _pendingStamp = RubberStamps.BuiltIn[0];
        Viewer.Tool = ViewerTool.AddStamp;
        Viewer.Focus();
    }

    private async Task PlaceStamp(SignaturePlacement p)
    {
        var (design, custom) = (_pendingStamp, _pendingCustomStamp);
        var author = Author;
        Viewer.Tool = ViewerTool.Hand;
        if (custom is not null)
        {
            _lastStamp = custom;
            StampImage image;
            try
            {
                image = SignatureImages.ToStampImage(custom.Load());
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
            {
                Notify($"The stamp can't be read: {ex.Message}", error: true);
                return;
            }
            await RunStampEdit("Add stamp", (d, pw) => RubberStamps.PutImage(d, pw, p.PageIndex, p.Rect, image, custom.Name, author));
        }
        else if (design is not null)
        {
            _lastStamp = design;
            // "Approved by" says when it was placed.
            if (design.Detail is not null) design = ApprovedByNow();
            await RunStampEdit($"Add {design.Label.ToLowerInvariant()} stamp", (d, pw) => RubberStamps.Put(d, pw, p.PageIndex, p.Rect, design, author));
        }
    }

    internal string DevPendingStamp => _pendingStamp?.Label ?? _pendingCustomStamp?.Name ?? "(none)";
}
