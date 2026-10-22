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
