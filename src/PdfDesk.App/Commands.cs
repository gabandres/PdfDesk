using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PdfDesk.App;

/// <summary>App commands. Gestures here are window-wide, so none are bare letters (those would eat typing in text boxes).</summary>
public static class Cmd
{
    private static RoutedUICommand Make(string text, string name, params InputGesture[] gestures) =>
        new(text, name, typeof(Cmd), new InputGestureCollection(gestures));

    private static KeyGesture K(Key key, ModifierKeys mods = ModifierKeys.None, string? display = null) =>
        display is null ? new KeyGesture(key, mods) : new KeyGesture(key, mods, display);

    /// <summary>The command's first shortcut as users see it ("Ctrl+S"), or null when it has none.</summary>
    public static string? GestureText(ICommand? command) =>
        (command as RoutedCommand)?.InputGestures.OfType<KeyGesture>().FirstOrDefault()?.GetDisplayStringForCulture(CultureInfo.CurrentCulture);

    /// <summary>"<paramref name="text"/> (Ctrl+S)": a tooltip naming the command's current shortcut, if it has one.</summary>
    public static string Tip(string text, ICommand? command) => GestureText(command) is { } g ? $"{text} ({g})" : text;

    /// <summary>The close button on a title-bar tab (in a template, so <see cref="AddShortcutsToTips"/> doesn't reach it).</summary>
    public static string CloseTabTip => Tip("Close", CloseDoc);

