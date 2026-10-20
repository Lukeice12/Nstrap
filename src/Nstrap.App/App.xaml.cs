using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Nstrap.App.Services;

namespace Nstrap.App;

public partial class App : Application
{
    private Mutex? _instance;
    internal UpdateTicket? UpdateRestart { get; private set; }
    internal bool UpdateRolledBack { get; private set; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The copied executable runs without a window, profile load, client
        // discovery, FPS work or the normal application's single-instance mutex.
        if (e.Args.FirstOrDefault() == "--update-helper")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int result = 1;
            try
            {
                if (e.Args.Length == 3)
                    result = await UpdateHandoff.RunHelperAsync(new(e.Args[1], e.Args[2]));
            }
            catch { /* Malformed helper invocations cannot enter normal startup. */ }
            Shutdown(result);
            return;
        }
        try
        {
            string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nstrap");
            if (e.Args.FirstOrDefault() is "--update-resume" or "--update-rollback")
            {
                if (e.Args.Length != 3) throw new ArgumentException("Invalid update restart arguments.");
                UpdateRestart = new(e.Args[1], e.Args[2]);
                UpdateRolledBack = e.Args[0] == "--update-rollback";
                UpdateHandoff.ValidateResume(UpdateRestart, UpdateRolledBack);
            }
            if (UpdateRestart is null && e.Args.Length != 0)
                throw new ArgumentException($"Unknown or incomplete option: {e.Args[0]}");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))[..20];
            _instance = new Mutex(true, @"Local\Nstrap-" + key, out bool first);
            if (!first)
            {
                MessageBox.Show("N strap is already running for this data folder.", "N strap", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            DispatcherUnhandledException += OnUnhandledException;
            MainWindow = new MainWindow(new MainViewModel(dataDirectory));
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            if (UpdateRestart is null || UpdateRolledBack)
                MessageBox.Show($"N strap could not start. Existing data was left untouched.\n\n{ex.Message}", "N strap", MessageBoxButton.OK, MessageBoxImage.Error);
            // A failed updated startup must exit so the waiting helper can
            // restore the original. A modal dialog here would hold its EXE open.
            Shutdown(1);
        }
    }
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (MainWindow?.DataContext is MainViewModel model) model.Error(e.Exception);
        e.Handled = true;
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
