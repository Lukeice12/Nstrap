using System.IO;
using System.Runtime.InteropServices;

namespace Nstrap.App.Services;

/// <summary>
/// Emergency exit runs on the hotkey thread and bypasses WPF's closing, save and
/// error-dialog paths. Handoff cancellation must precede process termination:
/// otherwise an updater could interpret the exit as permission to relaunch.
/// </summary>
internal static class EmergencyActions
{
    private static readonly object Gate = new();
    private static readonly List<EventWaitHandle> RestartSignals = [];
    private static readonly List<RegisteredWaitHandle> RestartWatches = [];
    private static bool _exiting;
    private static int _restartPending;
    private static bool _preparingNuke;
    private static int _preparationVersion;
    private static RemovalSession? _removal;
    private static UpdateTicket? _updateRestart;
    private static bool _updateRollback;
    private static readonly SemaphoreSlim PreparationGate = new(1, 1);

    public const string ScopeDescription =
        "Removes this portable executable and this Windows account's Nstrap data, including profiles, icons, backups and update cache. "
        + "It first attempts to restore settings still owned by Nstrap when Roblox is closed; external edits are preserved. "
        + "Windows history, logs, restore points and copies elsewhere are outside its scope. "
        + "Locked, protected or redirected files and shared .NET runtime cache can remain.";

    public static bool IsNukePrepared
    {
        get { lock (Gate) return _removal is { IsReady: true }; }
    }

    // Events are deliberately retained for the process lifetime, including in
    // the resumed app. Killing that app must also cancel its waiting updater's
    // rollback path. An absent event makes a stale restart invocation fail.
    internal static RestartPreparation CreateRestartGuard(string token)
    {
        lock (Gate)
        {
            if (_exiting || _preparingNuke || _removal is not null)
                throw new InvalidOperationException("Disable the nuke shortcut before updating or renaming N strap.");
            var signal = new EventWaitHandle(false, EventResetMode.ManualReset, RestartEventName(token), out bool created);
            if (!created)
            {
                signal.Dispose();
                throw new IOException("A restart cancellation signal already exists.");
            }
            RestartSignals.Add(signal);
            _restartPending++;
            return new RestartPreparation(signal);
        }
    }

    internal sealed class RestartPreparation(EventWaitHandle signal) : IDisposable
    {
        private bool _completed;
        public void Complete() => _completed = true;
        public void Dispose()
        {
            lock (Gate)
            {
                if (!_completed) signal.Set();
                _restartPending--;
            }
        }
    }

    internal static void WatchRestartGuard(string token)
    {
        if (!EventWaitHandle.TryOpenExisting(RestartEventName(token), out var signal))
        {
            // Parent exit can race the first instruction of a newly launched
            // helper. A vanished guard is cancellation, never normal startup.
            TerminateCurrentProcess();
            return;
        }
        lock (Gate)
        {
            if (_exiting || signal.WaitOne(0))
            {
                signal.Dispose();
                TerminateCurrentProcess();
                return;
            }
            RestartSignals.Add(signal);
            RestartWatches.Add(ThreadPool.RegisterWaitForSingleObject(signal, static (_, _) => KillSwitch(),
                null, Timeout.Infinite, executeOnlyOnce: true));
        }
    }

    private static string RestartEventName(string token)
    {
        if (!UpdateHandoff.IsHash(token)) throw new ArgumentException("Invalid restart cancellation token.");
        return @"Local\Nstrap-Restart-" + token;
    }

    internal static void TrackUpdateRestart(UpdateTicket ticket, bool rollback)
    {
        lock (Gate) { _updateRestart = ticket; _updateRollback = rollback; }
    }

    public static void KillSwitch() => ExitImmediately(remove: false);

    public static void Nuke() => ExitImmediately(remove: true);

    private static void ExitImmediately(bool remove)
    {
        lock (Gate)
        {
            if (_exiting) return;
            _exiting = true;
            foreach (var signal in RestartSignals)
            {
                try { signal.Set(); } catch { /* Emergency exit must remain silent. */ }
            }
            // The helper was authenticated and prepared during explicit arming.
            // This path neither reads settings nor accepts filesystem paths.
            try
            {
                if (remove) _removal?.Commit();
                else _removal?.Cancel();
            }
            catch { /* A failed removal signal still closes the app. */ }
        }
        TerminateCurrentProcess();
    }

    private static void TerminateCurrentProcess()
    {
        // TerminateProcess on the current process does not return or run DLL /
        // managed shutdown handlers. This also works while the WPF thread is
        // blocked. Unsaved work is intentionally abandoned by this emergency action.
        TerminateProcess(GetCurrentProcess(), 0);
        Environment.Exit(0); // Defensive fallback if the native call fails.
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    public static async Task PrepareNukeAsync()
    {
        await PreparationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            int version;
            UpdateTicket? updateRestart;
            bool rollback;
            lock (Gate)
            {
                if (_exiting) throw new InvalidOperationException("N strap is closing.");
                if (_removal is { IsReady: true }) return;
                if (_restartPending != 0)
                    throw new InvalidOperationException("A restart handoff is in progress. Wait for it to finish before arming the nuke shortcut.");
                _preparingNuke = true;
                version = _preparationVersion;
                updateRestart = _updateRestart;
                rollback = _updateRollback;
            }
            if (updateRestart is not null)
                await UpdateHandoff.AwaitRemovalReadyAsync(updateRestart, rollback).ConfigureAwait(false);
            var prepared = await Task.Run(RemovalSession.PrepareAsync).ConfigureAwait(false);
            lock (Gate)
            {
                if (_exiting || version != _preparationVersion)
                {
                    prepared.Cancel();
                    prepared.Dispose();
                    throw new OperationCanceledException("The nuke shortcut was disarmed while preparing.");
                }
                _removal?.Dispose();
                _removal = prepared;
            }
        }
        finally
        {
            lock (Gate) _preparingNuke = false;
            PreparationGate.Release();
        }
    }

    public static void DisarmNuke()
    {
        lock (Gate)
        {
            if (_exiting) return;
            _preparationVersion++;
            _removal?.Cancel();
            _removal?.Dispose();
            _removal = null;
        }
    }

    public static async Task DisarmNukeForRestartAsync()
    {
        await PreparationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            RemovalSession? removal;
            lock (Gate)
            {
                if (_exiting) return;
                _preparationVersion++;
                removal = _removal;
                _removal = null;
                removal?.Cancel();
            }
            if (removal is null) return;
            try { await removal.WaitForExitAsync().ConfigureAwait(false); }
            finally { removal.Dispose(); }
        }
        finally { PreparationGate.Release(); }
    }
}
