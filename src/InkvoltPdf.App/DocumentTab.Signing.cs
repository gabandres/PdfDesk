using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using InkvoltPdf.App.Viewer;
using InkvoltPdf.Core;

namespace InkvoltPdf.App;

/// <summary>Digital signing (placing the box, applying the signature) and handwritten signature stamps (Fill &amp; Sign).</summary>
public partial class DocumentTab
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private TaskCompletionSource<SignaturePlacement?>? _sigBoxWait;
    private StampImage? _pendingSigImage;
    private string _pendingSigLabel = "Signature";

    /// <summary>Asks the window to run "Sign Digitally": null = a new signature, else the empty field to sign.</summary>
    public event EventHandler<string?>? SignRequested;

    private void InitSigning()
    {
        Viewer.SignatureFieldActivated += (_, w) => OnSignatureField(w.FieldName);
        Viewer.SignatureBoxDrawn += (_, p) =>
        {
            if (_sigBoxWait is not { } wait) return;
            _sigBoxWait = null;
            Viewer.Tool = ViewerTool.Hand;
            wait.TrySetResult(p);
        };
        Viewer.ToolChanged += (_, _) =>
        {
            if (Viewer.Tool != ViewerTool.PlaceSignature && _sigBoxWait is { } wait)
            {
                _sigBoxWait = null;
                wait.TrySetResult(null);
            }
            if (Viewer.Tool != ViewerTool.AddSignature) _pendingSigImage = null;
        };
        Viewer.SignatureStampPlaced += (_, p) => _ = Viewer.Tool switch
        {
            ViewerTool.AddImage => PlaceImage(p),
            ViewerTool.AddStamp => PlaceStamp(p),
            _ => PlaceSignatureStamp(p),
        };
        Viewer.SignatureStampChanged += (_, c) => _ = ChangeSignatureStamp(c);
    }

    /// <summary>A signature field was clicked: an empty one is signed; a signed one shows its details.</summary>
    private void OnSignatureField(string fieldName)
    {
        var info = _signatures.FirstOrDefault(s => s.FieldName == fieldName);
        if (info is { Status: not SignatureStatus.Unsigned })
        {
            ShowSignatureDetails(fieldName);
            return;
        }
        SignRequested?.Invoke(this, fieldName);
    }

    /// <summary>Opens the Signatures pane with the field's entry selected.</summary>
    public void ShowSignatureDetails(string? fieldName)
    {
        _sigBarDismissed = false;
        ShowPane(NavPaneKind.Signatures);
        if (fieldName is null) return;
        var item = SignatureList.Items.OfType<SignatureItem>().FirstOrDefault(i => i.Info.FieldName == fieldName);
        if (item is null) return;
        _shownSignature = fieldName;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (SignatureList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement fe) fe.BringIntoView();
        });
        if (item.Info.PageIndex >= 0 && !item.Info.Rect.IsEmpty) Viewer.Flash(item.Info.PageIndex, item.Info.Rect);
    }

    private string? _shownSignature;

    private void OnSignField(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SignatureItem s } && !IsBusy) SignRequested?.Invoke(this, s.Info.FieldName);
    }

    /// <summary>The empty signature field <paramref name="fieldName"/>: its box (width/height) and whether it is visible.</summary>
    public (double Aspect, bool Visible) SignatureFieldShape(string fieldName)
    {
        var info = _signatures.FirstOrDefault(s => s.FieldName == fieldName);
        return info is null || info.Rect.IsEmpty ? (3.2, false) : (info.Rect.Width / info.Rect.Height, true);
    }

    /// <summary>Lets the user drag the visible signature's box on a page. Null when cancelled (Esc, another tool).</summary>
    public Task<SignaturePlacement?> PickSignatureBoxAsync()
    {
        _sigBoxWait?.TrySetResult(null);
        _sigBoxWait = null;
        SetOrganizing(false);
        Viewer.Tool = ViewerTool.PlaceSignature;
        var wait = new TaskCompletionSource<SignaturePlacement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sigBoxWait = wait;
        Notify("Drag a box on the page where the signature goes (Esc cancels).");
        Viewer.Focus();
        return wait.Task;
    }

    /// <summary>Signs the document (off the UI thread; smart card and Windows Hello PIN prompts come from Windows).</summary>
    public async Task<EditOutcome> ApplySignatureAsync(SignRequest request)
    {
        var outcome = await TryEdit("Sign", (d, pw) => Signing.Sign(d, pw, request), quiet: true, contentOnly: true);
        if (outcome == EditOutcome.Done) _sigBarDismissed = false;
        return outcome;
    }

    /// <summary>The RFC 3161 exchange with a timestamp server: posts the request (a hash only), returns the reply.</summary>
    public static Func<byte[], CancellationToken, Task<byte[]>> TimestampClient(string url) => async (request, cancel) =>
    {
        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
        using var response = await Http.PostAsync(url, content, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
    };

    // ───────────────────────────── Handwritten signatures ─────────────────────────────

    /// <summary>Arms the Add signature tool with a saved signature: the next click on a page places it.</summary>
    public void BeginPlaceSignature(SavedSignature saved)
    {
        StampImage image;
        System.Windows.Media.Imaging.BitmapSource bmp;
        try
        {
            bmp = saved.Load();
            image = SignatureImages.ToStampImage(bmp);
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UnauthorizedAccessException)
        {
            Notify($"The saved signature can't be read: {ex.Message}", error: true);
            return;
        }
        SetOrganizing(false);
        var h = saved.Initials ? 28.0 : 42.0;
        var w = Math.Min(260, h * bmp.PixelWidth / Math.Max(1, bmp.PixelHeight));
        Viewer.SignatureStampSize = new Size(w, w * bmp.PixelHeight / Math.Max(1, bmp.PixelWidth));
        Viewer.Tool = ViewerTool.AddSignature;
        _pendingSigImage = image;
        _pendingSigLabel = saved.Initials ? "Initials" : "Signature";
        Notify($"Click where the {(saved.Initials ? "initials go" : "signature goes")}. Drag it to move it, drag its corner to resize, Delete removes it.");
        Viewer.Focus();
    }

    private async Task PlaceSignatureStamp(SignaturePlacement p)
    {
        if (_pendingSigImage is not { } image)
        {
            Notify("Choose a signature first (Fill & Sign → Sign).");
            return;
        }
        var label = _pendingSigLabel;
        Viewer.Tool = ViewerTool.Hand;
        await RunStampEdit(label == "Initials" ? "Add initials" : "Add signature",
            (d, pw) => SignatureStamps.Put(d, pw, p.PageIndex, p.Rect, image, null, label));
    }

    /// <summary>A placed signature, picture or stamp moved, resized or deleted on the page.</summary>
    private Task ChangeSignatureStamp(SignatureStampChange c)
    {
        var kind = Viewer.PlacedStampKind(c.Id);
        var what = kind switch { StampKind.Image => "image", StampKind.Stamp => "stamp", _ => "signature" };
        if (c.Rect is not { } r) return RunStampEdit($"Remove {what}", (d, pw) => TextStamps.Remove(d, pw, c.Id));
        return kind == StampKind.Stamp
            ? RunStampEdit($"Move {what}", (d, pw) => RubberStamps.Move(d, pw, c.Id, c.PageIndex, r))
            : RunStampEdit($"Move {what}", (d, pw) => SignatureStamps.Put(d, pw, c.PageIndex, r, null, c.Id));
    }

    /// <summary>An undoable stamp edit; waits while another edit (or its prompt) holds the tab.</summary>
    private async Task RunStampEdit(string label, Func<byte[], string?, byte[]> edit)
    {
        while (true)
        {
            while (IsBusy)
            {
                await WhenIdle();
                if (_disposed) return;
            }
            if (await TryEdit(label, edit, contentOnly: true) != EditOutcome.Busy || _disposed) return;
        }
    }

    internal IReadOnlyList<TextStamp> DevSignatureStamps => Viewer.DevSignatureStamps;

    /// <summary>The signature last shown in the Signatures pane (for scripts).</summary>
    internal string? DevShownSignature => _shownSignature;
}
