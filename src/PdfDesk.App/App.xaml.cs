using System.Windows;
using System.Windows.Threading;

namespace PdfDesk.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private MainWindow? _ready;
    private readonly List<string[]> _pendingArgs = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        // Crash logging first: everything after this (settings, single instance, the window) may throw.
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write($"Fatal: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write($"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        var args = e.Args;
        var script = Array.IndexOf(args, "--script");
        if (script >= 0 && script + 1 < args.Length)
        {
            if (DevScriptAllowed)
            {
                RunDevScript(e, args, script);
                return;
            }
            // Release builds: a script can drive the UI and write files anywhere, so it needs an explicit opt-in.
            Log.Write("Ignored --script (set PDFDESK_DEVSCRIPT=1 to allow dev scripts in this build).");
            args = [.. args.Where((_, i) => i != script && i != script + 1)];
        }

        _instance = SingleInstance.TryAcquire(args);
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        // Listen before opening the startup files: with a multi-file Explorer open, the other launches arrive now.
        // Their files wait in a queue until the window is ready.
        _instance.Listen(forwarded => Dispatcher.BeginInvoke(() =>
        {
            if (_ready is null)
            {
                _pendingArgs.Add(forwarded);
                return;
            }
            _ready.BringToFront();
            _ready.OpenForwarded(forwarded);
        }));

        base.OnStartup(e);
        ApplyTheme();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (args.Length > 0) window.OpenFiles(args);
        else window.ReopenLastSession();

        _ready = window;
        foreach (var queued in _pendingArgs) window.OpenFiles(queued);
        if (_pendingArgs.Count > 0) window.BringToFront();
        _pendingArgs.Clear();
    }

    /// <summary>Dev scripts run in Debug builds, or in any build when PDFDESK_DEVSCRIPT=1.</summary>
    private static bool DevScriptAllowed =>
#if DEBUG
        true;
#else
        Environment.GetEnvironmentVariable("PDFDESK_DEVSCRIPT") == "1";
#endif

    private void RunDevScript(StartupEventArgs e, string[] args, int script)
    {
        // Dev mode: run UI steps off-screen, separate from any running instance.
        // Never touch the user's settings: save to PDFDESK_DATA if given, else not at all.
        AppSettings.ReadOnly = Environment.GetEnvironmentVariable("PDFDESK_DATA") is not { Length: > 0 };
        base.OnStartup(e);
        var dev = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, Width = 1400, Height = 900, ShowActivated = false };
        dev.WindowState = WindowState.Normal;
        MainWindow = dev;
        dev.Show();
        dev.OpenFiles(args.Where((_, i) => i != script && i != script + 1));
        _ = DevScript.RunAsync(dev, args[script + 1]);
    }

    /// <summary>Applies the theme chosen in Settings (System follows Windows light/dark).</summary>
    public static void ApplyTheme()
    {
        Current.ThemeMode = AppSettings.Current.Theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        foreach (Window w in Current.Windows) ApplyFrame(w);
    }

    /// <summary>True when the app shows dark (an explicit choice, or Windows' app mode when following the system).</summary>
    public static bool IsDark => AppSettings.Current.Theme switch
    {
        "Dark" => true,
        "Light" => false,
        _ => Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int light && light == 0,
    };

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Makes the window frame, caption buttons and Mica follow the app theme (also when it differs from Windows'),
    /// since the title bar is drawn by Windows, not WPF.
    /// </summary>
    public static void ApplyFrame(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var mica = 2;
        DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int)); // DWMWA_SYSTEMBACKDROP_TYPE = Mica
    }

    // Rate limiting for the error dialog: a layout or render exception can repeat many times a second.
    private readonly Dictionary<string, DateTime> _lastErrorShown = [];
    private readonly Queue<DateTime> _recentErrors = new();
    private DateTime _quitOfferedAt = DateTime.MinValue;
    private bool _showingError;
    private const int ErrorStormCount = 8;
    private static readonly TimeSpan ErrorRepeatWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorStormWindow = TimeSpan.FromSeconds(10);

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var ex = e.Exception;
        var now = DateTime.UtcNow;
        var key = ErrorKey(ex);
        var repeat = _lastErrorShown.TryGetValue(key, out var last) && now - last < ErrorRepeatWindow;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > ErrorStormWindow) _recentErrors.Dequeue();

        if (repeat || _showingError)
        {
            Log.Write($"Unhandled (again, not shown): {ex.GetType().Name}: {ex.Message}");
        }
        else
        {
            Log.Write($"Unhandled: {ex}");
        }
        if (_showingError) return;

        if (_recentErrors.Count >= ErrorStormCount && now - _quitOfferedAt > TimeSpan.FromMinutes(1))
        {
            _quitOfferedAt = now;
            _showingError = true;
            try
            {
                if (Prompt.Confirm(null, "PdfDesk keeps running into errors",
                        $"Something keeps going wrong ({ex.Message}). Save your work if you can, or quit PdfDesk now and start it again. Details were written to {Log.FilePath}",
                        "Quit PdfDesk", warning: true))
                {
                    Log.Write("Quitting after repeated errors (user choice).");
                    Shutdown(1);
                }
            }
            finally
            {
                _showingError = false;
                _recentErrors.Clear();
            }
            return;
        }
        if (repeat) return;

        _lastErrorShown[key] = now;
        _showingError = true;
        try
        {
            Prompt.Error(null, "Something went wrong", $"{ex.Message}  Details were written to {Log.FilePath}");
        }
        finally
        {
            _showingError = false;
            // Errors raised while the dialog was open shouldn't immediately reopen it.
            _lastErrorShown[key] = DateTime.UtcNow;
        }
    }

    /// <summary>Same exception type thrown from the same place = the same error.</summary>
    private static string ErrorKey(Exception ex)
    {
        var site = ex.TargetSite is { } m ? $"{m.DeclaringType?.FullName}.{m.Name}" : ex.StackTrace?.Split('\n', 2)[0].Trim() ?? "";
        return $"{ex.GetType().FullName}@{site}";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
