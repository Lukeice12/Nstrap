using System.ComponentModel;
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
    public DownloadArchiveProof[]? DownloadProofs { get; set; }
}

/// <summary>
/// An armed shortcut has one authenticated, windowless helper waiting for an
/// explicit in-memory commit AND the parent's exit. Ordinary exit/disarming
/// only removes this helper's own staging job, never app data or the portable EXE.
/// </summary>
internal sealed class RemovalSession : IDisposable
{
    internal static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nstrap");
    private static string Root => Path.Combine(DataRoot, "removal");
    private readonly EventWaitHandle _commit;
    private readonly EventWaitHandle _cancel;
    private readonly Process _process;
    private bool _disposed;
    public bool IsReady => !_disposed && !_process.HasExited;
    public string DownloadStatus { get; }

    private RemovalSession(EventWaitHandle commit, EventWaitHandle cancel, Process process, string downloadStatus)
    {
        _commit = commit; _cancel = cancel; _process = process;
        DownloadStatus = downloadStatus;
    }

    public static async Task<RemovalSession> PrepareAsync()
    {
        UpdateHandoff.RequireInstallable();
        string target = Environment.ProcessPath!;
        RequireTarget(target);
        // The optional GitHub lookup ends before a helper is started. Activation
        // never performs network discovery or expands the prepared set of files.
        var downloadProofs = await DownloadRemoval.ReadProofsAsync().ConfigureAwait(false);
        string id = Guid.NewGuid().ToString("N"), token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string directory = DirectoryFor(id), helperName = Path.GetFileName(target), helper = Path.Combine(directory, helperName);
        UpdateHandoff.CreatePrivateDirectory(Root);
        UpdateHandoff.CreatePrivateDirectory(directory);
        EventWaitHandle? commit = null, cancel = null, ready = null;
        Process? process = null;
        string hash = "";
        try
        {
            commit = CreateEvent(id, token, "commit");
            cancel = CreateEvent(id, token, "cancel");
            ready = CreateEvent(id, token, "ready");
            using var parent = Process.GetCurrentProcess();
            hash = UpdateHandoff.Hash(target);
            var job = new RemovalJob
            {
                Token = token, Target = target, Hash = hash,
                ParentId = parent.Id, ParentStartedUtc = parent.StartTime.ToUniversalTime().Ticks,
                DownloadProofs = downloadProofs
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
            while (timer.Elapsed < TimeSpan.FromSeconds(downloadProofs is { Length: > 0 } ? 20 : 15))
            {
                if (process.HasExited) throw new IOException(PreparationError(process.ExitCode));
                if (ready.WaitOne(0)) return new(commit, cancel, process, ReadDownloadStatus(directory));
                await Task.Delay(50).ConfigureAwait(false);
            }
            throw new IOException("The removal helper did not become ready. The nuke shortcut remains disabled.");
        }
        catch
        {
            cancel?.Set();
            // A started helper owns its cleanup, after it has released all pins.
            // A helper that already exited may have failed before authenticating
            // the job; the parent can still clean its own exact staged copy.
            if ((process is null || process.HasExited) && UpdateHandoff.IsHash(hash))
                RemovalFiles.DeleteOwnJob(directory, hash, helperName);
            process?.Dispose(); commit?.Dispose(); cancel?.Dispose();
            throw;
        }
        finally { ready?.Dispose(); }
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
        string? ownDirectory = null, ownHash = null, ownHelperName = null;
        Mutex? instance = null;
        DownloadRemoval? downloads = null;
        bool removalStarted = false;
        PreparationStage stage = PreparationStage.Startup;
        try
        {
            UpdateHandoff.RequireUnelevated();
            if (!UpdateHandoff.IsHash(token)) return FailureCode(stage);
            stage = PreparationStage.Staging;
            string directory = DirectoryFor(id), helperName = Path.GetFileName(Environment.ProcessPath)!;
            string helper = Path.Combine(directory, helperName);
            if (!AppIdentity.IsExecutableName(helperName)
                || !string.Equals(Environment.ProcessPath, helper, StringComparison.OrdinalIgnoreCase)) return FailureCode(stage);
            UpdateHandoff.RequirePrivateDirectory(Root);
            UpdateHandoff.RequirePrivateDirectory(directory);
            stage = PreparationStage.Job;
            string jobPath = Path.Combine(directory, "job.json");
            UpdateHandoff.RequireRegularPath(jobPath);
            if (new FileInfo(jobPath).Length > 16 * 1024) return FailureCode(stage);
            var job = JsonSerializer.Deserialize<RemovalJob>(File.ReadAllBytes(jobPath));
            if (job is null || job.Token != token || !UpdateHandoff.IsHash(job.Hash)
                || job.ParentId <= 0 || job.ParentStartedUtc <= 0) return FailureCode(stage);
            stage = PreparationStage.Target;
            RequireTarget(job.Target);
            stage = PreparationStage.Helper;
            if (!string.Equals(helperName, Path.GetFileName(job.Target), StringComparison.OrdinalIgnoreCase)
                || !UpdateHandoff.HashMatches(helper, job.Hash)) return FailureCode(stage);
            ownDirectory = directory; ownHash = job.Hash; ownHelperName = helperName;
            stage = PreparationStage.Target;
            GitHubUpdates.ValidateExecutable(job.Target, GitHubUpdates.CurrentVersion);
            stage = PreparationStage.Parent;
            using var parent = Process.GetProcessById(job.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != job.ParentStartedUtc
                || !string.Equals(parent.MainModule?.FileName, job.Target, StringComparison.OrdinalIgnoreCase)) return FailureCode(stage);
            stage = PreparationStage.Signals;
            using var commit = EventWaitHandle.OpenExisting(EventName(id, token, "commit"));
            using var cancel = EventWaitHandle.OpenExisting(EventName(id, token, "cancel"));
            using var ready = EventWaitHandle.OpenExisting(EventName(id, token, "ready"));
            // Keep the existing singleton object alive across parent exit. App
            // startup gates on createdNew, so a new launch in this session
            // cannot begin writing profiles while their removal is in progress.
            stage = PreparationStage.Instance;
            instance = Mutex.OpenExisting(App.InstanceMutexName(DataRoot));
            using (var targetParents = new RemovalFiles.Pins())
            {
                stage = PreparationStage.TargetDirectory;
                targetParents.AddAncestors(Path.GetDirectoryName(job.Target)!);
                stage = PreparationStage.TargetFile;
                using var preparedTarget = new RemovalFiles.PreparedExecutable(job.Target, job.Hash);
                downloads = DownloadRemoval.Prepare(job.DownloadProofs, job.Target);
                AtomicFile.WriteJson(Path.Combine(directory, "downloads.json"), downloads.Status);
                stage = PreparationStage.Signals;
                using var parentExited = new ProcessExitWaitHandle(parent);
                ready.Set();
                // One kernel wait: an armed helper consumes no polling timer.
                if (WaitHandle.WaitAny([cancel, commit, parentExited]) != 1) return 0;
                // The helper never deletes live app files. The exact verified
                // process handle prevents PID reuse from satisfying this wait.
                stage = PreparationStage.ParentExit;
                if (!parent.WaitForExit(15_000)) return FailureCode(stage);
                if (cancel.WaitOne(0)) return 0;
                // Keep the original read pin alive while obtaining DELETE on
                // the same file. Nothing is removed if identity or access changed.
                stage = PreparationStage.DeletionAccess;
                using var target = preparedTarget.OpenForDeletion();
                removalStarted = true;
                // Registry cleanup is independent of profile/data access. The
                // authenticated target is the only Run command this may remove.
                try { StartupRegistration.RemoveOwned(job.Target); }
                catch { /* An inaccessible or externally changed entry remains. */ }
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
                downloads.DeleteFiles();
                RemovalFiles.DeletePinned(target);
            }
            // No-delete-sharing ancestor pins have now been released. Each
            // eligible folder still has its own continuous identity read pin.
            downloads?.DeleteEmptyDirectories();
            return 0;
        }
        catch (Exception error) { return FailureCode(stage, error); }
        finally
        {
            downloads?.Dispose();
            if (ownDirectory is not null && ownHash is not null && ownHelperName is not null)
            {
                RemovalFiles.DeleteOwnJob(ownDirectory, ownHash, ownHelperName);
                RemovalFiles.RemoveEmptyDirectory(Root);
                if (removalStarted) RemovalFiles.RemoveEmptyDirectory(DataRoot);
            }
            instance?.Dispose();
        }
    }

    private static string ReadDownloadStatus(string directory)
    {
        const string unavailable = "Downloads status is unavailable; unverified files will be kept.";
        try
        {
            string path = Path.Combine(directory, "downloads.json");
            UpdateHandoff.RequireRegularPath(path);
            if (new FileInfo(path).Length > 2048) return unavailable;
            string? status = JsonSerializer.Deserialize<string>(File.ReadAllBytes(path));
            return status is { Length: > 0 and <= 400 } ? status : unavailable;
        }
        catch { return unavailable; }
    }

    // The process exit code is the diagnostic channel. Only fixed stage IDs and
    // native error numbers cross it; no tokens, paths or exception text persist.
    private enum PreparationStage
    {
        Startup = 1, Staging, Job, Helper, Target, Parent, Signals, Instance,
        TargetDirectory, TargetFile, ParentExit, DeletionAccess
    }
    private const int FailurePrefix = 0x4E000000;
    private static int FailureCode(PreparationStage stage, Exception? error = null)
    {
        int native = error is Win32Exception win32 ? win32.NativeErrorCode
            : error is not null && (error.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
                ? error.HResult & 0xFFFF : 0;
        return FailurePrefix | ((int)stage << 16) | (native & 0xFFFF);
    }

    private static string PreparationError(int exitCode)
    {
        if ((exitCode & unchecked((int)0xFF000000)) != FailurePrefix)
            return $"The removal helper exited before it became ready (exit 0x{exitCode:X8}). "
                + "Check Windows Security protection history and access to your local app-data folder. The nuke shortcut remains disabled.";
        var stage = (PreparationStage)((exitCode >> 16) & 0xFF);
        string action = stage switch
        {
            PreparationStage.Startup => "start without administrator privileges",
            PreparationStage.Staging => "validate its private staging folder",
            PreparationStage.Job => "validate its preparation record",
            PreparationStage.Helper => "verify its copied executable",
            PreparationStage.Target => "validate the portable executable",
            PreparationStage.Parent => "authenticate the running application",
            PreparationStage.Signals => "open its preparation signals",
            PreparationStage.Instance => "retain the application's instance lock",
            PreparationStage.TargetDirectory => "lock the portable executable's folder",
            PreparationStage.TargetFile => "read and identify the portable executable",
            _ => "complete preparation"
        };
        int native = exitCode & 0xFFFF;
        string advice = native switch
        {
            5 => "Windows denied access. Use an extracted copy in a writable local folder and check Windows Security protection history.",
            32 or 33 => "Another program is holding a conflicting file lock. Close other copies or tools using this executable, then enable the shortcut again.",
            2 or 3 => "A required file or folder disappeared. Extract a fresh portable copy and enable the shortcut again.",
            1 or 50 or 87 when stage is PreparationStage.TargetFile or PreparationStage.TargetDirectory =>
                "Windows could not perform a required file identity or locking operation on this drive. Extract the app onto a local NTFS drive.",
            _ => stage == PreparationStage.Startup
                ? "Restart N strap normally, without Run as administrator."
                : "Restart the app from an extracted portable copy in a regular local folder."
        };
        return $"The removal helper could not {action}. {advice} "
            + $"The nuke shortcut remains disabled. [removal:{stage}:{native}]";
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
