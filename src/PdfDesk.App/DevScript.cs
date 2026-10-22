using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfDesk.App.Viewer;

namespace PdfDesk.App;

/// <summary>
/// Developer aid: <c>PdfDesk.exe file.pdf --script steps.txt</c> runs UI steps off-screen and saves window snapshots,
/// so the UI can be verified without capturing the user's screen. One step per line:
/// <c>wait ms · shot out.png · page N · zoom 150|fitwidth|fitpage · layout Continuous|Facing|… · find text ·
/// findnext · tool hand|select|snapshot · selectall · nav pages|bookmarks|search · select 1,3 · rotate 90 ·
/// delete · undo · redo · scroll dy · log text · exit</c>.
/// Results and errors go to <c>steps.txt.log</c>.
/// </summary>
internal static class DevScript
{
    public static long Started { get; } = Environment.TickCount64;

    public static async Task RunAsync(MainWindow window, string scriptPath)
    {
        var log = new List<string>();
        try
        {
            await Task.Delay(1500);
            foreach (var raw in await File.ReadAllLinesAsync(scriptPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var sp = line.IndexOf(' ');
                var cmd = sp < 0 ? line : line[..sp];
                var arg = sp < 0 ? "" : line[(sp + 1)..].Trim();
                var tab = window.ActiveTab;
                var v = tab?.PageView;
                switch (cmd.ToLowerInvariant())
                {
                    case "wait": await Task.Delay(int.Parse(arg)); break;
                    case "shot": Shot(window, arg); break;
                    case "page": v!.GoToPage(int.Parse(arg) - 1); break;
                    case "zoom" when arg == "fitwidth": v!.SetZoomMode(ZoomMode.FitWidth); break;
                    case "zoom" when arg == "fitpage": v!.SetZoomMode(ZoomMode.FitPage); break;
                    case "zoom": v!.SetZoom(double.Parse(arg) / 100); break;
                    case "layout": v!.SetLayout(Enum.Parse<ViewLayout>(arg, true)); break;
                    case "tool": v!.Tool = arg switch { "select" => ViewerTool.SelectText, "snapshot" => ViewerTool.Snapshot, _ => ViewerTool.Hand }; break;
                    case "selectall": v!.SelectAll(); break;
                    case "find": Cmd.Find.Execute(null, window); SetFindText(tab!, arg); tab!.FindNext(false); break;
                    case "findnext": tab!.FindNext(false); break;
                    case "nav": Nav(tab!, arg); break;
                    case "select": tab!.SelectPages(arg.Split(',').Select(s => int.Parse(s) - 1)); break;
                    case "rotate": await Organize.RotateSelected(tab!, int.Parse(arg)); break;
                    case "delete": await Organize.DeleteSelected(tab!); break;
                    case "undo": tab!.Undo(); break;
                    case "redo": tab!.Redo(); break;
                    case "scroll": v!.ScrollToVerticalOffset(v.VerticalOffset + double.Parse(arg)); break;
                    case "copytext": log.Add($"selected text: {v!.GetSelectedText().Length} chars: {Trunc(v.GetSelectedText())}"); break;
                    case "state": log.Add($"page={v!.CurrentPage + 1}/{v.PageCount} zoom={v.Zoom:P0} mode={v.ZoomMode} layout={v.Layout} dirty={tab!.Session.IsDirty} undo={tab.Session.UndoLabel ?? "-"} redo={tab.Session.RedoLabel ?? "-"} selected={string.Join(',', tab.SelectedPages.Select(p => p + 1))} {v.Diagnostics}"); break;
                    case "log": log.Add(arg); break;
                    case "screen": await ScreenShot(window, arg); break;
                    case "organize": tab!.SetOrganizing(arg != "off"); break;
                    case "theme": Application.Current.ThemeMode = arg == "dark" ? ThemeMode.Dark : ThemeMode.Light; break;
                    case "darkpages": Cmd.DarkPages.Execute(null, window); break;
                    case "toast": window.ShowToast(new Toast(arg, "Undo", () => { })); break;
                    case "palette":
                        Cmd.CommandPalette.Execute(null, window);
                        if (window.FindName("PaletteBox") is System.Windows.Controls.TextBox pb) pb.Text = arg;
                        break;
                    case "closepalette": window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent }); break;
                    case "mem":
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        using var proc = System.Diagnostics.Process.GetCurrentProcess();
                        log.Add($"mem: working set {proc.WorkingSet64 / 1048576} MB, private {proc.PrivateMemorySize64 / 1048576} MB, managed {GC.GetTotalMemory(false) / 1048576} MB");
                        break;
                    }
                    case "time": log.Add($"time: {Environment.TickCount64 - Started} ms since start"); break;
                    case "xps":
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var n = await FileActions.WriteXps(tab!.Session.Engine, [.. Enumerable.Range(0, Math.Min(6, v!.PageCount))], arg);
                        log.Add($"xps: {n} pages, {new FileInfo(arg).Length / 1024} KB in {sw.ElapsedMilliseconds} ms");
                        break;
                    }
                    case "dialog":
                    {
                        // dialog <CommandName> <png>: open a dialog, snapshot it, cancel it.
                        var parts = arg.Split(' ', 2);
                        var command = (System.Windows.Input.RoutedUICommand)typeof(Cmd).GetField(parts[0])!.GetValue(null)!;
                        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                        timer.Tick += (_, _) =>
                        {
                            timer.Stop();
                            var dlg = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != window && w.IsVisible);
                            if (dlg is null)
                            {
                                log.Add($"dialog {parts[0]}: no window appeared");
                                return;
                            }
                            Shot(dlg, parts[1]);
                            log.Add($"dialog {parts[0]}: '{dlg.Title}' {dlg.ActualWidth:0}x{dlg.ActualHeight:0}");
                            dlg.Close();
                        };
                        timer.Start();
                        command.Execute(null, window);
                        await Task.Delay(1200);
                        break;
                    }
                    case "exit": break;
                    default: log.Add($"unknown step: {line}"); break;
                }
                log.Add($"ok: {line}");
                await Task.Delay(150);
            }
        }
        catch (Exception ex)
        {
            log.Add($"FAILED: {ex}");
        }
        await File.WriteAllLinesAsync(scriptPath + ".log", log);
        Application.Current.Shutdown();
    }

    private static string Trunc(string s) => (s.Length > 120 ? s[..120] + "…" : s).Replace("\r\n", " ⏎ ");

    private static void SetFindText(DocumentTab tab, string text)
    {
        if (tab.FindName("FindBox") is System.Windows.Controls.TextBox box) box.Text = text;
    }

    private static void Nav(DocumentTab tab, string which) =>
        tab.ShowPane(which switch { "bookmarks" => NavPaneKind.Bookmarks, "search" => NavPaneKind.Search, "none" => NavPaneKind.None, _ => NavPaneKind.Pages });

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 r);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    /// <summary>Shows the window on screen for a moment and captures only its own rectangle (real Mica and caption buttons).</summary>
    private static async Task ScreenShot(Window window, string path)
    {
        var (left, top, topmost) = (window.Left, window.Top, window.Topmost);
        window.Left = 40;
        window.Top = 40;
        window.Topmost = true;
        await Task.Delay(1200);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hbm = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, hbm);
        BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000);
        SelectObject(mem, old);
        var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        DeleteObject(hbm);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        window.Topmost = topmost;
        window.Left = left;
        window.Top = top;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void Shot(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bmp = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = window.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(bg, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
