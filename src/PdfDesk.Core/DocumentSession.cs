using MuPDFCore;

namespace PdfDesk.Core;

/// <summary>
/// An open document: its bytes, the MuPDF view of them, and undo/redo snapshots of page edits. The file on disk is
/// read once and never locked; it changes only on Save.
/// </summary>
public sealed class DocumentSession : IDisposable
{
    private const int MaxUndoSteps = 50;
    private const long MaxUndoBytes = 768L * 1024 * 1024;

    private readonly LinkedList<(byte[] Data, string Label, int Version)> _undo = new();
    private readonly Stack<(byte[] Data, string Label, int Version)> _redo = new();
    private int _version;
    private int _savedVersion;
    private int _nextVersion = 1;
    private (long Length, DateTime WriteTimeUtc)? _onDisk;

    public string? FilePath { get; private set; }
    public string DisplayName { get; private set; }
    public byte[] Data { get; private set; }
    public string? Password { get; }
    public InputFileTypes FileType { get; }
    public PdfEngine Engine { get; private set; }
    /// <summary>True when the content differs from what is on disk (undoing back to the saved state clears it).</summary>
    public bool IsDirty => _version != _savedVersion;
    public bool IsPdf => FileType == InputFileTypes.PDF;

    public bool CanUndo => _undo.Count > 0;
    /// <summary>Number of undo steps; lets a toast's Undo act only on the edit it announced.</summary>
    public int UndoDepth => _undo.Count;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Last?.Value.Label;
    public string? RedoLabel => _redo.TryPeek(out var r) ? r.Label : null;

    /// <summary>Raised after the document content changed (edit, undo, redo); the engine has been replaced.</summary>
    public event EventHandler? ContentChanged;

    /// <summary>Raised when the name, path or dirty state changed.</summary>
    public event EventHandler? StateChanged;

    private DocumentSession(string? path, string displayName, byte[] data, string? password, InputFileTypes type, PdfEngine engine)
    {
        FilePath = path;
        DisplayName = displayName;
        Data = data;
        Password = password;
        FileType = type;
        Engine = engine;
    }

    /// <exception cref="PasswordRequiredException"/>
    public static DocumentSession Open(string path, string? password = null)
    {
        // Stamp before reading: a write that lands mid-read then shows up as a change rather than going unnoticed.
        var stamp = DiskStamp(path);
        var data = File.ReadAllBytes(path);
        var type = PdfEngine.TypeFromPath(path);
        var engine = PdfEngine.Open(data, type, password);
        return new DocumentSession(Path.GetFullPath(path), Path.GetFileName(path), data, password, type, engine) { _onDisk = stamp };
    }

    /// <summary>A new, unsaved PDF (e.g. the result of Combine or Extract).</summary>
    public static DocumentSession FromBytes(byte[] data, string displayName, string? password = null)
    {
        var engine = PdfEngine.Open(data, InputFileTypes.PDF, password);
        var session = new DocumentSession(null, displayName, data, password, InputFileTypes.PDF, engine);
        session._savedVersion = -1; // never saved
        return session;
    }

    /// <summary>Replaces the content with edited bytes, recording an undo step.</summary>
    public void Commit(byte[] newData, string label)
    {
        var engine = PdfEngine.Open(newData, FileType, Password);
        _undo.AddLast((Data, label, _version));
        TrimUndo();
        _redo.Clear();
        Swap(newData, engine, _nextVersion++);
    }

    public void Undo()
    {
        if (_undo.Last is not { } node) return;
        var (data, label, version) = node.Value;
        var engine = PdfEngine.Open(data, FileType, Password);
        _undo.RemoveLast();
        _redo.Push((Data, label, _version));
        Swap(data, engine, version);
    }

    public void Redo()
    {
        if (!_redo.TryPeek(out var step)) return;
        var engine = PdfEngine.Open(step.Data, FileType, Password);
        _redo.Pop();
        _undo.AddLast((Data, step.Label, _version));
        Swap(step.Data, engine, step.Version);
    }

    /// <summary>
    /// Writes the current bytes to <paramref name="path"/> atomically: a flushed temp file in the same folder, then
    /// swapped in with File.Replace (which keeps the target's ACLs, attributes and creation time).
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The target is read-only (or access is denied).</exception>
    public void SaveAs(string path)
    {
        path = Path.GetFullPath(path);
        var target = new FileInfo(path);
        if (target.Exists && target.IsReadOnly)
            throw new UnauthorizedAccessException($"\"{target.Name}\" is read-only. Save a copy under another name, or clear its read-only attribute.");

        var temp = Path.Combine(target.DirectoryName!, $".{target.Name}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                stream.Write(Data);
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(path))
            {
                File.Move(temp, path);
            }
            else
            {
                try
                {
                    File.Replace(temp, path, destinationBackupFileName: null);
                }
                catch (IOException) when (File.Exists(temp))
                {
                    // Some file systems (FAT, certain network shares) can't replace; the target may already be gone.
                    File.Move(temp, path, overwrite: true);
                }
            }
        }
        finally
        {
            // Best effort: a failed cleanup must not hide the real error (or turn a completed save into a failure).
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        FilePath = path;
        DisplayName = Path.GetFileName(path);
        _savedVersion = _version;
        _onDisk = DiskStamp(path);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when the file is missing or its size or last-write time differ from when it was opened or last saved.</summary>
    public bool HasChangedOnDisk()
    {
        if (FilePath is null) return false;
        var now = DiskStamp(FilePath);
        return now is null || (_onDisk is { } then && now != then);
    }

    /// <summary>True when the document came from a file that no longer exists (moved, renamed or deleted).</summary>
    public bool IsMissingOnDisk() => FilePath is not null && !File.Exists(FilePath);

    /// <summary>
    /// Re-reads the file (same password), discarding unsaved changes and undo history; raises ContentChanged then
    /// StateChanged. On failure (I/O, password, corrupt file) nothing changes.
    /// </summary>
    public void ReloadFromDisk()
    {
        if (FilePath is null) throw new InvalidOperationException("This document has not been saved to a file.");
        var stamp = DiskStamp(FilePath);
        var data = File.ReadAllBytes(FilePath);
        var engine = PdfEngine.Open(data, FileType, Password);
        _undo.Clear();
        _redo.Clear();
        _onDisk = stamp;
        var version = _nextVersion++;
        _savedVersion = version;
        Swap(data, engine, version);
    }

    private static (long, DateTime)? DiskStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
    }

    private void Swap(byte[] data, PdfEngine engine, int version)
    {
        var old = Engine;
        Data = data;
        Engine = engine;
        _version = version;
        ContentChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
        old.Dispose();
    }

    private void TrimUndo()
    {
        var total = _undo.Sum(u => (long)u.Data.Length);
        while (_undo.Count > 1 && (_undo.Count > MaxUndoSteps || total > MaxUndoBytes))
        {
            total -= _undo.First!.Value.Data.Length;
            _undo.RemoveFirst();
        }
    }

    public void Dispose() => Engine.Dispose();
}