    /// <summary>
    /// Adds each command button's shortcut to its tooltip, read from the command's gestures, so tooltips never drift from
    /// the bindings. Call once after InitializeComponent; tooltips should name the action only.
    /// </summary>
    public static void AddShortcutsToTips(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is ButtonBase { ToolTip: string tip } b && GestureText(b.Command) is { } g && !tip.EndsWith($"({g})", StringComparison.Ordinal))
                b.ToolTip = $"{tip} ({g})";
            AddShortcutsToTips(child);
        }
    }

    // File
    public static readonly RoutedUICommand Open = Make("_Open…", nameof(Open), K(Key.O, ModifierKeys.Control));
    public static readonly RoutedUICommand Combine = Make("_Combine Files into PDF…", nameof(Combine));
    public static readonly RoutedUICommand Save = Make("_Save", nameof(Save), K(Key.S, ModifierKeys.Control));
    public static readonly RoutedUICommand SaveAs = Make("Save _As…", nameof(SaveAs), K(Key.S, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand CloseDoc = Make("_Close", nameof(CloseDoc), K(Key.W, ModifierKeys.Control), K(Key.F4, ModifierKeys.Control));
    public static readonly RoutedUICommand CloseAll = Make("Close A_ll", nameof(CloseAll), K(Key.W, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand Print = Make("_Print…", nameof(Print), K(Key.P, ModifierKeys.Control));
    public static readonly RoutedUICommand Scan = Make("Sca_n…", nameof(Scan));
    public static readonly RoutedUICommand ExportImages = Make("Export Pages as _Images…", nameof(ExportImages));
    public static readonly RoutedUICommand Properties = Make("Document P_roperties…", nameof(Properties), K(Key.D, ModifierKeys.Control));
    public static readonly RoutedUICommand ShowInFolder = Make("Show in _Folder", nameof(ShowInFolder));
    public static readonly RoutedUICommand Exit = Make("E_xit", nameof(Exit));

    // Edit
    public static readonly RoutedUICommand Undo = Make("_Undo", nameof(Undo), K(Key.Z, ModifierKeys.Control));
    public static readonly RoutedUICommand Redo = Make("_Redo", nameof(Redo), K(Key.Y, ModifierKeys.Control), K(Key.Z, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand Copy = Make("_Copy", nameof(Copy), K(Key.C, ModifierKeys.Control));
    public static readonly RoutedUICommand SelectAll = Make("Select _All Text", nameof(SelectAll), K(Key.A, ModifierKeys.Control));
    public static readonly RoutedUICommand Find = Make("_Find…", nameof(Find), K(Key.F, ModifierKeys.Control));
    public static readonly RoutedUICommand FindNext = Make("Find _Next", nameof(FindNext), K(Key.F3));
    public static readonly RoutedUICommand FindPrevious = Make("Find Pre_vious", nameof(FindPrevious), K(Key.F3, ModifierKeys.Shift));
    public static readonly RoutedUICommand SearchPane = Make("_Search Pane", nameof(SearchPane), K(Key.F, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand ToolHand = Make("_Hand Tool", nameof(ToolHand));
    public static readonly RoutedUICommand ToolSelect = Make("Select _Text Tool", nameof(ToolSelect));
    public static readonly RoutedUICommand ToolSnapshot = Make("S_napshot Tool", nameof(ToolSnapshot));
    public static readonly RoutedUICommand ToolAddText = Make("Add Te_xt (Fill and Sign)", nameof(ToolAddText));
    public static readonly RoutedUICommand ToolCheck = Make("Add Chec_k Mark", nameof(ToolCheck));
    public static readonly RoutedUICommand ToolCross = Make("Add C_ross", nameof(ToolCross));
    public static readonly RoutedUICommand AddSignature = Make("Add _Signature or Initials…", nameof(AddSignature));
    public static readonly RoutedUICommand HighlightFields = Make("_Highlight Form Fields", nameof(HighlightFields));

    // View
    public static readonly RoutedUICommand ZoomIn = Make("Zoom _In", nameof(ZoomIn), K(Key.OemPlus, ModifierKeys.Control, "Ctrl+="), K(Key.Add, ModifierKeys.Control));
    public static readonly RoutedUICommand ZoomOut = Make("Zoom _Out", nameof(ZoomOut), K(Key.OemMinus, ModifierKeys.Control, "Ctrl+-"), K(Key.Subtract, ModifierKeys.Control));
    public static readonly RoutedUICommand ActualSize = Make("_Actual Size", nameof(ActualSize), K(Key.D1, ModifierKeys.Control));
    public static readonly RoutedUICommand FitPage = Make("Fit _Page", nameof(FitPage), K(Key.D0, ModifierKeys.Control));
    public static readonly RoutedUICommand FitWidth = Make("Fit _Width", nameof(FitWidth), K(Key.D2, ModifierKeys.Control));
    public static readonly RoutedUICommand LayoutSingle = Make("_Single Page", nameof(LayoutSingle), K(Key.D4, ModifierKeys.Control));
    public static readonly RoutedUICommand LayoutContinuous = Make("_Continuous", nameof(LayoutContinuous), K(Key.D5, ModifierKeys.Control));
    public static readonly RoutedUICommand LayoutFacing = Make("_Two Pages", nameof(LayoutFacing), K(Key.D6, ModifierKeys.Control));
    public static readonly RoutedUICommand LayoutCover = Make("Two Pages with Cover", nameof(LayoutCover), K(Key.D7, ModifierKeys.Control));
    public static readonly RoutedUICommand RotateViewRight = Make("Rotate _View Clockwise", nameof(RotateViewRight), K(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift++"), K(Key.Add, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand RotateViewLeft = Make("Rotate View Countercloc_kwise", nameof(RotateViewLeft), K(Key.OemMinus, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+-"), K(Key.Subtract, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand NavPane = Make("_Navigation Pane", nameof(NavPane), K(Key.F4));
    public static readonly RoutedUICommand FullScreen = Make("_Full Screen", nameof(FullScreen), K(Key.F11), K(Key.L, ModifierKeys.Control));
    public static readonly RoutedUICommand FirstPage = Make("_First Page", nameof(FirstPage), K(Key.Home, ModifierKeys.Control));
    public static readonly RoutedUICommand PrevPage = Make("_Previous Page", nameof(PrevPage));
    public static readonly RoutedUICommand NextPage = Make("_Next Page", nameof(NextPage));
    public static readonly RoutedUICommand LastPage = Make("_Last Page", nameof(LastPage), K(Key.End, ModifierKeys.Control));
    public static readonly RoutedUICommand GoToPage = Make("_Go to Page…", nameof(GoToPage), K(Key.G, ModifierKeys.Control), K(Key.N, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand Back = Make("Previous _View", nameof(Back), K(Key.Left, ModifierKeys.Alt));
    public static readonly RoutedUICommand Forward = Make("Next Vie_w", nameof(Forward), K(Key.Right, ModifierKeys.Alt));

    // Tabs (handled before the focused control sees the keys, so the page view or a list can't take them)
    public static readonly RoutedUICommand NextTab = Make("Ne_xt Tab", nameof(NextTab),
        K(Key.Tab, ModifierKeys.Control, "Ctrl+Tab"), K(Key.PageDown, ModifierKeys.Control, "Ctrl+PgDn"));
    public static readonly RoutedUICommand PrevTab = Make("Pre_vious Tab", nameof(PrevTab),
        K(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+Tab"), K(Key.PageUp, ModifierKeys.Control, "Ctrl+PgUp"));
    public static readonly RoutedUICommand ReopenClosed = Make("Reopen Closed _Tab", nameof(ReopenClosed), K(Key.T, ModifierKeys.Control | ModifierKeys.Shift));

    // App
    public static readonly RoutedUICommand CommandPalette = Make("Command _Palette…", nameof(CommandPalette), K(Key.K, ModifierKeys.Control), K(Key.P, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand Settings = Make("_Settings…", nameof(Settings), K(Key.OemComma, ModifierKeys.Control, "Ctrl+,"));
    public static readonly RoutedUICommand Shortcuts = Make("_Keyboard Shortcuts", nameof(Shortcuts), K(Key.F1));
    public static readonly RoutedUICommand DarkPages = Make("_Dark Pages (Night Reading)", nameof(DarkPages));
    public static readonly RoutedUICommand ToggleDarkMode = Make("Toggle Dar_k Mode", nameof(ToggleDarkMode), K(Key.L, ModifierKeys.Control | ModifierKeys.Shift));

    // Organize
    public static readonly RoutedUICommand Organize = Make("_Organize Pages", nameof(Organize), K(Key.O, ModifierKeys.Control | ModifierKeys.Shift));
    // Rotating pages changes the file, so it has no default shortcut: Ctrl+Shift+- / + rotate the view (as in Acrobat).
    public static readonly RoutedUICommand RotateRight = Make("Rotate Pages _Clockwise", nameof(RotateRight));
    public static readonly RoutedUICommand RotateLeft = Make("Rotate Pages C_ounterclockwise", nameof(RotateLeft));
    public static readonly RoutedUICommand RotatePages = Make("_Rotate Pages…", nameof(RotatePages));
    public static readonly RoutedUICommand DeletePages = Make("_Delete Pages…", nameof(DeletePages), K(Key.D, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand DeleteSelected = Make("Delete Selected Pages", nameof(DeleteSelected));
    public static readonly RoutedUICommand InsertPages = Make("_Insert Pages from File…", nameof(InsertPages), K(Key.I, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand InsertBlank = Make("Insert _Blank Pages…", nameof(InsertBlank));
    public static readonly RoutedUICommand InsertFromScanner = Make("Insert from Sca_nner…", nameof(InsertFromScanner));
    public static readonly RoutedUICommand ExtractPages = Make("_Extract Pages…", nameof(ExtractPages), K(Key.E, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand SplitDocument = Make("_Split Document…", nameof(SplitDocument));
    public static readonly RoutedUICommand MovePages = Make("_Move Pages…", nameof(MovePages), K(Key.M, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand DuplicatePages = Make("D_uplicate Selected Pages", nameof(DuplicatePages));
    public static readonly RoutedUICommand CropPages = Make("Cr_op Pages…", nameof(CropPages));
    public static readonly RoutedUICommand ReversePages = Make("Re_verse Page Order", nameof(ReversePages));

    // Review: panes and markup (bound by MainWindow when the UI for them lands)
    public static readonly RoutedUICommand ShowComments = Make("_Comments", nameof(ShowComments));
    public static readonly RoutedUICommand ShowAttachments = Make("_Attachments", nameof(ShowAttachments));
    public static readonly RoutedUICommand ShowLayers = Make("_Layers", nameof(ShowLayers));
    public static readonly RoutedUICommand ShowSignatures = Make("_Signatures", nameof(ShowSignatures));
    public static readonly RoutedUICommand Sign = Make("Sign _Digitally…", nameof(Sign));
    public static readonly RoutedUICommand Highlight = Make("_Highlight Text", nameof(Highlight), K(Key.H, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand Underline = Make("_Underline Text", nameof(Underline), K(Key.U, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand StrikeOut = Make("_Strike Out Text", nameof(StrikeOut), K(Key.X, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand AddNote = Make("Add _Note", nameof(AddNote), K(Key.C, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand Redact = Make("_Redact", nameof(Redact));
    public static readonly RoutedUICommand RecognizeText = Make("Recognize Te_xt (OCR)…", nameof(RecognizeText));
    public static readonly RoutedUICommand ReloadFromDisk = Make("Re_load from Disk", nameof(ReloadFromDisk), K(Key.F5));
}
