using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PdfDesk.App.Viewer;
using PdfDesk.Core;

namespace PdfDesk.App;

/// <summary>A comment in the Comments pane (replies are indented under the comment they answer).</summary>
public sealed class CommentItem(AnnotationInfo info, string pageHeader, int depth)
{
    public AnnotationInfo Info { get; } = info;
    public string PageHeader { get; } = pageHeader;
    public Thickness Indent { get; } = new(depth * 18, 0, 0, 0);
    public string Contents => Info.Contents.Trim();
    public Visibility ContentsVisibility => Contents.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Title => Info.ReplyTo is not null ? "Reply" : Info.Kind switch
    {
        AnnotationKind.Text => "Note",
        AnnotationKind.FreeText => "Text box",
        AnnotationKind.StrikeOut => "Strike-out",
        AnnotationKind.Squiggly => "Squiggly underline",
        AnnotationKind.FileAttachment => "Attached file",
        AnnotationKind.Square => "Rectangle",
        AnnotationKind.Circle => "Ellipse",
        AnnotationKind.PolyLine => "Polyline",
        AnnotationKind.Ink => "Drawing",
        AnnotationKind.Caret => "Insert text",
        AnnotationKind.Redact => "Redaction mark",
        AnnotationKind.Other => Info.Subtype,
        _ => Info.Kind.ToString(),
    };

    public string Meta
    {
        get
        {
            var parts = new[] { Info.Author, Info.Modified?.LocalDateTime.ToString("g") }.Where(s => !string.IsNullOrWhiteSpace(s));
            var text = string.Join(" · ", parts);
            return text.Length > 0 ? "  " + text : "";
        }
    }

    public string Glyph => Info.Kind switch
    {
        AnnotationKind.Highlight => "",
        AnnotationKind.Underline or AnnotationKind.Squiggly => "",
        AnnotationKind.StrikeOut => "",
        AnnotationKind.Ink => "",
        AnnotationKind.FileAttachment => "",
        _ => "",
    };

    /// <summary>The comment's own color as a swatch (light gray when it has none).</summary>
    public Brush Swatch
    {
        get
        {
            var c = Info.Color is { } k
                ? Color.FromRgb((byte)Math.Round(Math.Clamp(k.R, 0, 1) * 255), (byte)Math.Round(Math.Clamp(k.G, 0, 1) * 255), (byte)Math.Round(Math.Clamp(k.B, 0, 1) * 255))
                : Color.FromRgb(0xD8, 0xD8, 0xD8);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    /// <summary>Dark glyph on light colors, white on dark ones.</summary>
    public Brush GlyphBrush => Info.Color is { } k && 0.299 * k.R + 0.587 * k.G + 0.114 * k.B < 0.55 ? Brushes.White : DarkGlyph;

    private static readonly Brush DarkGlyph = MakeFrozen(Color.FromRgb(0x20, 0x20, 0x20));
    private static Brush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public string AccessibleName => $"{Title}{(Info.Author is { Length: > 0 } a ? $" by {a}" : "")}, {PageHeader}: {Contents}";
    public override string ToString() => AccessibleName;
}

/// <summary>A file attached to the document, in the Attachments pane.</summary>
public sealed class AttachmentItem(AttachmentInfo info, string? pageLabel)
{
    public AttachmentInfo Info { get; } = info;
    public string Name => Info.FileName;
    public bool IsPdf => Info.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || Info.MimeType == "application/pdf";
    public string Glyph => IsPdf ? "" : "";

    public string Detail
    {
        get
        {
            var parts = new List<string> { FileActions.FormatSize(Info.Size) };
            if (pageLabel is not null) parts.Add($"on page {pageLabel}");
            if (!string.IsNullOrWhiteSpace(Info.Description) && Info.Description != Info.FileName) parts.Add(Info.Description.Trim());
            return string.Join(" · ", parts);
        }
    }

    public string Tip => Info.Modified is { } m ? $"{Name}\nModified {m.LocalDateTime:g}" : Name;
    public string AccessibleName => $"{Name}, {Detail}";
    public override string ToString() => AccessibleName;
}

/// <summary>A layer (or a label grouping layers) in the Layers pane.</summary>
public sealed class LayerNode(string label, LayerInfo? layer, List<LayerNode> children, bool visible, Action<LayerNode> toggled) : INotifyPropertyChanged
{
    private bool _visible = visible;

    public string Label { get; } = label;
    public LayerInfo? Layer { get; } = layer;
    public List<LayerNode> Children { get; } = children;
    public bool IsEnabled => Layer is { Locked: false };
    public string? Tip => Layer is { Locked: true } ? "The document locks this layer" : null;
    public Visibility CheckVisibility => Layer is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GroupVisibility => Layer is null ? Visibility.Visible : Visibility.Collapsed;

    public bool IsChecked
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            toggled(this);
        }
    }

    public override string ToString() => Label;
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A signature field and its verification result, in the Signatures pane.</summary>
public sealed class SignatureItem(SignatureInfo info, string? pageLabel, bool brokenByEdits = false)
{
    public SignatureInfo Info { get; } = info;
    public string Title => Info.Status == SignatureStatus.Unsigned ? $"{Info.FieldName} (not signed)" : Info.SignerName ?? Info.FieldName;
    public Visibility GoVisibility => Info.PageIndex >= 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SignVisibility => Info.Status == SignatureStatus.Unsigned ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>ok, warn, bad or none (drives the icon color).</summary>
    public string Severity => DocumentTab.SignatureSeverity(Info, brokenByEdits);

    public string Glyph => Severity switch { "ok" => "", "bad" => "", "none" => "", _ => "" };
    public string Status => DocumentTab.SignatureSummary(Info, brokenByEdits);

