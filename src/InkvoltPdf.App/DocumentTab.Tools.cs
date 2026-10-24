namespace InkvoltPdf.App;

/// <summary>Read Aloud for this tab, and notices the file tools show.</summary>
public partial class DocumentTab
{
    private ReadAloud? _readAloud;

    /// <summary>True while Read Aloud reads this document (or is paused).</summary>
    public bool IsReadingAloud => _readAloud?.IsActive == true;

    /// <summary>Read Aloud's controller (made on first use).</summary>
    internal ReadAloud ReadAloudPlayer => _readAloud ??= new ReadAloud(this, Viewer, ReadingView);

    /// <summary>Starts reading from the selection or the view, or stops (Ctrl+Shift+Y).</summary>
    public Task ToggleReadAloud() => _disposed ? Task.CompletedTask : ReadAloudPlayer.Toggle();

    internal void RaiseReadAloudChanged() => RaiseState();

    /// <summary>
    /// A toast about this document. With <paramref name="undo"/>, its Undo button undoes the newest edit, as long as that
    /// is still the newest one.
    /// </summary>
    public void ShowNotice(string message, bool undo = false, bool error = false)
    {
        if (!undo)
        {
            Notify(message, error: error);
            return;
        }
        var depth = Session.UndoDepth;
        var label = Session.UndoLabel;
        Notify(message, "Undo", () =>
        {
            if (_disposed || IsBusy || Session.UndoDepth != depth || Session.UndoLabel != label) return;
            Undo();
        }, error);
    }
}
