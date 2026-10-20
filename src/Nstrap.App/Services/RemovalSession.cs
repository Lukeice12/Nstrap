using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Nstrap.Core;

namespace Nstrap.App.Services;

internal sealed class RemovalJob
{
    public RemovalJob() { }
    public string Token { get; set; } = "";
    public string Target { get; set; } = "";
    public string Hash { get; set; } = "";
    public int ParentId { get; set; }
    public long ParentStartedUtc { get; set; }
}

/// <summary>
/// An armed shortcut has one authenticated, windowless helper waiting for an
/// explicit in-memory commit AND the parent's exit. Ordinary exit/disarming
/// only removes this helper's own staging job, never app data or the portable EXE.
/// </summary>
internal sealed class RemovalSession : IDisposable
{
    internal const string HelperName = "Nstrap.Remove.exe";
    internal static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nstrap");
    private static string Root => Path.Combine(DataRoot, "removal");
    private readonly EventWaitHandle _commit;
    private readonly EventWaitHandle _cancel;
    private readonly Process _process;
    private bool _disposed;
    public bool IsReady => !_disposed && !_process.HasExited;

    private RemovalSession(EventWaitHandle commit, EventWaitHandle cancel, Process process)
    {
        _commit = commit; _cancel = cancel; _process = process;
    }

