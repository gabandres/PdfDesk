using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using InkvoltPdf.App.Viewer;
using InkvoltPdf.Core;

namespace InkvoltPdf.App;

/// <summary>
/// Pictures (the Image tool: a picture from a file or the clipboard, placed like a handwritten signature) and links
/// (the Link tool: drag a box, then say where it leads; move, resize, edit and delete existing links).
/// </summary>
public partial class DocumentTab
{
    private StampImage? _pendingImage;
    private string _pendingImageName = "Image";
    private bool _pickingImage;

    private void InitInsert()
    {
        Viewer.ToolChanged += (_, _) =>
        {
            if (Viewer.Tool != ViewerTool.AddImage) _pendingImage = null;
            // The Image tool chosen with its key or button: pick the picture first.
            else if (_pendingImage is null && !_pickingImage) Dispatcher.BeginInvoke(() => _ = BeginAddImage(null));
        };
        Viewer.LinkDrawn += (_, p) => _ = AddLinkAt(p);
        Viewer.LinkChanged += (_, c) => _ = ChangeLink(c);
        Viewer.LinkEditRequested += (_, l) => _ = EditLink(l);
        CtxEditLink.Click += (_, _) => { if (Viewer.ContextLink is { } l) _ = EditLink(l); };
        CtxDeleteLink.Click += (_, _) => { if (Viewer.ContextLink is { } l) _ = ChangeLink(new LinkChange(l.Id, l.PageIndex, null)); };
        Viewer.ContextMenuOpening += (_, _) =>
        {
            var link = Session.IsPdf && !IsOrganizing ? Viewer.ContextLink : null;
            foreach (var item in new FrameworkElement[] { CtxEditLink, CtxDeleteLink, CtxLinkSeparator })
                item.Visibility = link is not null ? Visibility.Visible : Visibility.Collapsed;
            if (link is not null) CtxEditLink.Header = $"_Edit Link ({Trim(link.Describe(), 40)})…";
        };
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ───────────────────────────── Pictures ─────────────────────────────

    /// <summary>
    /// Picks a picture (<paramref name="path"/>, else from a file dialog; or the clipboard's image) and arms the Image tool:
    /// the next click on a page places it. False when there was nothing to place.
    /// </summary>
    public async Task<bool> BeginAddImage(string? path, bool fromClipboard = false)
    {
        if (!Session.IsPdf || _disposed || _pickingImage) return false;
        _pickingImage = true;
        try
        {
            BitmapSource bmp;
            string name;
            if (fromClipboard)
            {
                if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } clip)
                {
                    Notify("There's no image on the clipboard. Copy a picture (for example a screenshot) first.");
                    return Disarm();
                }
                bmp = SignatureImages.Fit(SignatureImages.FromClipboard((BitmapSource)clip.GetAsFrozen()), 2000);
                name = "Pasted image";
            }
            else
            {
                if (path is null)
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Add Image", Filter = FileTools.ImageFilter, InitialDirectory = AppSettings.Current.LastFolder };
                    if (dlg.ShowDialog(Window.GetWindow(this)) != true) return Disarm();
                    path = dlg.FileName;
                }
                var file = path;
                bmp = await Task.Run(() => SignatureImages.LoadImage(file));
                name = Path.GetFileName(path);
            }
            var image = await Task.Run(() => SignatureImages.ToStampImage(bmp));
            if (_disposed) return false;
            SetOrganizing(false);
            // Its size at 96 dpi, no larger than half the page.
            var g = Viewer.Engine?.GetPage(Math.Clamp(Viewer.CurrentPage, 0, Math.Max(0, Viewer.PageCount - 1)));
            double w = bmp.PixelWidth * 0.75, h = bmp.PixelHeight * 0.75;
            var fit = Math.Min(1, Math.Min((g?.Width ?? 612) * 0.5 / w, (g?.Height ?? 792) * 0.5 / h));
            Viewer.SignatureStampSize = new Size(Math.Max(8, w * fit), Math.Max(8, h * fit));
            _pendingImage = image;
            _pendingImageName = name;
            Viewer.Tool = ViewerTool.AddImage;
            Notify("Click where the picture goes. Drag it to move it, drag its corner to resize, Delete removes it.");
            Viewer.Focus();
            return true;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or COMException or ExternalException)
        {
            Log.Write($"Add image failed: {ex.Message}");
            Notify($"That picture can't be read: {ex.Message}", error: true);
            return Disarm();
        }
        finally
        {
            _pickingImage = false;
        }

        bool Disarm()
        {
            if (Viewer.Tool == ViewerTool.AddImage && _pendingImage is null) Viewer.Tool = ViewerTool.Hand;
            return false;
        }
    }

    private async Task PlaceImage(SignaturePlacement p)
    {
        if (_pendingImage is not { } image) return;
        var name = _pendingImageName;
        var author = Author;
        Viewer.Tool = ViewerTool.Hand;
        await RunStampEdit("Add image", (d, pw) => SignatureStamps.PutImage(d, pw, p.PageIndex, p.Rect, image, name, author));
    }

    // ───────────────────────────── Links ─────────────────────────────

    private async Task AddLinkAt(LinkPlacement p)
    {
        if (AskLinkTarget(null, p.PageIndex) is not { } target) return;
        await RunEdit("Add link", (d, pw) => Links.Put(d, pw, p.PageIndex, p.Rect, target.Uri, target.Page));
    }

    /// <summary>
    /// Makes the selected text a link (asks where it leads): the keyboard way to add one, with caret browsing or a
    /// selection. One link per page, over its selected lines; one undo step.
    /// </summary>
    public async Task<bool> LinkSelection()
    {
        if (!CanMarkup) return false;
        if (Viewer.SelectedPageCount > 50)
        {
            Notify("Select the text of at most 50 pages to make it a link.", error: true);
            return false;
        }
        if (await SelectionLineRects("Making a link") is not { } parts) return false;
        if (parts.Count == 0)
        {
            Notify("The selection has no text to make a link.");
            return false;
        }
        if (AskLinkTarget(null, parts[0].Page) is not { } target) return false;
        var ok = await RunEdit("Add link", (d, pw) =>
        {
            foreach (var (page, rects) in parts) d = Links.PutOnText(d, pw, page, rects, target.Uri, target.Page);
            return d;
        });
        if (ok) Viewer.ClearSelection();
        return ok;
    }

    /// <summary>Changes where a link leads (asks), keeping where it is.</summary>
    public async Task<bool> EditLink(LinkInfo link)
    {
        if (!ReviewIsCurrent() || AskLinkTarget(link, link.PageIndex) is not { } target) return false;
        return await RunEdit("Edit link", (d, pw) => Links.Put(d, pw, link.PageIndex, link.Rect, target.Uri, target.Page, link.Id));
    }

    private async Task<bool> ChangeLink(LinkChange c)
    {
        var outcome = !ReviewIsCurrent() ? EditOutcome.Busy
            : c.Rect is { } r
                ? await TryEdit("Move link", (d, pw) => Links.Move(d, pw, c.Id, c.PageIndex, r))
                : await TryEdit("Delete link", (d, pw) => Links.Delete(d, pw, c.Id));
        if (outcome is EditOutcome.Done or EditOutcome.NoChange) return true;
        // Not made (declined, failed, or the tab was busy): the page view already shows the box at its new place, so
        // show the links as the document has them.
        await RefreshLinks();
        return false;
    }

    private async Task RefreshLinks()
    {
        var data = Session.Data;
        var pw = Session.Password;
        try
        {
            var links = await Task.Run(() => Links.Read(data, pw));
            if (!_disposed && ReferenceEquals(data, Session.Data)) Viewer.SetLinkAnnotations(links);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"Reading links failed: {ex.Message}");
        }
    }

    /// <summary>Asks where a link leads: a web or email address, or a page of this document. Null when cancelled.</summary>
    private (string? Uri, int Page)? AskLinkTarget(LinkInfo? existing, int onPage)
    {
        if (Window.GetWindow(this) is not { } owner) return null;
        if (DevScript.TakeLinkTarget(out var scripted)) return scripted;
        var dlg = new FormDialog(owner, existing is null ? "Add Link" : "Edit Link", existing is null ? "Add" : "Save", 460);
        var group = $"link{GetHashCode()}";
        var toWeb = dlg.Add(null, new RadioButton { Content = "A web or email address", GroupName = group, IsChecked = existing is not { Uri: null, TargetPage: >= 0 } });
        var url = dlg.Add(null, new TextBox { Text = existing?.Uri ?? "https://", Margin = new Thickness(24, 4, 0, 0) });
        System.Windows.Automation.AutomationProperties.SetName(url, "Web or email address");
        var toPage = dlg.Add(null, new RadioButton { Content = "A page in this document", GroupName = group, IsChecked = existing is { Uri: null, TargetPage: >= 0 }, Margin = new Thickness(0, 12, 0, 0) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 4, 0, 0) };
        var page = new TextBox { Width = 80, Text = existing is { TargetPage: >= 0 } e ? PageLabel(e.TargetPage) : PageLabel(Math.Min(Viewer.PageCount - 1, onPage + 1)) };
        System.Windows.Automation.AutomationProperties.SetName(page, "Page");
        row.Children.Add(page);
        row.Children.Add(new TextBlock { Text = $"of {Viewer.PageCount}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        dlg.Add(null, row);
        dlg.AddNote("Inkvolt PDF asks before opening any address that isn't http, https or mailto; so do most viewers.");
        url.GotKeyboardFocus += (_, _) => toWeb.IsChecked = true;
        page.GotKeyboardFocus += (_, _) => toPage.IsChecked = true;
        dlg.Loaded += (_, _) =>
        {
            var box = toPage.IsChecked == true ? page : url;
            box.Focus();
            box.SelectAll();
        };

        (string? Uri, int Page) result = default;
        dlg.Validate(() =>
        {
            if (toPage.IsChecked == true)
            {
                if (FindPage(page.Text.Trim()) is not { } p) return $"Enter a page from 1 to {Viewer.PageCount}.";
                result = (null, p);
                return null;
            }
            if (Links.Normalize(url.Text) is not { } uri) return "Type a web address (https://…) or an email address.";
            // Addresses other than web and email open other programs: the same care as when opening such a link.
            if (!Links.IsSafe(uri) && !Prompt.Confirm(dlg, "Link to this address?",
                    $"{uri} isn't a web or email address, so it may open another program. Inkvolt PDF and most viewers ask before opening it. Only add it if you trust where it leads.",
                    "Add link", warning: true))
                return "Choose a web or email address instead.";
            result = (uri, -1);
            return null;
        });
        return dlg.ShowModal() ? result : null;
    }

    internal IReadOnlyList<LinkInfo> DevLinks => Viewer.DevLinks;
}
