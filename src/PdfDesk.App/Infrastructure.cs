using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PdfDesk.App;

/// <summary>Where PdfDesk keeps its data. Depends on nothing else, so logging works from the first line of startup.</summary>
public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\PdfDesk, or PDFDESK_DATA when set (dev/script runs use a throwaway folder).</summary>
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("PDFDESK_DATA") is { Length: > 0 } dev
        ? dev
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfDesk");

    /// <summary>Per-user id for the single-instance mutex/pipe and the settings lock.</summary>
    public static string UserId { get; } = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
}

/// <summary>
/// Plain-text log at %LOCALAPPDATA%\PdfDesk\logs\pdfdesk.log (kept under ~1 MB, one rotation).
/// Local only; never log passwords or document contents, and prefer file names (<see cref="Name"/>) over full paths.
/// Independent of <see cref="AppSettings"/>, so it works before (and while) settings load.
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private const long MaxBytes = 1_000_000;

    public static string FilePath { get; } = Path.Combine(AppPaths.DataDirectory, "logs", "pdfdesk.log");

    /// <summary>Just the file name of <paramref name="path"/>, for log lines that don't need the folder.</summary>
    public static string Name(string? path) => string.IsNullOrEmpty(path) ? "(unsaved)" : Path.GetFileName(path);

    public static void Write(string message)
    {
        try
        {
            message = message.Replace("\r\n", "\n").Replace("\n", "\n    ");
            Trace.WriteLine(message);
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never take the app down (it also runs inside crash handlers and static initializers).
        }
    }
}

/// <summary>Writes files via a temporary file in the same folder, then swaps it in, so a crash never leaves half a file.</summary>
public static class AtomicFile
{
    /// <param name="prepare">Runs on the finished temporary file before it is swapped in (e.g. to add a stream).</param>
    public static void WriteAllBytes(string path, byte[] bytes, Action<string>? prepare = null)
    {
        var full = Path.GetFullPath(path);
        var tmp = Path.Combine(Path.GetDirectoryName(full)!, $"~pdfdesk-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            prepare?.Invoke(tmp);
            if (!File.Exists(full))
            {
                File.Move(tmp, full);
                return;
            }
            try
            {
                File.Replace(tmp, full, null, ignoreMetadataErrors: true);
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
            {
                // Some file systems (FAT, a few network shares) don't support Replace.
                File.Move(tmp, full, overwrite: true);
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
}

/// <summary>Opens URLs and folders without letting a missing handler crash the app.</summary>
public static class Shell
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Write($"Could not open {Log.Name(target)}: {ex.Message}");
        }
    }

    public static void ShowInFolder(string path)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"Could not show {Log.Name(path)}: {ex.Message}");
        }
    }
}

