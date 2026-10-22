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
            AppSettings.ReadOnly = true;
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
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        window.OpenFiles(e.Args);

        _instance.Listen(args => Dispatcher.BeginInvoke(() =>
        {
            window.BringToFront();
            window.OpenFiles(args);
        }));
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write($"Unhandled: {e.Exception}");
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in {Log.FilePath}", "PdfDesk",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
