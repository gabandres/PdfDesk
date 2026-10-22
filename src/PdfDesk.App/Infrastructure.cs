using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PdfDesk.App;

/// <summary>
/// Plain-text log at %LOCALAPPDATA%\PdfDesk\logs\pdfdesk.log (kept under ~1 MB, one rotation).
/// Local only; never log passwords or document contents.
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private const long MaxBytes = 1_000_000;

    public static string FilePath { get; } = Path.Combine(AppSettings.DataDirectory, "logs", "pdfdesk.log");

    public static void Write(string message)
    {
        message = message.Replace("\r\n", "\n").Replace("\n", "\n    ");
        Trace.WriteLine(message);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
        }
    }
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
            Log.Write($"Could not open {target}: {ex.Message}");
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
            Log.Write($"Could not show {path}: {ex.Message}");
        }
    }
}

/// <summary>
/// One PdfDesk per user session. A second launch ("Open with PdfDesk" while it's open) hands its file arguments to
/// the running copy over a named pipe only this user can open, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Id = $"PdfDesk-{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Returns the owner instance, or null after forwarding <paramref name="args"/> to the running one.</summary>
    public static SingleInstance? TryAcquire(string[] args)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{Id}", out var created);
        if (created) return new SingleInstance(mutex);
        mutex.Dispose();
        return Forward(args) ? null : new SingleInstance(new Mutex(false));
    }

    /// <summary>Calls <paramref name="onArgs"/> (on a background thread) whenever another launch forwards its arguments.</summary>
    public void Listen(Action<string[]> onArgs)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(_cts.Token);
                    onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Write($"Single-instance pipe: {ex.Message}");
                    await Task.Delay(500);
                }
            }
        });
    }

    /// <summary>True when the running instance received the arguments.</summary>
    private static bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', args.Select(a => File.Exists(a) ? Path.GetFullPath(a) : a)));
            client.Write(bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The other instance is hung or shutting down: run standalone rather than losing the user's file.
            Log.Write($"Could not reach the running PdfDesk: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}

/// <summary>User preferences in %LOCALAPPDATA%\PdfDesk\settings.json.</summary>
public sealed class AppSettings
{
    private const int MaxRecent = 15;

    /// <summary>%LOCALAPPDATA%\PdfDesk, or PDFDESK_DATA when set (dev/script runs use a throwaway folder).</summary>
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("PDFDESK_DATA") is { Length: > 0 } dev
        ? dev
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfDesk");

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
    /// <summary>Per-file view state, keyed by full path (most recent 300 kept).</summary>
    public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed class FileState
    {
        public int Page { get; set; }
        public string ZoomMode { get; set; } = "FitWidth";
        public double Zoom { get; set; } = 1;
        public DateTime LastOpened { get; set; }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecent) RecentFiles.RemoveRange(MaxRecent, RecentFiles.Count - MaxRecent);
        LastFolder = Path.GetDirectoryName(path) ?? LastFolder;
        StateOf(path).LastOpened = DateTime.Now;
        Save();
    }

    public void RemoveRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public FileState StateOf(string path)
    {
        if (!Files.TryGetValue(path, out var state)) Files[path] = state = new FileState();
        if (Files.Count > 300)
            foreach (var old in Files.OrderBy(f => f.Value.LastOpened).Take(Files.Count - 300).Select(f => f.Key).ToList())
                Files.Remove(old);
        return state;
    }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            // Settings are a convenience: failing to save them must never block opening or saving a document.
            Log.Write($"Could not save settings: {ex.Message}");
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
                loaded.Files = new Dictionary<string, FileState>(loaded.Files, StringComparer.OrdinalIgnoreCase);
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Settings unreadable, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }
}