    public string Details
    {
        get
        {
            var lines = new List<string> { $"Field: {Info.FieldName}{(pageLabel is null ? " (invisible)" : $", page {pageLabel}")}" };
            if (Info.SigningTime is { } t) lines.Add($"Signed: {t.LocalDateTime:F}{(Info.HasTimestamp ? " (timestamped)" : " (the signer's clock)")}");
            if (Info.CertificationLevel is { } p) lines.Add(p switch
            {
                1 => "Certified: no changes allowed",
                2 => "Certified: filling in forms and signing allowed",
                _ => "Certified: filling in forms, signing and comments allowed",
            });
            if (!string.IsNullOrWhiteSpace(Info.Reason)) lines.Add($"Reason: {Info.Reason}");
            if (!string.IsNullOrWhiteSpace(Info.Location)) lines.Add($"Location: {Info.Location}");
            if (Info.SubFilter is { } f) lines.Add($"Format: {f}");
            if (Info.Status != SignatureStatus.Unsigned) lines.Add(Info.CoversWholeDocument ? "Covers the whole document" : "Doesn't cover the whole document (changed after signing)");
            if (Info.Certificate is { } c)
            {
                lines.Add($"Certificate: {c.Subject}");
                lines.Add($"Issued by: {c.Issuer}");
                lines.Add($"Valid: {c.NotBefore:d} to {c.NotAfter:d}{(Info.CertificateValidAtSigningTime == false ? " (not valid at signing time)" : "")}");
                lines.Add($"Trust: {(Info.CertificateTrusted ? "trusted by Windows" : Info.CertificateStatus)}");
                lines.Add($"Serial: {c.SerialNumber}");
                lines.Add($"SHA-1 thumbprint: {c.Thumbprint}");
            }
            if (!string.IsNullOrWhiteSpace(Info.Detail)) lines.Add(Info.Detail);
            return string.Join("\n", lines);
        }
    }
}

public enum ReviewPane { Comments, Attachments, Layers, Signatures }

/// <summary>Comments, attachments, layers, signatures, redaction, page labels and the document's opening preferences.</summary>
public partial class DocumentTab
{
    private int _reviewLoad;
    private bool _reviewLoadedOnce;
    private bool _hadViewState;
    private string[]? _labels;
    private byte[]? _reviewData;
    private IReadOnlyList<AnnotationInfo> _annotations = [];
    private List<CommentItem> _commentItems = [];
    private IReadOnlyList<AttachmentInfo> _attachments = [];
    private IReadOnlyList<LayerInfo> _layers = [];
    private IReadOnlyList<LayerOrderNode> _layerOrder = [];
    private readonly Dictionary<string, bool> _layerChoice = [];
    private int _layerTicket;
    private PdfEngine? _viewEngine;
    private IReadOnlyList<SignatureInfo> _signatures = [];
    private bool _signedOnOpen;
    private readonly Dictionary<string, SignatureInfo> _signaturesOnOpen = [];
    private bool _signatureBaselineStale; // reloaded from disk: the next verification is the new "on open" result
    private bool _signatureWarned;
    private bool _sigBarDismissed;
    private bool _xfaBarDismissed;
    private bool _displayDocTitle;
    private string? _docTitle;

    /// <summary>The engine pages are shown, printed and exported with: the document, or a copy with the chosen layers.</summary>
    public PdfEngine ViewEngine => _viewEngine ?? Session.Engine;

    /// <summary>The tab's caption: the document title when the file asks for it (DisplayDocTitle), else the file name.</summary>
    public string DisplayTitle => _docTitle ?? Session.DisplayName;

    /// <summary>The comment author: the name set in Settings, else the Windows user name.</summary>
    public static string Author => AppSettings.Current.AuthorName is { Length: > 0 } a ? a : Environment.UserName;

    public IReadOnlyList<AnnotationInfo> Comments => _annotations;
    public IReadOnlyList<AttachmentInfo> AttachedFiles => _attachments;
    public IReadOnlyList<SignatureInfo> SignatureResults => _signatures;
    public IReadOnlyList<LayerInfo> LayerList => _layers;
    public bool HasLayerOverride => _viewEngine is not null;

    // ───────────────────────────── Page labels ─────────────────────────────

    public bool HasPageLabels => _labels is not null;

    /// <summary>The page's label (e.g. "iv"), or its number when the document has no labels.</summary>
    public string PageLabel(int index) => _labels is { } l && index >= 0 && index < l.Length ? l[index] : (index + 1).ToString();

    /// <summary>The page a typed label or number refers to: a matching label first, then a page number.</summary>
    public int? FindPage(string text)
    {
        text = text.Trim();
        if (DocumentFeatures.FindPageByLabel(_labels, text) is { } byLabel) return byLabel;
        return int.TryParse(text, out var n) && n >= 1 && n <= Viewer.PageCount ? n - 1 : null;
    }

    // ───────────────────────────── Setup ─────────────────────────────

    private void InitReview()
    {
        CommentsRail.Click += (_, _) => TogglePane(NavPaneKind.Comments);
        AttachmentsRail.Click += (_, _) => TogglePane(NavPaneKind.Attachments);
        LayersRail.Click += (_, _) => TogglePane(NavPaneKind.Layers);
        SignaturesRail.Click += (_, _) => TogglePane(NavPaneKind.Signatures);
        CommentsRail.Visibility = AttachmentsRailHost.Visibility = Session.IsPdf ? Visibility.Visible : Visibility.Collapsed;

        CommentFilter.TextChanged += (_, _) => RefreshCommentList();
        CommentList.SelectionChanged += (_, _) =>
        {
            var sel = CommentList.SelectedItem as CommentItem;
            CommentEditButton.IsEnabled = CommentEditMenu.IsEnabled = sel is not null;
            CommentDeleteButton.IsEnabled = CommentDeleteMenu.IsEnabled = sel is not null;
            if (sel is not null && !_syncingComments) FlashComment(sel.Info);
        };
        CommentList.MouseDoubleClick += (_, e) =>
        {
            if (ItemData<CommentItem>(e.OriginalSource) is { } c) _ = EditComment(c.Info);
        };
        CommentList.KeyDown += (_, e) =>
        {
            if (CommentList.SelectedItem is not CommentItem c || Keyboard.Modifiers != ModifierKeys.None) return;
            if (e.Key == Key.Delete) _ = DeleteComment(c.Info);
            else if (e.Key == Key.Enter) _ = EditComment(c.Info);
            else return;
            e.Handled = true;
        };
        CommentEditButton.Click += (_, _) => { if (CommentList.SelectedItem is CommentItem c) _ = EditComment(c.Info); };
        CommentEditMenu.Click += (_, _) => { if (CommentList.SelectedItem is CommentItem c) _ = EditComment(c.Info); };
        CommentDeleteButton.Click += (_, _) => { if (CommentList.SelectedItem is CommentItem c) _ = DeleteComment(c.Info); };
        CommentDeleteMenu.Click += (_, _) => { if (CommentList.SelectedItem is CommentItem c) _ = DeleteComment(c.Info); };

        AttachmentList.SelectionChanged += (_, _) => UpdateAttachmentButtons();
        AttachmentList.MouseDoubleClick += (_, e) =>
        {
            if (ItemData<AttachmentItem>(e.OriginalSource) is not { } a) return;
            if (a.IsPdf) _ = OpenAttachment(a.Info);
            else _ = SaveAttachment(a.Info);
        };
        AttachmentList.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Delete || AttachmentList.SelectedItem is not AttachmentItem a) return;
            _ = RemoveAttachment(a.Info);
            e.Handled = true;
        };
        AttachSaveButton.Click += (_, _) => { if (AttachmentList.SelectedItem is AttachmentItem a) _ = SaveAttachment(a.Info); };
        AttachOpenButton.Click += (_, _) => { if (AttachmentList.SelectedItem is AttachmentItem a) _ = OpenAttachment(a.Info); };
        AttachRemoveButton.Click += (_, _) => { if (AttachmentList.SelectedItem is AttachmentItem a) _ = RemoveAttachment(a.Info); };
        AttachAddButton.Click += (_, _) => _ = AddAttachment();

        var layerItemStyle = new Style(typeof(TreeViewItem), LayerTree.TryFindResource(typeof(TreeViewItem)) as Style);
        layerItemStyle.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, true));
        layerItemStyle.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(LayerNode.Label))));
        LayerTree.ItemContainerStyle = layerItemStyle;
        LayersResetButton.Click += (_, _) => ResetLayers();
        LayersSaveButton.Click += (_, _) => _ = SaveLayersAsDefault();

        SigBarClose.Click += (_, _) =>
        {
            _sigBarDismissed = true;
            SigBar.Visibility = Visibility.Collapsed;
        };
        SigBarDetails.Click += (_, _) => ShowPane(NavPaneKind.Signatures);
        XfaBarClose.Click += (_, _) =>
        {
            _xfaBarDismissed = true;
            XfaBar.Visibility = Visibility.Collapsed;
        };

        RedactClear.Click += (_, _) => Viewer.ClearRedactions();
        RedactApply.Click += (_, _) => _ = ApplyRedactions();
        RedactDone.Click += (_, _) =>
        {
            if (Viewer.Tool == ViewerTool.Redact) Viewer.Tool = ViewerTool.Hand;
            UpdateRedactBar();
        };
        RedactHitsButton.Click += (_, _) => RedactSearchHits();
        Viewer.RedactionsChanged += (_, _) => UpdateRedactBar();
        Viewer.ToolChanged += (_, _) => UpdateRedactBar();
        Viewer.NoteRequested += (_, n) => _ = AddNoteAt(n.PageIndex, n.X, n.Y, null);
        Viewer.AnnotationActivated += (_, a) => _ = EditComment(a);

        CtxRedactText.Click += (_, _) => RedactSelection();
        Viewer.ContextMenuOpening += (_, _) =>
        {
            var markup = Session.IsPdf && Viewer.HasSelection;
            foreach (var item in new FrameworkElement[] { CtxHighlight, CtxUnderline, CtxStrikeOut, CtxRedactText, CtxMarkupSeparator })
                item.Visibility = markup ? Visibility.Visible : Visibility.Collapsed;
        };
        InitSigning();
        _ = LoadReviewAsync();
    }

    private static T? ItemData<T>(object source) where T : class
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem { DataContext: T t }) return t;
            if (d is ListBox) return null;
        }
        return null;
    }

    // ───────────────────────────── Loading ─────────────────────────────

    private sealed record ReviewInfo(
        string[]? Labels,
        IReadOnlyList<AnnotationInfo> Annotations,
        IReadOnlyList<AttachmentInfo> Attachments,
        IReadOnlyList<LayerInfo> Layers,
        IReadOnlyList<LayerOrderNode> LayerOrder,
        IReadOnlyList<SignatureInfo>? Signatures,
        (bool HasXfa, bool XfaOnly)? Xfa,
        ViewerPrefs? Prefs,
        string? Title,
        IReadOnlyList<TextStamp> SignatureStamps);

    /// <summary>Reads labels, comments, attachments, layers and signatures off the UI thread (the latest request wins).</summary>
    private async Task LoadReviewAsync()
    {
        if (!Session.IsPdf || _disposed) return;
        var ticket = ++_reviewLoad;
        var data = Session.Data;
        var pw = Session.Password;
        var first = !_reviewLoadedOnce;
        var wantTitle = first || _displayDocTitle;
        ReviewInfo info;
        try
        {
            info = await Task.Run(() => ReadReview(data, pw, first, wantTitle));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"Reading document features failed: {ex.Message}");
            return;
        }
        if (ticket != _reviewLoad || _disposed) return;
        _reviewLoadedOnce = true;
        _reviewData = data;

        _labels = info.Labels is { } l && l.Length == Viewer.PageCount ? l : null;
        foreach (var t in _thumbs) t.SetLabel(PageLabel(t.Index), Viewer.PageCount);

        _annotations = info.Annotations;
        Viewer.SetSignatureStamps(info.SignatureStamps);
        Viewer.SetAnnotations([.. _annotations.Where(a => !a.IsPdfDeskStamp)]);
        RefreshCommentList();

        _attachments = info.Attachments;
        RefreshAttachments();

        _layers = info.Layers;
        _layerOrder = info.LayerOrder;
        RefreshLayerTree();
        if (_layerChoice.Count > 0 && _viewEngine is null) _ = ApplyLayersAsync();

        if (info.Signatures is { } sigs)
        {
            ShowSignatures(sigs, first || _signatureBaselineStale);
            _signatureBaselineStale = false;
        }

        if (first)
        {
            if (info.Xfa is { XfaOnly: true } && !_xfaBarDismissed) XfaBar.Visibility = Visibility.Visible;
            if (info.Prefs is { } prefs)
            {
                _displayDocTitle = prefs.DisplayDocTitle;
                if (!_hadViewState) ApplyOpenPrefs(prefs);
            }
        }
        if (_displayDocTitle && wantTitle) _docTitle = string.IsNullOrWhiteSpace(info.Title) ? null : info.Title.Trim();
        RaiseState();
    }

    private static ReviewInfo ReadReview(byte[] data, string? pw, bool first, bool wantTitle)
    {
        T Try<T>(string what, Func<T> read, T fallback)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Write($"Reading {what} failed: {ex.Message}");
                return fallback;
            }
        }
        var labels = Try("page labels", () => DocumentFeatures.PageLabels(data, pw), null);
        var annots = Try<IReadOnlyList<AnnotationInfo>>("comments", () => Annotations.Read(data, pw), []);
        var files = Try<IReadOnlyList<AttachmentInfo>>("attachments", () => Attachments.List(data, pw), []);
        var layers = Try<IReadOnlyList<LayerInfo>>("layers", () => Layers.List(data, pw), []);
        var order = layers.Count > 0 ? Try<IReadOnlyList<LayerOrderNode>>("layer order", () => Layers.ReadOrder(data, pw), []) : [];
        var sigs = Try<IReadOnlyList<SignatureInfo>?>("signatures", () => Signatures.Verify(data, pw, checkRevocationOnline: true), null);
        var xfa = first ? Try<(bool, bool)?>("XFA", () => FormEditor.HasXfa(data, pw), null) : null;
        var prefs = first ? Try<ViewerPrefs?>("opening settings", () => DocumentFeatures.ReadViewerPrefs(data, pw), null) : null;
        var title = wantTitle ? Try<string?>("title", () => PageEditor.ReadMetadata(data, pw).Title, null) : null;
        var sigStamps = Try<IReadOnlyList<TextStamp>>("signature stamps", () => SignatureStamps.Read(data, pw), []);
        return new ReviewInfo(labels, annots, files, layers, order, sigs, xfa, prefs, title, sigStamps);
    }

    /// <summary>Honors the document's opening settings (only for files without a remembered position).</summary>
    private void ApplyOpenPrefs(ViewerPrefs prefs)
    {
        var pane = prefs.PageMode switch
        {
            PageModeKind.Outlines => NavPaneKind.Bookmarks,
            PageModeKind.Thumbs => NavPaneKind.Pages,
            PageModeKind.Attachments => NavPaneKind.Attachments,
            PageModeKind.Layers when _layers.Count > 0 => NavPaneKind.Layers,
            _ => (NavPaneKind?)null,
        };
        if (pane is { } p && !IsOrganizing) ShowPane(p);

        if (prefs.PageLayout is { } layout)
            Viewer.SetLayout(layout switch
            {
                PageLayoutKind.SinglePage => ViewLayout.SinglePage,
                PageLayoutKind.TwoColumnLeft or PageLayoutKind.TwoPageLeft => ViewLayout.Facing,
                PageLayoutKind.TwoColumnRight or PageLayoutKind.TwoPageRight => ViewLayout.FacingCover,
                _ => ViewLayout.Continuous,
            });

        // Only if the reader hasn't moved yet (the settings arrive a moment after the first pages).
        if (prefs.OpenAction is { } dest && dest.PageIndex >= 0 && dest.PageIndex < Viewer.PageCount && Viewer.CurrentPage == 0)
        {
            switch (dest.Kind)
            {
                case ZoomKind.Fit or ZoomKind.FitB:
                    Viewer.SetZoomMode(ZoomMode.FitPage);
                    break;
                case ZoomKind.FitH or ZoomKind.FitBH:
                    Viewer.SetZoomMode(ZoomMode.FitWidth);
                    break;
                case ZoomKind.XYZ when dest.Zoom is > 0 and < 64:
                    Viewer.SetZoom(dest.Zoom.Value);
                    break;
            }
            Viewer.GoToPage(dest.PageIndex, dest.Kind is ZoomKind.Fit or ZoomKind.FitB ? null : dest.Y, record: false);
        }
    }

    // ───────────────────────────── Comments ─────────────────────────────

    private bool _syncingComments;

    private void RefreshCommentList()
    {
        var byId = _annotations.Where(a => !a.IsPdfDeskStamp).ToDictionary(a => a.Id);
        var replies = byId.Values.Where(a => a.ReplyTo is not null && byId.ContainsKey(a.ReplyTo)).ToLookup(a => a.ReplyTo!);
        var items = new List<CommentItem>();
        void Add(AnnotationInfo a, int depth, HashSet<string> seen)
        {
            if (!seen.Add(a.Id)) return;
            items.Add(new CommentItem(a, $"Page {PageLabel(a.PageIndex)}", Math.Min(depth, 4)));
            foreach (var r in replies[a.Id].OrderBy(r => r.Modified ?? DateTimeOffset.MinValue)) Add(r, depth + 1, seen);
        }
        var seen = new HashSet<string>();
        foreach (var a in byId.Values.Where(a => a.ReplyTo is null || !byId.ContainsKey(a.ReplyTo)).OrderBy(a => a.PageIndex).ThenBy(a => a.Rect.Y).ThenBy(a => a.Rect.X))
            Add(a, 0, seen);
        _commentItems = items;

        var q = CommentFilter.Text.Trim();
        var shown = q.Length == 0 ? items : items.Where(c => $"{c.Title} {c.Info.Author} {c.Contents}".Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var selectedId = (CommentList.SelectedItem as CommentItem)?.Info.Id;
        var view = new ListCollectionView(shown);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CommentItem.PageHeader)));
        _syncingComments = true;
        CommentList.ItemsSource = view;
        if (selectedId is not null && shown.FirstOrDefault(c => c.Info.Id == selectedId) is { } again) CommentList.SelectedItem = again;
        _syncingComments = false;
        CommentsEmpty.Text = items.Count == 0
            ? "No comments yet. Select text and press Ctrl+Shift+H to highlight it, or add a note."
            : shown.Count == 0 ? "No comments match." : "";
        CommentsEmpty.Visibility = CommentsEmpty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePaneDetail();
    }

    private void FlashComment(AnnotationInfo a)
    {
        if (a.PageIndex < 0 || a.PageIndex >= Viewer.PageCount) return;
        var rect = a.QuadRects.Count > 0 ? a.QuadRects.Aggregate(default(RectD), (acc, r) => acc.Union(r)) : a.Rect;
        Viewer.Flash(a.PageIndex, rect);
    }

    /// <summary>True when the comments shown were read from the current bytes (their ids are valid for edits).</summary>
    private bool ReviewIsCurrent()
    {
        if (ReferenceEquals(_reviewData, Session.Data)) return true;
        Notify("Comments are still being refreshed after the last change. Try again in a moment.");
        return false;
    }

    public bool CanMarkup => Session.IsPdf && !IsOrganizing && Viewer.HasSelection;

    /// <summary>Highlights, underlines or strikes out the selected text (one undo step, even across pages).</summary>
    public async Task<bool> MarkupSelection(TextMarkupKind kind)
    {
        if (!CanMarkup) return false;
        if (Viewer.SelectedPageCount > 50)
        {
            Notify("Select at most 50 pages of text to mark it up.", error: true);
            return false;
        }
        var parts = Viewer.GetSelectionLineRects();
        if (parts.Count == 0)
        {
            Notify("The selection has no text to mark.");
            return false;
        }
        var color = kind switch
        {
            TextMarkupKind.Underline => new AnnotColor(0.1, 0.55, 0.2),
            TextMarkupKind.StrikeOut => AnnotColor.Red,
            TextMarkupKind.Squiggly => new AnnotColor(0.1, 0.45, 0.85),
            _ => AnnotColor.Yellow,
        };
        var author = Author;
        var label = kind switch { TextMarkupKind.Underline => "Underline text", TextMarkupKind.StrikeOut => "Strike out text", TextMarkupKind.Squiggly => "Squiggly underline", _ => "Highlight text" };
        var ok = await RunEdit(label, (d, pw) =>
        {
            foreach (var (page, rects) in parts) d = Annotations.AddTextMarkup(d, pw, page, kind, rects, color, null, author);
            return d;
        }, contentOnly: true);
        if (ok) Viewer.ClearSelection();
        return ok;
    }

    /// <summary>Asks for a note's text (unless given) and puts a sticky note with its top-left at (x, y).</summary>
    public async Task<bool> AddNoteAt(int page, double x, double y, string? text)
    {
        if (!Session.IsPdf || page < 0 || page >= Viewer.PageCount) return false;
        if (text is null)
        {
            if (Window.GetWindow(this) is not { } owner) return false;
            var dlg = new FormDialog(owner, "Add note", "Add");
            var box = NoteBox(dlg, "");
            dlg.AddNote($"Shown on page {PageLabel(page)} as a note icon; its text appears when you point at it. Author: {Author}.");
            dlg.Validate(() => box.Text.Trim().Length == 0 ? "Type the note's text." : null);
            if (!dlg.ShowModal()) return false;
            text = box.Text.Trim();
        }
        var g = Viewer.Engine!.GetPage(page);
        x = Math.Clamp(x, 0, Math.Max(0, g.Width - 20));
        y = Math.Clamp(y, 0, Math.Max(0, g.Height - 20));
        var author = Author;
        var note = text;
        var ok = await RunEdit("Add note", (d, pw) => Annotations.AddNote(d, pw, page, x, y, note, author), contentOnly: true);
        if (ok && Viewer.Tool == ViewerTool.AddNote) Viewer.Tool = ViewerTool.Hand; // one note per click, like other editors
        return ok;
    }

    private static TextBox NoteBox(FormDialog dlg, string text)
    {
        var box = dlg.AddText("Text", text);
        box.AcceptsReturn = true;
        box.TextWrapping = TextWrapping.Wrap;
        box.MinHeight = 110;
        box.MaxHeight = 320;
        box.VerticalContentAlignment = VerticalAlignment.Top;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        return box;
    }

    public async Task<bool> EditComment(AnnotationInfo a, string? newText = null)
    {
        if (a.IsPdfDeskStamp || !ReviewIsCurrent()) return false;
        if (newText is null)
        {
            if (Window.GetWindow(this) is not { } owner) return false;
            var dlg = new FormDialog(owner, a.Kind == AnnotationKind.Text ? "Edit note" : "Edit comment", "Save");
            var box = NoteBox(dlg, a.Contents);
            var who = string.Join(" · ", new[] { a.Author, a.Modified?.LocalDateTime.ToString("g") }.Where(s => !string.IsNullOrWhiteSpace(s)));
            dlg.AddNote($"Page {PageLabel(a.PageIndex)}{(who.Length > 0 ? $" · {who}" : "")}");
            if (!dlg.ShowModal()) return false;
            newText = box.Text.TrimEnd();
        }
        if (newText == a.Contents) return false;
        var text = newText;
        return await RunEdit("Edit comment", (d, pw) => Annotations.SetContents(d, pw, a.Id, text), contentOnly: true);
    }

    public async Task<bool> DeleteComment(AnnotationInfo a)
    {
        if (!ReviewIsCurrent()) return false;
        var replies = _annotations.Count(x => x.ReplyTo == a.Id);
        return await RunEdit(replies > 0 ? $"Delete comment and {replies} repl{(replies == 1 ? "y" : "ies")}" : "Delete comment",
            (d, pw) => Annotations.Delete(d, pw, a.Id), contentOnly: true);
    }

    // ───────────────────────────── Attachments ─────────────────────────────

    private void RefreshAttachments()
    {
        var selected = (AttachmentList.SelectedItem as AttachmentItem)?.Info.Key;
        var items = _attachments.Select(a => new AttachmentItem(a, a.PageIndex >= 0 ? PageLabel(a.PageIndex) : null)).ToList();
        AttachmentList.ItemsSource = items;
        if (items.FirstOrDefault(i => i.Info.Key == selected) is { } again) AttachmentList.SelectedItem = again;
        AttachmentsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AttachmentsBadge.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AttachmentsBadgeText.Text = items.Count > 99 ? "99+" : items.Count.ToString();
        AttachmentsRail.ToolTip = items.Count == 0 ? "Attachments" : $"Attachments ({items.Count} file{(items.Count == 1 ? "" : "s")})";
        System.Windows.Automation.AutomationProperties.SetName(AttachmentsRail, (string)AttachmentsRail.ToolTip);
        UpdateAttachmentButtons();
        UpdatePaneDetail();
    }

    private void UpdateAttachmentButtons()
    {
        var sel = AttachmentList.SelectedItem as AttachmentItem;
        AttachSaveButton.IsEnabled = AttachRemoveButton.IsEnabled = sel is not null;
        AttachOpenButton.IsEnabled = sel is { IsPdf: true };
    }

    /// <summary>Writes an attachment to a file the user picks. Attachments are never opened or run by PdfDesk itself.</summary>
    public async Task SaveAttachment(AttachmentInfo a, string? path = null)
    {
        if (!ReviewIsCurrent()) return;
        if (path is null && DevScript.TakeSavePath(out var scripted))
        {
            if (scripted is null) return;
            path = scripted;
        }
        if (path is null)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save attachment",
                FileName = SafeFileName(a.FileName),
                Filter = "All files (*.*)|*.*",
                InitialDirectory = AppSettings.Current.LastFolder is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null,
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            path = dlg.FileName;
        }
        try
        {
            var data = Session.Data;
            var pw = Session.Password;
            var target = path;
            await Task.Run(() => File.WriteAllBytes(target, Attachments.Extract(data, pw, a.Key)));
            Notify($"Saved {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Prompt.Error(Window.GetWindow(this), "Couldn't save the attachment", ex.Message);
        }
    }

    /// <summary>Opens an attached PDF in a new tab (an unsaved copy; nothing is written to disk).</summary>
    public async Task OpenAttachment(AttachmentInfo a)
    {
        if (!ReviewIsCurrent() || Window.GetWindow(this) is not MainWindow main) return;
        try
        {
            var data = Session.Data;
            var pw = Session.Password;
            var bytes = await Task.Run(() => Attachments.Extract(data, pw, a.Key));
            DocumentSession session;
            string? password = null;
            while (true)
            {
                try
                {
                    var p = password;
                    session = await Task.Run(() => DocumentSession.FromBytes(bytes, SafeFileName(a.FileName), p));
                    break;
                }
                catch (PasswordRequiredException ex)
                {
                    password = PdfFiles.PromptPassword(main, a.FileName, password is not null || ex.WrongPassword);
                    if (password is null) return;
                }
            }
            main.OpenSession(session);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Prompt.Error(Window.GetWindow(this), $"Couldn't open {a.FileName}", ex.Message);
        }
    }

    public async Task<bool> AddAttachment(string? path = null, string? description = null)
    {
        if (!Session.IsPdf) return false;
        if (path is null)
        {
            var dlg = new OpenFileDialog { Title = "Attach a file", Filter = "All files (*.*)|*.*" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return false;
            path = dlg.FileName;
        }
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 512L * 1024 * 1024) throw new IOException("Files over 512 MB can't be attached.");
            var source = path;
            bytes = await Task.Run(() => File.ReadAllBytes(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Prompt.Error(Window.GetWindow(this), "Couldn't read the file", ex.Message);
            return false;
        }
        var name = Path.GetFileName(path);
        var modified = File.GetLastWriteTime(path);
        var ok = await RunEdit($"Attach {name}", (d, pw) => Attachments.Add(d, pw, name, bytes, description, modified), contentOnly: true);
        if (ok) ShowPane(NavPaneKind.Attachments);
        return ok;
    }

    public async Task<bool> RemoveAttachment(AttachmentInfo a)
    {
        if (!ReviewIsCurrent()) return false;
        return await RunEdit($"Remove attachment {a.FileName}", (d, pw) => Attachments.Remove(d, pw, a.Key), contentOnly: true);
    }

    private static string SafeFileName(string name)
    {
        // Embedded names come from the file: no folders, no invalid or control characters, no "." / ".." or trailing dots.
        var clean = string.Concat(Path.GetFileName(name.Replace('/', '\\')).Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c))
            .Trim().TrimEnd('.', ' ');
        return clean.Length == 0 ? "attachment" : clean;
    }

    // ───────────────────────────── Layers ─────────────────────────────

    private void RefreshLayerTree()
    {
        LayersRail.Visibility = _layers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_layers.Count == 0 && _pane == NavPaneKind.Layers) ShowPane(NavPaneKind.Pages);
        var byId = _layers.ToDictionary(l => l.Id);
        var used = new HashSet<int>();
        List<LayerNode> Build(IEnumerable<LayerOrderNode> nodes) =>
        [
            .. nodes.Select(n =>
            {
                var layer = n.LayerId is { } id && byId.TryGetValue(id, out var l) ? l : null;
                if (layer is not null) used.Add(layer.Id);
                return new LayerNode(layer?.Name ?? n.Label ?? "", layer, Build(n.Children), layer is not null && IsLayerVisible(layer), OnLayerToggled);
            }).Where(n => n.Layer is not null || n.Children.Count > 0),
        ];
        var roots = Build(_layerOrder);
        // Layers the order tree leaves out still get a row.
        roots.AddRange(_layers.Where(l => !used.Contains(l.Id)).Select(l => new LayerNode(l.Name, l, [], IsLayerVisible(l), OnLayerToggled)));
        LayerTree.ItemsSource = roots;
        LayersResetButton.IsEnabled = LayersSaveButton.IsEnabled = _layerChoice.Count > 0;
        UpdatePaneDetail();
    }

    private bool IsLayerVisible(LayerInfo l) => _layerChoice.TryGetValue(l.Name, out var on) ? on : l.DefaultVisible;

    private void OnLayerToggled(LayerNode node)
    {
        if (node.Layer is not { } layer) return;
        // Remembered by name: layer ids are object numbers, which change when the file is rewritten by an edit.
        if (node.IsChecked == layer.DefaultVisible) _layerChoice.Remove(layer.Name);
        else _layerChoice[layer.Name] = node.IsChecked;
        LayersResetButton.IsEnabled = LayersSaveButton.IsEnabled = _layerChoice.Count > 0;
        _ = ApplyLayersAsync();
    }

    /// <summary>Sets a layer's visibility for viewing (by name); used by the pane and the dev script.</summary>
    public void SetLayerVisible(string name, bool visible)
    {
        if (_layers.FirstOrDefault(l => l.Name == name) is not { Locked: false } layer) return;
        if (visible == layer.DefaultVisible) _layerChoice.Remove(name);
        else _layerChoice[name] = visible;
        RefreshLayerTree();
        _ = ApplyLayersAsync();
    }

    private void ResetLayers()
    {
        _layerChoice.Clear();
        RefreshLayerTree();
        _ = ApplyLayersAsync();
    }

    /// <summary>
    /// Shows the document with the chosen layers through a separate view engine (a copy with that default layer
    /// configuration); the document's own bytes and undo history are untouched. Printing and exporting use the same engine.
    /// </summary>
    private async Task ApplyLayersAsync()
    {
        var ticket = ++_layerTicket;
        var visibility = _layers.Where(l => _layerChoice.TryGetValue(l.Name, out var on) && on != l.DefaultVisible)
            .ToDictionary(l => l.Id, l => _layerChoice[l.Name]);
        PdfEngine? engine = null;
        var data = Session.Data;
        if (visibility.Count > 0)
        {
            var pw = Session.Password;
            try
            {
                engine = await Task.Run(() =>
                {
                    var bytes = Layers.WithVisibility(data, pw, visibility);
                    try
                    {
                        return PdfEngine.Open(bytes, MuPDFCore.InputFileTypes.PDF, pw);
                    }
                    catch (PasswordRequiredException)
                    {
                        return PdfEngine.Open(bytes, MuPDFCore.InputFileTypes.PDF, null);
                    }
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Write($"Showing layers failed: {ex}");
                if (ticket == _layerTicket) Notify($"Couldn't change the layers: {ex.Message}", error: true);
                return;
            }
        }
        if (ticket != _layerTicket || _disposed || !ReferenceEquals(data, Session.Data) || engine is not null && engine.PageCount != Session.Engine.PageCount)
        {
            engine?.Dispose();
            return;
        }
        SetViewEngine(engine);
    }

    private void SetViewEngine(PdfEngine? engine)
    {
        var old = _viewEngine;
        if (old is null && engine is null) return;
        _viewEngine = engine;
        Viewer.SetEngine(ViewEngine, _scheduler, keepPosition: true, contentOnly: true);
        RebuildThumbs();
        if (IsOrganizing) Organizer.Load(Session.Engine, Organizer.SelectedPages, Viewer.CurrentPage);
        old?.Dispose();
        RaiseState();
    }

    /// <summary>The document changed: the layer view (made from the old bytes) is stale; it is rebuilt once the layers are re-read.</summary>
    private void DropViewEngine()
    {
        _layerTicket++;
        if (_viewEngine is null) return;
        var old = _viewEngine;
        _viewEngine = null;
        old.Dispose();
    }

    private async Task SaveLayersAsDefault()
    {
        if (!ReviewIsCurrent() || _layerChoice.Count == 0) return;
        var visibility = _layers.Where(l => _layerChoice.ContainsKey(l.Name)).ToDictionary(l => l.Id, l => _layerChoice[l.Name]);
        var choice = new Dictionary<string, bool>(_layerChoice);
        _layerChoice.Clear(); // the saved defaults are what shows now
        if (!await RunEdit("Save layer visibility", (d, pw) => Layers.SetDefaultVisibility(d, pw, visibility)))
        {
            foreach (var (k, v) in choice) _layerChoice[k] = v;
            return;
        }
        RefreshLayerTree();
    }

    // ───────────────────────────── Signatures and XFA ─────────────────────────────

    public bool IsSigned => _signatures.Any(s => s.Status != SignatureStatus.Unsigned);

    /// <summary>
    /// The signature verified when the document was opened (or reloaded) but no longer does after this tab's edits.
    /// That's the expected effect of editing a signed file, not tampering, so it is reported as such.
    /// </summary>
    internal bool BrokenByEdits(SignatureInfo s) =>
        s.Status is SignatureStatus.Invalid or SignatureStatus.DisallowedChanges
        && _signaturesOnOpen.TryGetValue(s.FieldName, out var before)
        && before.Status is SignatureStatus.Valid or SignatureStatus.ModifiedAfterSigning;

    /// <summary>Valid when opened and still intact: this tab's edits were appended after the signed bytes.</summary>
    private bool AppendedByEdits(SignatureInfo s) =>
        s.Status == SignatureStatus.ModifiedAfterSigning
        && _signaturesOnOpen.TryGetValue(s.FieldName, out var before) && before.Status == SignatureStatus.Valid;

    internal static string SignatureSeverity(SignatureInfo s, bool brokenByEdits = false) => brokenByEdits ? "warn" : s.Status switch
    {
        SignatureStatus.Unsigned => "none",
        SignatureStatus.Invalid or SignatureStatus.DisallowedChanges => "bad",
        SignatureStatus.Valid when s.CertificateTrusted => "ok",
        _ => "warn",
    };

    internal static string SignatureSummary(SignatureInfo s, bool brokenByEdits = false) => brokenByEdits
        ? "Valid when opened. Your edits changed the signed document, so this signature no longer verifies. Undo them, or keep the signed original by saving your changes as a copy."
        : s.Status switch
    {
        SignatureStatus.Unsigned => "Empty signature field.",
        SignatureStatus.Invalid => "Invalid: the signed content was altered, or the signature is damaged.",
        SignatureStatus.DisallowedChanges => "Invalid: the signed bytes are intact, but the document was later changed in ways this signature doesn't allow, so what you see may not be what was signed.",
        SignatureStatus.Unsupported => "Not checked: PdfDesk can't verify this kind of signature.",
        SignatureStatus.ModifiedAfterSigning => s.CertificateTrusted
            ? "The signature is valid, but the document was modified after signing."
            : "The signature is intact, but the document was modified after signing and the certificate isn't trusted on this PC.",
        _ when !s.CertificateTrusted => $"The signature is intact, but the certificate isn't trusted on this PC ({s.CertificateStatus}).",
        _ => "Valid: the document hasn't changed since it was signed.",
    };

    private string ShortStatus(SignatureInfo s) => BrokenByEdits(s) ? "valid when opened, invalidated by your edits" : s.Status switch
    {
        SignatureStatus.ModifiedAfterSigning when AppendedByEdits(s) => "intact; your changes were added after signing",
        SignatureStatus.Invalid => "invalid",
        SignatureStatus.DisallowedChanges => "invalid: changed in ways the signature doesn't allow",
        SignatureStatus.Unsupported => "can't be checked",
        SignatureStatus.ModifiedAfterSigning => "modified after signing",
        _ when !s.CertificateTrusted => "certificate not trusted",
        _ => "valid",
    };

    private void ShowSignatures(IReadOnlyList<SignatureInfo> sigs, bool onOpen)
    {
        // A signature our edits broke may no longer parse: keep naming its signer and certificate as they were.
        sigs = [.. sigs.Select(s => !onOpen && BrokenByEdits(s) && _signaturesOnOpen[s.FieldName] is var o
            ? s.Certificate is null
                ? s with { SignerName = s.SignerName ?? o.SignerName, SigningTime = s.SigningTime ?? o.SigningTime, Certificate = o.Certificate, CertificateTrusted = o.CertificateTrusted, CertificateStatus = o.CertificateStatus }
                : s with { SignerName = s.SignerName ?? o.SignerName, SigningTime = s.SigningTime ?? o.SigningTime }
            : s)];
        _signatures = sigs;
        if (onOpen)
        {
            _signedOnOpen = IsSigned;
            _signaturesOnOpen.Clear();
            foreach (var s in sigs) _signaturesOnOpen[s.FieldName] = s;
        }
        SignaturesRail.Visibility = sigs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SignatureList.ItemsSource = sigs.Select(s => new SignatureItem(s, s.PageIndex >= 0 ? PageLabel(s.PageIndex) : null, BrokenByEdits(s))).ToList();
        if (sigs.Count == 0 && _pane == NavPaneKind.Signatures) ShowPane(NavPaneKind.Pages);

        var signed = sigs.Where(s => s.Status != SignatureStatus.Unsigned).ToList();
        if (signed.Count == 0)
        {
            SigBar.Visibility = Visibility.Collapsed;
            UpdatePaneDetail();
            return;
        }
        string text;
        if (signed.Count == 1)
        {
            var s = signed[0];
            text = $"Signed by {s.SignerName ?? s.FieldName} — {ShortStatus(s)}.";
        }
        else if (signed.All(BrokenByEdits))
        {
            text = $"{signed.Count} signatures were valid when opened; your edits invalidated them.";
        }
        else
        {
            var problems = signed.Count(s => SignatureSeverity(s, BrokenByEdits(s)) != "ok");
            text = problems == 0
                ? $"{signed.Count} signatures, all valid."
                : $"{signed.Count} signatures; {problems} {(problems == 1 ? "has a problem" : "have problems")}: {string.Join("; ", signed.Where(s => SignatureSeverity(s, BrokenByEdits(s)) != "ok").Take(3).Select(s => $"{s.SignerName ?? s.FieldName} — {ShortStatus(s)}"))}.";
        }
        var severities = signed.Select(s => SignatureSeverity(s, BrokenByEdits(s))).ToList();
        var worst = severities.Contains("bad") ? "bad" : severities.Contains("warn") ? "warn" : "ok";
        SigBarText.Text = text;
        SigBarIcon.SetResourceReference(TextBlock.ForegroundProperty,
            worst switch { "ok" => "SystemFillColorSuccessBrush", "bad" => "SystemFillColorCriticalBrush", _ => "SystemFillColorCautionBrush" });
        System.Windows.Automation.AutomationProperties.SetName(SigBar, text);
        SigBar.Visibility = _sigBarDismissed ? Visibility.Collapsed : Visibility.Visible;
        UpdatePaneDetail();
    }

    private void OnGoToSignature(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SignatureItem s } && s.Info.PageIndex >= 0) Viewer.Flash(s.Info.PageIndex, s.Info.Rect);
    }

    /// <summary>
    /// Edits of a signed document are appended after the signed bytes (incremental update), which keeps the signatures
    /// valid. Only an encrypted signed document is rewritten (redaction too, which warns on its own).
    /// </summary>
    private bool NeedsSignedWarning => _signedOnOpen && !_signatureWarned && Session.Engine.IsEncrypted;

    /// <summary>Before the first edit of an encrypted signed document: say that editing breaks the signatures.</summary>
    private bool ConfirmSignedEdit()
    {
        if (!NeedsSignedWarning) return true;
        if (!Prompt.Confirm(Window.GetWindow(this), "This document is digitally signed",
                "Any change invalidates its signatures: they will no longer verify. To keep the signed original, save your changes as a copy (Save As).",
                "Edit anyway", warning: true)) return false;
        _signatureWarned = true;
        return true;
    }

    /// <summary>Before an edit that would invalidate signatures that are intact now.</summary>
    private bool ConfirmBreakingEdit(string label, IReadOnlyList<SignatureInfo> broken)
    {
        var who = broken.Count == 1 ? $"the signature of {broken[0].SignerName ?? broken[0].FieldName}" : $"{broken.Count} signatures";
        return Prompt.Confirm(Window.GetWindow(this), $"{label} invalidates {who}",
            "A signature allows filling in forms, comments and more signatures afterwards, but not this change: it alters what was signed, " +
            "or it can't be added without rewriting the file. Afterwards the signature shows as invalid.\n\n" +
            "To keep the signed original, undo the change, or save your changes as a copy (Save As).",
            $"{label} anyway", warning: true);
    }

    // ───────────────────────────── Redaction ─────────────────────────────

    private void UpdateRedactBar()
    {
        var marks = Viewer.Redactions;
        var tool = Viewer.Tool == ViewerTool.Redact;
        RedactBar.Visibility = tool || marks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var pages = marks.Select(m => m.PageIndex).Distinct().Count();
        RedactBarText.Text = marks.Count == 0
            ? "Drag over the areas to black out. Nothing is removed until you apply the redactions."
            : $"{marks.Count} area{(marks.Count == 1 ? "" : "s")} on {pages} page{(pages == 1 ? "" : "s")} marked for redaction. Click a mark with the redaction tool to remove it.";
        RedactApply.IsEnabled = RedactClear.IsEnabled = marks.Count > 0;
        RedactHitsButton.Visibility = _results.Count > 0 && Session.IsPdf ? Visibility.Visible : Visibility.Collapsed;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks the selected text's lines for redaction.</summary>
    public int RedactSelection()
    {
        if (!Session.IsPdf || !Viewer.HasSelection) return 0;
        var parts = Viewer.GetSelectionLineRects();
        var marks = parts.SelectMany(p => p.Rects.Select(r => new RedactMark(p.Page, r.Inflate(0.5)))).ToList();
        Viewer.AddRedactions(marks);
        Viewer.ClearSelection();
        return marks.Count;
    }

    /// <summary>Search &amp; redact: marks every hit of the current search.</summary>
    public int RedactSearchHits()
    {
        if (!Session.IsPdf) return 0;
        var marks = _results.SelectMany(h => h.Rects.Select(r => new RedactMark(h.Page, r.Inflate(0.5)))).ToList();
        Viewer.AddRedactions(marks);
        if (marks.Count > 0) Notify($"Marked {_results.Count} match{(_results.Count == 1 ? "" : "es")} for redaction. Check the marks, then apply them.");
        return marks.Count;
    }

    public bool HasSearchResults => _results.Count > 0;

    public void MarkRedaction(int page, RectD rect) => Viewer.AddRedactions([new RedactMark(page, rect)]);

    /// <summary>Confirms, then blacks out every marked area for good (the affected pages become images).</summary>
    public async Task<bool> ApplyRedactions(bool confirm = true)
    {
        var marks = Viewer.Redactions.ToList();
        if (marks.Count == 0 || !Session.IsPdf) return false;
        var pages = marks.Select(m => m.PageIndex).Distinct().Order().ToList();
        var list = string.Join(", ", pages.Take(12).Select(PageLabel)) + (pages.Count > 12 ? "…" : "");
        if (confirm && !Prompt.Confirm(Window.GetWindow(this), $"Apply {marks.Count} redaction{(marks.Count == 1 ? "" : "s")}?",
                $"The marked areas are blacked out and their content removed. {(pages.Count == 1 ? "Page" : "Pages")} {list} become images: their text can no longer be selected, searched or copied, and their links, comments and form fields are flattened into the image.{(_signedOnOpen ? "\n\nThis document is digitally signed: redaction rewrites the file, so its signatures will no longer verify." : "")}\n\nYou can undo this until you save. Once saved, the removed content can't be recovered.",
                "Apply redactions", warning: true)) return false;
        var areas = marks.GroupBy(m => m.PageIndex).ToDictionary(g => g.Key, g => (IReadOnlyList<RectD>)[.. g.Select(m => m.Rect)]);
        var ocr = PageOcr.Engine;
        var recognized = 0;
        var ocrSkipped = false;
        var ok = await RunEdit($"Redact {marks.Count} area{(marks.Count == 1 ? "" : "s")}", (d, pw) =>
        {
            var redacted = Redaction.Apply(d, pw, areas);
            if (ocr is null) return redacted;
            // The redacted pages are images now: recognize what's left on them and lay it back as invisible text, so they
            // stay searchable and selectable. Part of the same edit (one undo step). Cancel skips only this part.
            var progress = Dispatcher.Invoke(() => new ProgressPrompt(Window.GetWindow(this), "Applying redactions", "Recognizing text…"));
            try
            {
                var (words, _) = RecognizePages(ocr, redacted, pw, pages, progress, "Recognizing the text left on redacted pages", areas);
                recognized = words.Values.Sum(w => w.Count);
                return recognized == 0 ? redacted : TextLayer.AddInvisibleText(redacted, pw, words);
            }
            catch (OperationCanceledException)
            {
                ocrSkipped = true;
                return redacted;
            }
            finally
            {
                Dispatcher.Invoke(progress.Dispose);
            }
        }, signaturesWarned: confirm);
        if (ok)
        {
            Viewer.ClearRedactions();
            if (Viewer.Tool == ViewerTool.Redact) Viewer.Tool = ViewerTool.Hand;
            if (ocr is null) Notify("The redacted pages are images now and their text can't be searched: no text recognition (OCR) language is installed in Windows.");
            else if (ocrSkipped) Notify("Redacted. Text recognition was cancelled, so the redacted pages can't be searched; use Recognize Text (OCR) to add it later.");
        }
        return ok;
    }

    /// <summary>
    /// Recognize Text (OCR): for scanned pages (no text), recognizes the words and adds them as an invisible text layer,
    /// so the pages become searchable and selectable while looking the same. One undo step; Cancel changes nothing.
    /// </summary>
    public async Task<bool> RecognizeText()
    {
        if (!Session.IsPdf || IsBusy || Window.GetWindow(this) is not { } owner) return false;
        if (PageOcr.Engine is not { } ocr)
        {
            Prompt.Info(owner, "Text recognition isn't available", PageOcr.Unavailable);
            return false;
        }
        var count = Viewer.PageCount;
        var current = Viewer.CurrentPage;
        var dlg = new FormDialog(owner, "Recognize Text (OCR)", "Recognize", 460);
        var which = dlg.AddChoice("Pages", ["All pages without text", $"Current page ({PageLabel(current)})", "Page range"]);
        var range = dlg.Add("Page range", new TextBox { Text = Core.PageRange.Format(SelectedPages.Length > 1 ? SelectedPages : [current]) });
        range.GotKeyboardFocus += (_, _) => which.SelectedIndex = 2;
        dlg.AddNote($"Pages that already have text are skipped. The pages look the same afterwards; their words can be searched, selected and copied. Recognition runs on this PC with Windows' {ocr.RecognizerLanguage.DisplayName} text recognition, so check important text.");
        int[] pages = [];
        dlg.Validate(() =>
        {
            switch (which.SelectedIndex)
            {
                case 0:
                    pages = [.. Enumerable.Range(0, count)];
                    return null;
                case 1:
                    pages = [current];
                    return null;
                default:
                    return Core.PageRange.TryParse(range.Text, count, out pages, out var err) ? null : err;
            }
        });
        if (!dlg.ShowModal()) return false;

        var found = 0;
        var pagesWithWords = 0;
        var hadText = 0;
        var chosen = pages;
        var outcome = await TryEdit("Recognize text", (d, pw) =>
        {
            var progress = Dispatcher.Invoke(() => new ProgressPrompt(owner, "Recognizing text", "Starting…"));
            try
            {
                var (words, skipped) = RecognizePages(ocr, d, pw, chosen, progress, "Recognizing text");
                hadText = skipped;
                pagesWithWords = words.Count;
                found = words.Values.Sum(w => w.Count);
                return found == 0 ? d : TextLayer.AddInvisibleText(d, pw, words);
            }
            finally
            {
                Dispatcher.Invoke(progress.Dispose);
            }
        }, quiet: true);
        switch (outcome)
        {
            case EditOutcome.Done:
                Notify($"Recognized {found} word{(found == 1 ? "" : "s")} on {pagesWithWords} page{(pagesWithWords == 1 ? "" : "s")}{(hadText > 0 ? $" ({hadText} already had text)" : "")}. They can be searched and selected now.",
                    "Undo", () => { if (!IsBusy && Session.UndoLabel == "Recognize text") Undo(); });
                return true;
            case EditOutcome.NoChange:
                Notify(hadText == chosen.Length
                    ? (chosen.Length == 1 ? "This page already has text: nothing to recognize." : "These pages already have text: nothing to recognize.")
                    : "No text was recognized on these pages.");
                return false;
            case EditOutcome.Declined:
                Notify("Text recognition cancelled. Nothing was changed.");
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// OCR of <paramref name="pages"/> in <paramref name="data"/> (runs on a worker thread). Pages that already have text are
    /// skipped; words mostly inside a <paramref name="exclude"/> area (blacked-out redactions) are dropped.
    /// </summary>
    /// <exception cref="OperationCanceledException">The user pressed Cancel in <paramref name="progress"/>.</exception>
    internal static (Dictionary<int, IReadOnlyList<OcrWord>> Words, int HadText) RecognizePages(Windows.Media.Ocr.OcrEngine ocr, byte[] data, string? pw, IReadOnlyList<int> pages,
        ProgressPrompt progress, string title, IReadOnlyDictionary<int, IReadOnlyList<RectD>>? exclude = null)
    {
        var result = new Dictionary<int, IReadOnlyList<OcrWord>>();
        var hadText = 0;
        using var engine = PdfEngine.Open(data, MuPDFCore.InputFileTypes.PDF, pw);
        for (var i = 0; i < pages.Count; i++)
        {
            progress.Token.ThrowIfCancellationRequested();
            progress.Report($"{title}: page {i + 1} of {pages.Count}…", (double)i / pages.Count);
            var page = pages[i];
            if (page < 0 || page >= engine.PageCount) continue;
            if (TextLayer.HasText(engine, page))
            {
                hadText++;
                continue;
            }
            var words = PageOcr.Recognize(ocr, engine, page, progress.Token);
            if (exclude is not null && exclude.TryGetValue(page, out var blacked))
                words.RemoveAll(w => blacked.Any(b => Overlap(w.Rect, b) > 0.3 * w.Rect.Width * w.Rect.Height));
            if (words.Count > 0) result[page] = words;
        }
        return (result, hadText);

        static double Overlap(RectD a, RectD b) =>
            Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
    }
}
