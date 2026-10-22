using System.Windows;
using System.Windows.Threading;

namespace PdfDesk.App;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        var script = Array.IndexOf(e.Args, "--script");
        if (script >= 0 && script + 1 < e.Args.Length)
        {
            // Dev mode: run UI steps off-screen, separate from any running instance.
            // Never touch the user's settings: save to PDFDESK_DATA if given, else not at all.
            AppSettings.ReadOnly = Environment.GetEnvironmentVariable("PDFDESK_DATA") is not { Length: > 0 };
            base.OnStartup(e);
            var dev = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, Width = 1400, Height = 900, ShowActivated = false };
            dev.WindowState = WindowState.Normal;
            MainWindow = dev;
            dev.Show();
            dev.OpenFiles(e.Args.Where((_, i) => i != script && i != script + 1));
            _ = DevScript.RunAsync(dev, e.Args[script + 1]);
            return;
        }

        _instance = SingleInstance.TryAcquire(e.Args);
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write($"Fatal: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write($"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        base.OnStartup(e);
        ApplyTheme();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0) window.OpenFiles(e.Args);
        else window.ReopenLastSession();

        _instance.Listen(args => Dispatcher.BeginInvoke(() =>
        {
            window.BringToFront();
            window.OpenFiles(args);
        }));
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

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write($"Unhandled: {e.Exception}");
        Prompt.Error(null, "Something went wrong", $"{e.Exception.Message}  Details were written to {Log.FilePath}");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
