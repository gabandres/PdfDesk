using System.Windows.Input;

namespace PdfDesk.App;

/// <summary>App commands. Gestures here are window-wide, so none are bare letters (those would eat typing in text boxes).</summary>
public static class Cmd
{
    private static RoutedUICommand Make(string text, string name, params InputGesture[] gestures) =>
        new(text, name, typeof(Cmd), new InputGestureCollection(gestures));

    private static KeyGesture K(Key key, ModifierKeys mods = ModifierKeys.None, string? display = null) =>
        display is null ? new KeyGesture(key, mods) : new KeyGesture(key, mods, display);

    // File
    public static readonly RoutedUICommand Open = Make("_Open…", nameof(Open), K(Key.O, ModifierKeys.Control));
    public static readonly RoutedUICommand Combine = Make("_Combine Files into PDF…", nameof(Combine));
    public static readonly RoutedUICommand Save = Make("_Save", nameof(Save), K(Key.S, ModifierKeys.Control));
    public static readonly RoutedUICommand SaveAs = Make("Save _As…", nameof(SaveAs), K(Key.S, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand CloseDoc = Make("_Close", nameof(CloseDoc), K(Key.W, ModifierKeys.Control), K(Key.F4, ModifierKeys.Control));
    public static readonly RoutedUICommand CloseAll = Make("Close A_ll", nameof(CloseAll), K(Key.W, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand Print = Make("_Print…", nameof(Print), K(Key.P, ModifierKeys.Control));
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
    public static readonly RoutedUICommand NavPane = Make("_Navigation Pane", nameof(NavPane), K(Key.F4));
    public static readonly RoutedUICommand FullScreen = Make("_Full Screen", nameof(FullScreen), K(Key.F11));
    public static readonly RoutedUICommand FirstPage = Make("_First Page", nameof(FirstPage), K(Key.Home, ModifierKeys.Control));
    public static readonly RoutedUICommand PrevPage = Make("_Previous Page", nameof(PrevPage));
    public static readonly RoutedUICommand NextPage = Make("_Next Page", nameof(NextPage));
    public static readonly RoutedUICommand LastPage = Make("_Last Page", nameof(LastPage), K(Key.End, ModifierKeys.Control));
    public static readonly RoutedUICommand GoToPage = Make("_Go to Page…", nameof(GoToPage), K(Key.G, ModifierKeys.Control));
    public static readonly RoutedUICommand Back = Make("Previous _View", nameof(Back), K(Key.Left, ModifierKeys.Alt));
    public static readonly RoutedUICommand Forward = Make("Next Vie_w", nameof(Forward), K(Key.Right, ModifierKeys.Alt));

    // App
    public static readonly RoutedUICommand CommandPalette = Make("Command _Palette…", nameof(CommandPalette), K(Key.K, ModifierKeys.Control), K(Key.P, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand Settings = Make("_Settings…", nameof(Settings), K(Key.OemComma, ModifierKeys.Control, "Ctrl+,"));
    public static readonly RoutedUICommand Shortcuts = Make("_Keyboard Shortcuts", nameof(Shortcuts), K(Key.F1));
    public static readonly RoutedUICommand DarkPages = Make("_Dark Pages (Night Reading)", nameof(DarkPages));
    public static readonly RoutedUICommand ToggleDarkMode = Make("Toggle Dar_k Mode", nameof(ToggleDarkMode), K(Key.L, ModifierKeys.Control | ModifierKeys.Shift));

    // Organize
    public static readonly RoutedUICommand Organize = Make("_Organize Pages", nameof(Organize), K(Key.O, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand RotateRight = Make("Rotate _Clockwise", nameof(RotateRight), K(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift++"));
    public static readonly RoutedUICommand RotateLeft = Make("Rotate C_ounterclockwise", nameof(RotateLeft), K(Key.OemMinus, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+-"));
    public static readonly RoutedUICommand RotatePages = Make("_Rotate Pages…", nameof(RotatePages));
    public static readonly RoutedUICommand DeletePages = Make("_Delete Pages…", nameof(DeletePages), K(Key.D, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand DeleteSelected = Make("Delete Selected Pages", nameof(DeleteSelected));
    public static readonly RoutedUICommand InsertPages = Make("_Insert Pages from File…", nameof(InsertPages), K(Key.I, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand InsertBlank = Make("Insert _Blank Pages…", nameof(InsertBlank));
    public static readonly RoutedUICommand ExtractPages = Make("_Extract Pages…", nameof(ExtractPages), K(Key.E, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand SplitDocument = Make("_Split Document…", nameof(SplitDocument));
    public static readonly RoutedUICommand MovePages = Make("_Move Pages…", nameof(MovePages), K(Key.M, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand DuplicatePages = Make("D_uplicate Selected Pages", nameof(DuplicatePages));
    public static readonly RoutedUICommand CropPages = Make("Cr_op Pages…", nameof(CropPages));
    public static readonly RoutedUICommand ReversePages = Make("Re_verse Page Order", nameof(ReversePages));
}