    public static async Task<RemovalSession> PrepareAsync()
    {
        UpdateHandoff.RequireInstallable();
        string target = Environment.ProcessPath!;
        RequireTarget(target);
        string id = Guid.NewGuid().ToString("N"), token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string directory = DirectoryFor(id), helper = Path.Combine(directory, HelperName);
        UpdateHandoff.CreatePrivateDirectory(Root);
        UpdateHandoff.CreatePrivateDirectory(directory);
        var commit = CreateEvent(id, token, "commit");
        var cancel = CreateEvent(id, token, "cancel");
        using var ready = CreateEvent(id, token, "ready");
        Process? process = null;
        string hash = "";
        try
        {
            using var parent = Process.GetCurrentProcess();
            hash = UpdateHandoff.Hash(target);
            var job = new RemovalJob
            {
                Token = token, Target = target, Hash = hash,
                ParentId = parent.Id, ParentStartedUtc = parent.StartTime.ToUniversalTime().Ticks
            };
            File.Copy(target, helper, false);
            if (!UpdateHandoff.HashMatches(helper, hash)) throw new IOException("The removal helper copy changed.");
            AtomicFile.WriteJson(Path.Combine(directory, "job.json"), job);
            var start = new ProcessStartInfo(helper)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory
            };
            // This helper's extracted runtime belongs to its exact private job.
            // The main app's shared/global runtime cache is never swept.
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(directory, "runtime");
            start.ArgumentList.Add("--removal-helper");
            start.ArgumentList.Add(id);
            start.ArgumentList.Add(token);
            process = Process.Start(start) ?? throw new IOException("Windows did not start the removal helper.");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (process.HasExited) throw new IOException("The removal helper could not safely prepare. Check file permissions.");
                if (ready.WaitOne(0)) return new(commit, cancel, process);
                await Task.Delay(50).ConfigureAwait(false);
            }
            throw new IOException("The removal helper did not become ready. The nuke shortcut remains disabled.");
        }
        catch
        {
            cancel.Set();
            // A started helper owns its cleanup, after it has released all pins.
            // Before launch, only our newly created exact job may be removed.
            if (process is null && UpdateHandoff.IsHash(hash)) RemovalFiles.DeleteOwnJob(directory, hash);
            process?.Dispose(); commit.Dispose(); cancel.Dispose();
            throw;
        }
    }

    public void Commit() { if (IsReady) _commit.Set(); }
    public void Cancel() { if (!_disposed) _cancel.Set(); }
    public async Task WaitForExitAsync()
    {
        if (_disposed) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _process.Dispose(); _commit.Dispose(); _cancel.Dispose();
    }

    internal static Task<int> RunHelperAsync(string id, string token) =>
        Task.Run(() => RunHelper(id, token));

    private static int RunHelper(string id, string token)
    {
        string? ownDirectory = null, ownHash = null;
        Mutex? instance = null;
        bool removalStarted = false;
        try
        {
            UpdateHandoff.RequireUnelevated();
            if (!UpdateHandoff.IsHash(token)) return 1;
            string directory = DirectoryFor(id), helper = Path.Combine(directory, HelperName);
            if (!string.Equals(Environment.ProcessPath, helper, StringComparison.OrdinalIgnoreCase)) return 1;
            UpdateHandoff.RequirePrivateDirectory(Root);
            UpdateHandoff.RequirePrivateDirectory(directory);
            string jobPath = Path.Combine(directory, "job.json");
            UpdateHandoff.RequireRegularPath(jobPath);
            if (new FileInfo(jobPath).Length > 16 * 1024) return 1;
            var job = JsonSerializer.Deserialize<RemovalJob>(File.ReadAllBytes(jobPath));
            if (job is null || job.Token != token || !UpdateHandoff.IsHash(job.Hash)
                || job.ParentId <= 0 || job.ParentStartedUtc <= 0) return 1;
            RequireTarget(job.Target);
            if (!UpdateHandoff.HashMatches(helper, job.Hash)) return 1;
            GitHubUpdates.ValidateExecutable(job.Target, GitHubUpdates.CurrentVersion);
            using var parent = Process.GetProcessById(job.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != job.ParentStartedUtc
                || !string.Equals(parent.MainModule?.FileName, job.Target, StringComparison.OrdinalIgnoreCase)) return 1;
            using var commit = EventWaitHandle.OpenExisting(EventName(id, token, "commit"));
            using var cancel = EventWaitHandle.OpenExisting(EventName(id, token, "cancel"));
            using var ready = EventWaitHandle.OpenExisting(EventName(id, token, "ready"));
            // Keep the existing singleton object alive across parent exit. App
            // startup gates on createdNew, so a new launch in this session
            // cannot begin writing profiles while their removal is in progress.
            instance = Mutex.OpenExisting(App.InstanceMutexName(DataRoot));
            ownDirectory = directory; ownHash = job.Hash;
            using (var targetParents = new RemovalFiles.Pins())
            {
                targetParents.AddAncestors(Path.GetDirectoryName(job.Target)!);
                using var target = RemovalFiles.PinExecutable(job.Target, job.Hash);
                using var parentExited = new ProcessExitWaitHandle(parent);
                ready.Set();
                // One kernel wait: an armed helper consumes no polling timer.
                if (WaitHandle.WaitAny([cancel, commit, parentExited]) != 1) return 0;
                // The helper never deletes live app files. The exact verified
                // process handle prevents PID reuse from satisfying this wait.
                if (!parent.WaitForExit(15_000)) return 1;
                if (cancel.WaitOne(0)) return 0;
                removalStarted = true;
                try
                {
                    using var dataParents = new RemovalFiles.Pins();
                    dataParents.AddAncestors(DataRoot);
                    EmergencyRestore.TryRestoreOwnedSettings(DataRoot);
                    UpdateHandoff.RemoveCompletedCopiesFor(job.Target);
                }
                catch { /* Missing or redirected data cannot broaden restoration. */ }
                try { RemovalFiles.DeleteData(DataRoot, directory); }
                catch { /* Data cleanup is best effort; the exact EXE is still pinned. */ }
                RemovalFiles.DeletePinned(target);
            }
            return 0;
        }
        catch { return 1; } // Helper mode never displays errors or enters normal startup.
        finally
        {
            if (ownDirectory is not null && ownHash is not null)
            {
                RemovalFiles.DeleteOwnJob(ownDirectory, ownHash);
                RemovalFiles.RemoveEmptyDirectory(Root);
                if (removalStarted) RemovalFiles.RemoveEmptyDirectory(DataRoot);
            }
            instance?.Dispose();
        }
    }

    private sealed class ProcessExitWaitHandle : WaitHandle
    {
        public ProcessExitWaitHandle(Process parent) =>
            SafeWaitHandle = new SafeWaitHandle(parent.Handle, ownsHandle: false);
    }

    private static string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid removal job.");
        string directory = Path.Combine(Root, id);
        UpdateHandoff.RequireRegularPath(directory);
        return directory;
    }
    private static string EventName(string id, string token, string kind) => @"Local\Nstrap-Removal-" + id + "-" + token + "-" + kind;
    private static EventWaitHandle CreateEvent(string id, string token, string kind)
    {
        var signal = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(id, token, kind), out bool created);
        if (created) return signal;
        signal.Dispose();
        throw new IOException("A removal preparation signal already exists.");
    }
    private static void RequireTarget(string target)
    {
        UpdateHandoff.RequireRegularPath(target);
        UpdateHandoff.RequireRegularPath(DataRoot);
        if (!string.Equals(target, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)
            || !AppIdentity.IsExecutableName(Path.GetFileName(target))
            || target.StartsWith(DataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || target.Split(Path.DirectorySeparatorChar).Any(part =>
                part.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || part.Equals(".tools", StringComparison.OrdinalIgnoreCase)
                || part.Equals(".codex", StringComparison.OrdinalIgnoreCase)
                || part.Equals(".agents", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Removal requires an extracted portable executable outside development and application data folders.");
    }
}
