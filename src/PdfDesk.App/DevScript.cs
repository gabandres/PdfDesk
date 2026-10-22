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
/// findnext · tool hand|select|snapshot · selectall · keys text (real key messages; {tab} = Tab) · editor · nav pages|bookmarks|search · select 1,3 · rotate 90 ·
/// delete · undo · redo · scroll dy · log text · exit</c>, plus <c>key Delete ctrl shift</c> (one WPF Key with modifiers),
/// <c>focus</c> (log the focused element) and <c>annots</c> (log the comments). Review steps: <c>selecttext p x1 y1 x2 y2 ·
/// highlight · underline · strikeout · note p x y text · editcomment i text · deletecomment i · selectcomment i ·
/// commentfilter text · pane comments|attachments|layers|signatures · redact p x y w h · redact-text · redact-search ·
/// apply-redactions [confirm] · attachments · attach path · extract i path · open-attachment i · remove-attachment i ·
/// layers · layer name on|off · sigs · bars · labels · gotopage label · title · open path · pane-state</c>.
/// Results and errors go to <c>steps.txt.log</c>. Debug builds only, or any build with PDFDESK_DEVSCRIPT=1.
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
                    case "tool": v!.Tool = arg switch { "select" => ViewerTool.SelectText, "snapshot" => ViewerTool.Snapshot, "addtext" => ViewerTool.AddText, "check" => ViewerTool.AddCheck, _ => ViewerTool.Hand }; break;
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
                    case "cmd":
                    {
                        var c = (System.Windows.Input.RoutedUICommand)typeof(Cmd).GetField(arg)!.GetValue(null)!;
                        log.Add($"cmd {arg}: can={c.CanExecute(null, window)}");
                        c.Execute(null, window);
                        break;
                    }
                    case "fields":
                        log.Add($"fillable widgets: {v!.FillableCount}");
                        for (var i = 0; i < Math.Min(v.FillableCount, int.TryParse(arg, out var nf) ? nf : 6); i++) log.Add("  " + v.DevDescribeField(i));
                        break;
                    case "click":
                    {
                        var a = arg.Split(' ');
                        log.Add($"click consumed: {v!.DevClick(int.Parse(a[0]) - 1, double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture))}");
                        break;
                    }
                    case "type": v!.DevType(arg); break;
                    case "keys": await PostKeys(window, arg); break;
                    case "key": await PostKey(window, arg); break;
                    case "focus":
                    {
                        var f = System.Windows.Input.Keyboard.FocusedElement;
                        log.Add($"focus: {f?.GetType().Name ?? "(none)"}{(f is FrameworkElement { Name.Length: > 0 } fe ? $" '{fe.Name}'" : "")}");
                        break;
                    }
                    case "annots": log.Add(Annotations(tab!)); break;
                    case "selecttext":
                    {
                        // selecttext <page> <x1> <y1> <x2> <y2>: select the text between two page points (points, top-left origin).
                        var a = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var n = a.Skip(1).Select(Num).ToArray();
                        log.Add($"selecttext: {v!.DevSelect(int.Parse(a[0]) - 1, n[0], n[1], n[2], n[3])} '{Trunc(v.GetSelectedText())}'");
                        break;
                    }
                    case "highlight": log.Add($"highlight: {await tab!.MarkupSelection(Core.TextMarkupKind.Highlight)}"); break;
                    case "underline": log.Add($"underline: {await tab!.MarkupSelection(Core.TextMarkupKind.Underline)}"); break;
                    case "strikeout": log.Add($"strikeout: {await tab!.MarkupSelection(Core.TextMarkupKind.StrikeOut)}"); break;
                    case "note":
                    {
                        // note <page> <x> <y> <text…>
                        var a = arg.Split(' ', 4);
                        log.Add($"note: {await tab!.AddNoteAt(int.Parse(a[0]) - 1, Num(a[1]), Num(a[2]), a[3])}");
                        break;
                    }
                    case "editcomment":
                    {
                        var a = arg.Split(' ', 2);
                        log.Add($"editcomment: {await tab!.EditComment(tab.Comments.Where(c => !c.IsPdfDeskStamp).ElementAt(int.Parse(a[0])), a[1])}");
                        break;
                    }
                    case "deletecomment": log.Add($"deletecomment: {await tab!.DeleteComment(tab.Comments.Where(c => !c.IsPdfDeskStamp).ElementAt(int.Parse(arg)))}"); break;
                    case "selectcomment":
                        if (tab!.FindName("CommentList") is System.Windows.Controls.ListBox cl) cl.SelectedIndex = int.Parse(arg);
                        break;
                    case "commentfilter":
                        if (tab!.FindName("CommentFilter") is System.Windows.Controls.TextBox cf) cf.Text = arg;
                        log.Add($"comments shown: {(tab.FindName("CommentList") as System.Windows.Controls.ListBox)?.Items.Count}");
                        break;
                    case "pane":
                        tab!.ShowReviewPane(Enum.Parse<ReviewPane>(arg, true));
                        break;
                    case "redact":
                    {
                        // redact <page> <x> <y> <w> <h>: mark an area (points, top-left origin)
                        var a = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        tab!.MarkRedaction(int.Parse(a[0]) - 1, new Core.RectD(Num(a[1]), Num(a[2]), Num(a[3]), Num(a[4])));
                        log.Add($"redaction marks: {v!.Redactions.Count}");
                        break;
                    }
                    case "redact-text": log.Add($"redact-text: {tab!.RedactSelection()} marks"); break;
                    case "redact-search": log.Add($"redact-search: {tab!.RedactSearchHits()} marks"); break;
                    case "apply-redactions": log.Add($"apply-redactions: {await tab!.ApplyRedactions(confirm: arg == "confirm")}"); break;
                    case "attachments":
                        log.Add($"attachments: {tab!.AttachedFiles.Count}");
                        foreach (var f in tab.AttachedFiles) log.Add($"  {f.FileName} {f.Size} bytes {f.Source} p{f.PageIndex + 1} '{f.Description}'");
                        break;
                    case "attach": log.Add($"attach: {await tab!.AddAttachment(arg, "Added by the dev script")}"); break;
                    case "extract":
                    {
                        var a = arg.Split(' ', 2);
                        await tab!.SaveAttachment(Attachment(tab, a[0]), a[1]);
                        log.Add($"extract: {new FileInfo(a[1]).Length} bytes");
                        break;
                    }
                    case "open-attachment":
                        await tab!.OpenAttachment(Attachment(tab, arg));
                        log.Add($"tabs: {window.TabCount}, active: {window.ActiveTab?.DisplayTitle}");
                        break;
                    case "remove-attachment": log.Add($"remove-attachment: {await tab!.RemoveAttachment(Attachment(tab, arg))}"); break;
                    case "layers":
                        log.Add($"layers: {tab!.LayerList.Count}, override={tab.HasLayerOverride}");
                        foreach (var l in tab.LayerList) log.Add($"  {l.Id} {l.Name} default={l.DefaultVisible} locked={l.Locked}");
                        break;
                    case "layer":
                    {
                        // layer <name> on|off
                        var at = arg.LastIndexOf(' ');
                        tab!.SetLayerVisible(arg[..at], arg[(at + 1)..] == "on");
                        await Task.Delay(800);
                        log.Add($"layer: override={tab.HasLayerOverride}");
                        break;
                    }
                    case "sigs":
                        log.Add($"signatures: {tab!.SignatureResults.Count}, bar: {BarText(tab, "SigBar", "SigBarText")}");
                        foreach (var s in tab.SignatureResults) log.Add($"  {s.FieldName}: {s.Status} signer={s.SignerName} trusted={s.CertificateTrusted}");
                        break;
                    case "bars":
                        foreach (var bar in new[] { "DiskBar", "FormBar", "XfaBar", "SigBar", "RedactBar" })
                            if (tab!.FindName(bar) is FrameworkElement { Visibility: Visibility.Visible }) log.Add($"bar {bar} visible");
                        log.Add($"redact bar: {BarText(tab!, "RedactBar", "RedactBarText")}");
                        break;
                    case "labels":
                        log.Add($"labels: {string.Join(' ', Enumerable.Range(0, v!.PageCount).Take(12).Select(tab!.PageLabel))}; pill: " +
                                $"'{(tab.FindName("PageBox") as System.Windows.Controls.TextBox)?.Text}' '{(tab.FindName("PageCountText") as System.Windows.Controls.TextBlock)?.Text}'");
                        break;
                    case "gotopage":
                        // gotopage <label or number>: what typing it into the page box does
                        log.Add($"gotopage {arg}: page index {tab!.FindPage(arg)?.ToString() ?? "none"}");
                        if (tab.FindPage(arg) is { } gp) v!.GoToPage(gp);
                        break;
                    case "title": log.Add($"title: tab '{tab!.DisplayTitle}', window '{window.Title}'"); break;
                    case "open": await window.OpenFileAsync(arg); log.Add($"tabs: {window.TabCount}"); break;
                    case "pane-state": log.Add($"pane: {tab!.CurrentPane} layout={v!.Layout} zoom={v.ZoomMode} page={v.CurrentPage + 1}"); break;
                    case "throw":
                        // throw N: N identical unhandled UI-thread exceptions (checks the error dialog's rate limiting).
                        for (var i = 0; i < int.Parse(arg); i++)
                            _ = window.Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("Dev script test exception")));
                        break;
                    case "prompt": log.Add(ClickButton(window, arg) ?? $"prompt: no window with a '{arg}' button"); break;
                    case "answer":
                    {
                        // answer <button>: the next step may open a modal prompt; click <button> in it once it shows (up to ~6 s).
                        var tries = 0;
                        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                        var button = arg;
                        timer.Tick += (_, _) =>
                        {
                            if (ClickButton(window, button) is { } clicked) log.Add($"answer: {clicked}");
                            else if (++tries < 20) return;
                            else log.Add($"answer: no prompt with a '{button}' button appeared");
                            timer.Stop();
                        };
                        timer.Start();
                        break;
                    }
                    case "dialogok":
                    {
                        // dialogok <CommandName> [combo<N>=<index>] [text<N>=<value>] [check<N>=on|off] [answer=<button>]:
                        // open a command's FormDialog, set its Nth combo/text/check box, press OK, then (optionally) press
                        // <button> in the prompts that follow. Logs the dialog's validation error if it stays open.
                        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var command = (System.Windows.Input.RoutedUICommand)typeof(Cmd).GetField(parts[0])!.GetValue(null)!;
                        var settings = parts.Skip(1).Select(p => p.Split('=', 2)).ToList();
                        var answer = settings.FirstOrDefault(s => s[0] == "answer")?[1];
                        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                        var stage = 0;
                        timer.Tick += (_, _) =>
                        {
                            if (stage++ == 0)
                            {
                                if (Application.Current.Windows.OfType<FormDialog>().FirstOrDefault(w => w.IsVisible) is not { } dlg)
                                {
                                    timer.Stop();
                                    log.Add($"dialogok {parts[0]}: no dialog appeared");
                                    return;
                                }
                                FillDialog(dlg, settings);
                                var ok = Descendants<System.Windows.Controls.Button>(dlg).First(b => b.IsDefault);
                                ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                if (dlg.IsVisible)
                                {
                                    var error = Descendants<System.Windows.Controls.TextBlock>(dlg).Where(t => t.IsVisible).Select(t => t.Text).LastOrDefault();
                                    log.Add($"dialogok {parts[0]}: stayed open: {error}");
                                    dlg.Close();
                                    timer.Stop();
                                }
                                return;
                            }
                            if (answer is null || stage > 8)
                            {
                                timer.Stop();
                                return;
                            }
                            if (ClickButton(window, answer) is { } clicked) log.Add($"dialogok {parts[0]}: {clicked}");
                        };
                        timer.Start();
                        command.Execute(null, window);
                        await Task.Delay(answer is null ? 1200 : 3000);
                        timer.Stop();
                        break;
                    }
                    case "editor": log.Add($"editor: {v!.DevEditorText ?? "(closed)"}"); break;
                    case "commit": v!.CommitEditor(); break;
                    case "save":
                        tab!.Session.SaveAs(arg);
                        log.Add($"saved {new FileInfo(arg).Length} bytes");
                        break;
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
        tab.ShowPane(Enum.TryParse<NavPaneKind>(which, true, out var kind) ? kind : NavPaneKind.Pages);

    /// <summary>
    /// Types like a keyboard (WM_KEYDOWN, WM_KEYUP; the message loop makes the WM_CHAR), unlike "type", which sets the
    /// editor's text directly. Lowercase letters, digits, space and {tab} only (no Shift).
    /// </summary>
    private static async Task PostKeys(Window window, string text)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        foreach (var ch in text.Replace("{tab}", "\t"))
        {
            var vk = (nint)(VkKeyScanW(ch) & 0xFF);
            PostMessageW(hwnd, 0x0100, vk, 1);
            PostMessageW(hwnd, 0x0101, vk, unchecked((nint)0xC0000001));
            await Task.Delay(40);
        }
    }

    /// <summary>
    /// <c>key Delete ctrl shift</c>: posts WM_KEYDOWN/UP for one WPF <see cref="System.Windows.Input.Key"/>, with the modifier
    /// keys pressed around it. Posted messages don't change the thread's key state, so the modifiers are also set in it
    /// (SetKeyboardState) while the key is processed; WPF reads Keyboard.Modifiers from there. Alt combos are sent as
    /// WM_SYSKEYDOWN. Limits: no character messages for Alt (menus' access keys may not react), and the key state is
    /// restored after ~150 ms, so a handler that runs later sees no modifiers.
    /// </summary>
    private static async Task PostKey(Window window, string spec)
    {
        var parts = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var key = Enum.Parse<System.Windows.Input.Key>(parts[0], ignoreCase: true);
        var mods = parts.Skip(1).Select(m => m.ToLowerInvariant() switch
        {
            "ctrl" or "control" => 0x11,
            "shift" => 0x10,
            "alt" => 0x12,
            _ => throw new ArgumentException($"Unknown modifier '{m}' (use ctrl, shift, alt)."),
        }).Distinct().ToList();
        var alt = mods.Contains(0x12);
        uint down = alt ? 0x0104u : 0x0100u, up = alt ? 0x0105u : 0x0101u;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);

        var saved = new byte[256];
        GetKeyboardState(saved);
        var state = (byte[])saved.Clone();
        foreach (var m in mods)
        {
            state[m] |= 0x80;
            state[m switch { 0x11 => 0xA2, 0x10 => 0xA0, _ => 0xA4 }] |= 0x80; // left-hand variant too
        }
        SetKeyboardState(state);
        try
        {
            nint context = alt ? 1 << 29 : 0; // lParam bit 29: Alt is down
            foreach (var m in mods) PostMessageW(hwnd, down, m, 1 | context);
            PostMessageW(hwnd, down, vk, 1 | context);
            PostMessageW(hwnd, up, vk, unchecked((nint)0xC0000001) | context);
            foreach (var m in Enumerable.Reverse(mods)) PostMessageW(hwnd, up, m, unchecked((nint)0xC0000001) | context);
            await Task.Delay(150);
        }
        finally
        {
            SetKeyboardState(saved);
        }
    }

    private static void FillDialog(Window dlg, List<string[]> settings)
    {
        var combos = Descendants<System.Windows.Controls.ComboBox>(dlg).ToList();
        var texts = Descendants<System.Windows.Controls.TextBox>(dlg).Where(t => t.TemplatedParent is null).ToList();
        var checks = Descendants<System.Windows.Controls.CheckBox>(dlg).ToList();
        foreach (var s in settings.Where(s => s.Length == 2))
        {
            if (s[0].StartsWith("combo")) combos[int.Parse(s[0][5..])].SelectedIndex = int.Parse(s[1]);
            else if (s[0].StartsWith("text")) texts[int.Parse(s[0][4..])].Text = s[1].Replace("{sp}", " ");
            else if (s[0].StartsWith("check")) checks[int.Parse(s[0][5..])].IsChecked = s[1] == "on";
        }
    }

    /// <summary>Clicks the button labelled <paramref name="text"/> in a visible secondary window; returns what it did, or null.</summary>
    private static string? ClickButton(Window main, string text)
    {
        foreach (var w in Application.Current.Windows.OfType<Window>().Where(w => w != main && w.IsVisible).Reverse())
        {
            var button = Descendants<System.Windows.Controls.Button>(w).FirstOrDefault(b => b.Content as string == text && b.IsEnabled);
            if (button is null) continue;
            var title = Descendants<System.Windows.Controls.TextBlock>(w).FirstOrDefault(t => t.FontSize >= 18)?.Text ?? w.Title;
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return $"clicked '{text}' in '{title}'";
        }
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    /// <summary>The comments the tab has read (Fill &amp; Sign items excluded), one line each.</summary>
    private static string Annotations(DocumentTab tab)
    {
        var list = tab.Comments.Where(c => !c.IsPdfDeskStamp).ToList();
        return $"annots: {list.Count}" + string.Concat(list.Select(c =>
            $"\n  {c.Subtype} p{c.PageIndex + 1} {c.Rect.X:0},{c.Rect.Y:0} {c.Rect.Width:0}x{c.Rect.Height:0} quads={c.QuadRects.Count} by '{c.Author}' replyTo={c.ReplyTo ?? "-"}: '{Trunc(c.Contents)}'"));
    }

    private static Core.AttachmentInfo Attachment(DocumentTab tab, string nameOrIndex) =>
        tab.AttachedFiles.FirstOrDefault(f => f.FileName == nameOrIndex) ?? tab.AttachedFiles[int.Parse(nameOrIndex)];

    private static double Num(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private static string BarText(DocumentTab tab, string bar, string text) =>
        tab.FindName(bar) is FrameworkElement { Visibility: Visibility.Visible } && tab.FindName(text) is System.Windows.Controls.TextBlock tb ? tb.Text : "(hidden)";

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short VkKeyScanW(char ch);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, nint wParam, nint lParam);
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
