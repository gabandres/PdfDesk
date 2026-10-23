namespace PdfDesk.App;

/// <summary>Commands for new-from-images, password protection, reducing size, flattening, bookmarks and Read Aloud.</summary>
public partial class MainWindow
{
    private void BindToolCommands()
    {
        BindAlways(Cmd.NewFromImages, () => _ = FileTools.NewFromImages(this));
        BindAlways(Cmd.NewFromClipboard, () => _ = FileTools.NewFromClipboard(this));
        BindEdit(Cmd.ProtectPassword, t => SecurityActions.Protect(this, t));
        BindEdit(Cmd.RemoveProtection, t => SecurityActions.RemoveProtection(this, t), t => t.Session.Engine.IsEncrypted);
        BindEdit(Cmd.ReduceSize, t => FileTools.ReduceSize(this, t));
        BindEdit(Cmd.Flatten, t => FileTools.Flatten(this, t));
        Bind(Cmd.AddBookmark, t => t.AddBookmarkForView(), t => t.Session.IsPdf && t.PageView.PageCount > 0);
        BindTab(Cmd.ReadAloud, t => _ = t.ToggleReadAloud(), t => t.PageView.PageCount > 0);
    }
}