/// <summary>
/// One PdfDesk per user session. A second launch ("Open with PdfDesk" while it's open) hands its file arguments to
/// the running copy over a named pipe only this user can open, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Same scope for mutex and pipe: per user and per logon session. The mutex is Local\ (per session) but pipe names
    // are machine-wide, so the name carries the session id too, or a second session would hand its files to the first.
    private static readonly string Id = $"PdfDesk-{AppPaths.UserId}-{Process.GetCurrentProcess().SessionId}";
    private const int ConnectTimeoutMs = 10_000;
    private const int MaxListenFailures = 8;

    /// <summary>Null when this copy runs standalone because the owner couldn't be reached.</summary>
    private readonly Mutex? _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex? mutex) => _mutex = mutex;

    /// <summary>True for the copy that owns the session (and listens for other launches).</summary>
    public bool IsOwner => _mutex is not null;

    /// <summary>Returns the owner instance, or null after forwarding <paramref name="args"/> to the running one.</summary>
    public static SingleInstance? TryAcquire(string[] args)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{Id}", out var created);
        if (created) return new SingleInstance(mutex);
        mutex.Dispose();
        return Forward(args) ? null : new SingleInstance(null);
    }

    /// <summary>
    /// Calls <paramref name="onArgs"/> (on a background thread) whenever another launch forwards its arguments.
    /// Start it before opening the startup files so simultaneous launches (a multi-file Explorer open) get through.
    /// Only the owner listens: a standalone fallback copy would just fight over the pipe name.
    /// </summary>
    public void Listen(Action<string[]> onArgs)
    {
        if (!IsOwner) return;
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            var failures = 0;
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(Id, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    failures = 0;
                    var connected = server;
                    server = null;
                    // Read on its own task so the next launch can connect right away.
                    _ = Task.Run(() => ReadAsync(connected, onArgs, token));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Log the first failure only, back off, and give up after a few tries instead of spinning.
                    if (++failures == 1) Log.Write($"Single-instance pipe: {ex.Message}");
                    if (failures >= MaxListenFailures)
                    {
                        Log.Write($"Single-instance pipe: giving up after {failures} failures; other launches will open their own window.");
                        return;
                    }
                    try
                    {
                        await Task.Delay(Math.Min(30_000, 500 << failures), token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }, token);
    }

    private static async Task ReadAsync(NamedPipeServerStream server, Action<string[]> onArgs, CancellationToken token)
    {
        try
        {
            await using (server)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(10_000);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(timeout.Token);
                onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested) Log.Write($"Single-instance: dropped a forwarded launch: {ex.GetType().Name}");
        }
    }

    /// <summary>True when the running instance received the arguments.</summary>
    private static bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            // Generous: the owner may itself be starting up (it listens before opening its own files).
            client.Connect(ConnectTimeoutMs);
            // The owner runs in another folder: every path becomes absolute against this launch's working directory.
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', args.Select(Absolute)));
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The other instance is hung or shutting down: run standalone rather than losing the user's file.
            Log.Write($"Could not reach the running PdfDesk ({ex.GetType().Name}); opening a separate window.");
            return false;
        }
    }

    /// <summary><paramref name="arg"/> as a full path (also when no such file exists); options and non-file URIs as they are.</summary>
    internal static string Absolute(string arg)
    {
        if (arg.Length == 0 || arg.StartsWith("--", StringComparison.Ordinal)
            || Uri.TryCreate(arg, UriKind.Absolute, out var uri) && !uri.IsFile)
            return arg;
        try
        {
            return Path.GetFullPath(arg);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return arg;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}

/// <summary>
/// User preferences in %LOCALAPPDATA%\PdfDesk\settings.json. Saving writes atomically and merges list data (recent files,
/// per-file view state) with what another PdfDesk window may have saved meanwhile.
/// </summary>
public sealed class AppSettings
{
    private const int MaxRecent = 15;
    private const int MaxFiles = 300;

    /// <summary>Same as <see cref="AppPaths.DataDirectory"/>.</summary>
    public static string DataDirectory => AppPaths.DataDirectory;

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    // The window position starts as NaN ("not set yet"), which plain JSON can't represent.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppSettings Current { get; } = Load();

    /// <summary>Dev/script runs must not overwrite the user's settings.</summary>
    public static bool ReadOnly { get; set; }

    // What this process last read from or wrote to disk, and what it changed since: the base of the three-way merge.
    private HashSet<string> _baseRecent = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _baseFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);

    public List<string> RecentFiles { get; set; } = [];
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; } = true;
    public double NavPaneWidth { get; set; } = 210;
    public bool NavPaneVisible { get; set; } = true;
    public string Layout { get; set; } = "Continuous";
    public string ZoomMode { get; set; } = "FitWidth";
    public string LastFolder { get; set; } = "";

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    /// <summary>Reopen each file at the page and zoom it was left at.</summary>
    public bool RestorePosition { get; set; } = true;
    /// <summary>Reopen the documents that were open when PdfDesk was closed.</summary>
    public bool ReopenTabs { get; set; }
    public List<string> OpenTabs { get; set; } = [];
    /// <summary>Show pages with inverted colors (night reading).</summary>
    public bool DarkPages { get; set; }
    public double OrganizerThumbSize { get; set; } = 170;
    /// <summary>Author name on new comments; empty = the Windows user name.</summary>
    public string AuthorName { get; set; } = "";
    /// <summary>Digital signing: the last digital ID used (certificate thumbprint) and the dialog's last choices.</summary>
    public string SignThumbprint { get; set; } = "";
    public string SignReason { get; set; } = "";
    public string SignLocation { get; set; } = "";
    public bool SignVisible { get; set; } = true;
    public bool SignShowDate { get; set; } = true;
    public bool SignShowReason { get; set; } = true;
    public bool SignShowDn { get; set; }
    public string SignImage { get; set; } = "";
    /// <summary>Add an RFC 3161 timestamp (sends only a hash of the signature to <see cref="TimestampUrl"/>).</summary>
    public bool SignTimestamp { get; set; }
    public string TimestampUrl { get; set; } = "http://timestamp.digicert.com";
    /// <summary>Per-file view state, keyed by full path (most recent 300 kept).</summary>
    public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed class FileState
    {
        public int Page { get; set; }
        public string ZoomMode { get; set; } = "FitWidth";
        public double Zoom { get; set; } = 1;
        public DateTime LastOpened { get; set; }
    }

    /// <summary>What <see cref="ClearRecent"/> removed, for its Undo.</summary>
    public sealed record RecentSnapshot(List<string> RecentFiles, Dictionary<string, FileState> Files, List<string> OpenTabs);

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecent) RecentFiles.RemoveRange(MaxRecent, RecentFiles.Count - MaxRecent);
        LastFolder = Path.GetDirectoryName(path) ?? LastFolder;
        StateOf(path).LastOpened = DateTime.Now;
        Save();
    }

    /// <summary>Forgets a file: its recent entry, its remembered page/zoom and its reopen-at-startup entry.</summary>
    public void RemoveRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Files.Remove(path);
        OpenTabs.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _touched.Remove(path);
        Save();
    }

    /// <summary>Clears the recent list and every remembered file path (view state, tabs to reopen). Returns them for Undo.</summary>
    public RecentSnapshot ClearRecent()
    {
        var snapshot = new RecentSnapshot([.. RecentFiles], new Dictionary<string, FileState>(Files, StringComparer.OrdinalIgnoreCase), [.. OpenTabs]);
        RecentFiles.Clear();
        Files.Clear();
        OpenTabs.Clear();
        _touched.Clear();
        Save();
        return snapshot;
    }

    public void RestoreRecent(RecentSnapshot snapshot)
    {
        RecentFiles = [.. snapshot.RecentFiles];
        foreach (var (path, state) in snapshot.Files) Files.TryAdd(path, state);
        OpenTabs = [.. snapshot.OpenTabs];
        _touched.UnionWith(RecentFiles);
        _touched.UnionWith(Files.Keys);
        Save();
    }

    public FileState StateOf(string path)
    {
        // A new entry counts as just opened, so pruning (oldest first) never drops the file being viewed now.
        if (!Files.TryGetValue(path, out var state)) Files[path] = state = new FileState { LastOpened = DateTime.Now };
        _touched.Add(path);
        Prune();
        return state;
    }

    /// <summary>Keeps the <see cref="MaxFiles"/> most recently opened files; entries this window used since its last save go last.</summary>
    private void Prune()
    {
        if (Files.Count > MaxFiles)
            foreach (var old in Files.OrderBy(f => _touched.Contains(f.Key)).ThenBy(f => f.Value.LastOpened)
                         .Take(Files.Count - MaxFiles).Select(f => f.Key).ToList())
                Files.Remove(old);
    }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            using var gate = new Mutex(false, $@"Local\PdfDesk-settings-{AppPaths.UserId}");
            var locked = false;
            try
            {
                try
                {
                    locked = gate.WaitOne(2000);
                }
                catch (AbandonedMutexException)
                {
                    locked = true;
                }
                if (TryRead(out var disk)) MergeFrom(disk);
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
                SetBaseline();
            }
            finally
            {
                if (locked) gate.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            // Settings are a convenience: failing to save them must never block opening or saving a document.
            Log.Write($"Could not save settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Three-way merge of the list data with the file on disk: keeps entries another window added since this one last read
    /// or wrote the file, drops entries another window removed (unless this window used them since), and keeps the newer
    /// view state when both have one. Plain preferences: last writer wins.
    /// </summary>
    private void MergeFrom(AppSettings disk)
    {
        foreach (var (path, state) in disk.Files)
        {
            if (Files.TryGetValue(path, out var mine))
            {
                if (state.LastOpened > mine.LastOpened) Files[path] = state;
            }
            else if (!_baseFiles.Contains(path))
            {
                Files[path] = state; // added by another window
            }
        }
        foreach (var path in _baseFiles.Where(p => !disk.Files.ContainsKey(p) && !_touched.Contains(p)).ToList())
            Files.Remove(path); // removed by another window

        var recent = new List<string>(RecentFiles);
        recent.RemoveAll(p => _baseRecent.Contains(p) && !disk.RecentFiles.Contains(p, StringComparer.OrdinalIgnoreCase) && !_touched.Contains(p));
        foreach (var path in disk.RecentFiles)
            if (!_baseRecent.Contains(path) && !recent.Contains(path, StringComparer.OrdinalIgnoreCase)) recent.Add(path);
        RecentFiles = [.. recent
            .Select((p, i) => (p, i, when: Files.TryGetValue(p, out var s) ? s.LastOpened : DateTime.MinValue))
            .OrderByDescending(x => x.when).ThenBy(x => x.i)
            .Select(x => x.p)
            .Take(MaxRecent)];
        Prune();
    }

    private void SetBaseline()
    {
        _baseRecent = new HashSet<string>(RecentFiles, StringComparer.OrdinalIgnoreCase);
        _baseFiles = new HashSet<string>(Files.Keys, StringComparer.OrdinalIgnoreCase);
        _touched.Clear();
    }

    /// <summary>Reads settings.json. A corrupt file is kept as settings.json.bak and logged, never silently dropped.</summary>
    private static bool TryRead(out AppSettings settings)
    {
        settings = new AppSettings();
        try
        {
            if (!File.Exists(FilePath)) return false;
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
            settings.Files = new Dictionary<string, FileState>(settings.Files ?? [], StringComparer.OrdinalIgnoreCase);
            settings.RecentFiles ??= [];
            settings.OpenTabs ??= [];
            return true;
        }
        catch (JsonException ex)
        {
            try
            {
                File.Copy(FilePath, FilePath + ".bak", overwrite: true);
                Log.Write($"Settings file is damaged; kept a copy as settings.json.bak and started fresh: {ex.Message}");
            }
            catch (Exception copyEx)
            {
                Log.Write($"Settings file is damaged and couldn't be backed up ({copyEx.Message}): {ex.Message}");
            }
            settings = new AppSettings();
            return false;
        }
        catch (Exception ex)
        {
            Log.Write($"Settings unreadable, using defaults: {ex.Message}");
            settings = new AppSettings();
            return false;
        }
    }

    private static AppSettings Load()
    {
        TryRead(out var loaded);
        loaded.SetBaseline();
        return loaded;
    }
}
