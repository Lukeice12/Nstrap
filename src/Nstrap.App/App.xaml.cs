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
    internal IdentityTicket? IdentityRestart { get; private set; }
    internal static string InstanceMutexName(string dataDirectory) =>
        @"Local\Nstrap-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))[..20];
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--removal-helper")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int result = 1;
            try
            {
                if (e.Args.Length == 3)
                    result = await RemovalSession.RunHelperAsync(e.Args[1], e.Args[2]);
            }
            catch { /* Removal helper failures never open a window. */ }
            Shutdown(result);
            return;
        }
        // The copied executable runs without a window, profile load, client
        // discovery, FPS work or the normal application's single-instance mutex.
        if (e.Args.FirstOrDefault() == "--update-helper")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int result = 1;
            try
            {
                if (e.Args.Length == 3)
                {
                    UpdateHandoff.WatchRestartGuard(new(e.Args[1], e.Args[2]));
                    result = await UpdateHandoff.RunHelperAsync(new(e.Args[1], e.Args[2]));
                }
            }
            catch { /* Malformed helper invocations cannot enter normal startup. */ }
            Shutdown(result);
            return;
        }
        try
        {
            string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nstrap");
            if (e.Args.FirstOrDefault() == "--identity-restart")
            {
                if (e.Args.Length != 3) throw new ArgumentException("Invalid program rename restart arguments.");
                EmergencyActions.WatchRestartGuard(e.Args[2]);
                IdentityRestart = new(e.Args[1], e.Args[2]);
                await AppIdentity.AwaitRestartAsync(IdentityRestart);
            }
            if (e.Args.FirstOrDefault() is "--update-resume" or "--update-rollback")
            {
                if (e.Args.Length != 3) throw new ArgumentException("Invalid update restart arguments.");
                UpdateRestart = new(e.Args[1], e.Args[2]);
                UpdateHandoff.WatchRestartGuard(UpdateRestart);
                UpdateRolledBack = e.Args[0] == "--update-rollback";
                UpdateHandoff.ValidateResume(UpdateRestart, UpdateRolledBack);
                EmergencyActions.TrackUpdateRestart(UpdateRestart, UpdateRolledBack);
            }
            if (UpdateRestart is null && IdentityRestart is null && e.Args.Length != 0)
                throw new ArgumentException($"Unknown or incomplete option: {e.Args[0]}");
            _instance = new Mutex(true, InstanceMutexName(dataDirectory), out bool first);
            if (!first)
            {
                MessageDialog.Show("N strap is already running for this data folder.", "N strap", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            DispatcherUnhandledException += OnUnhandledException;
            var model = new MainViewModel(dataDirectory);
            ThemeManager.Apply(model.SelectedTheme);
            MainWindow = new MainWindow(model);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            if (UpdateRestart is null || UpdateRolledBack)
                MessageDialog.Show($"N strap could not start. Existing data was left untouched.\n\n{ex.Message}", "N strap", MessageBoxButton.OK, MessageBoxImage.Error);
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
        EmergencyActions.DisarmNuke();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
