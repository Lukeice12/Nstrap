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
    public string? SmokeScreenshot { get; private set; }
    public string? SmokeImportFile { get; private set; }
    public bool UpdatesAllowed { get; private set; } = true;
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
            for (int i = 0; UpdateRestart is null && i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--data-dir" && i + 1 < e.Args.Length)
                {
                    dataDirectory = Path.GetFullPath(e.Args[++i]);
                    UpdatesAllowed = false;
                }
                else if (e.Args[i] == "--smoke-screenshot" && i + 1 < e.Args.Length) SmokeScreenshot = Path.GetFullPath(e.Args[++i]);
                else if (e.Args[i] == "--smoke-import-file" && i + 1 < e.Args.Length) SmokeImportFile = Path.GetFullPath(e.Args[++i]);
                else throw new ArgumentException($"Unknown or incomplete option: {e.Args[i]}");
            }
            if (SmokeScreenshot is not null && !e.Args.Contains("--data-dir"))
                throw new ArgumentException("Screenshot smoke mode requires an isolated --data-dir.");
            if (SmokeImportFile is not null && SmokeScreenshot is null)
                throw new ArgumentException("A smoke import fixture requires --smoke-screenshot and an isolated --data-dir.");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))[..20];
            _instance = new Mutex(true, @"Local\Nstrap-" + key, out bool first);
            if (!first)
            {
                MessageBox.Show("N strap is already running for this data folder.", "N strap", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            DispatcherUnhandledException += OnUnhandledException;
            MainWindow = new MainWindow(new MainViewModel(dataDirectory,
                SmokeScreenshot is not null ? Path.Combine(dataDirectory, "smoke-global-settings.xml") : null));
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            if (SmokeScreenshot is not null)
                File.WriteAllText(SmokeScreenshot + ".error.txt", ex.ToString());
            else if (UpdateRestart is null || UpdateRolledBack)
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
