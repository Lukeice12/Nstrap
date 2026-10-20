using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Nstrap.Core;

namespace Nstrap.App.Services;

internal sealed record IdentityTicket(string Id, string Token);

internal sealed class IdentityJob
{
    public string Token { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Hash { get; set; } = "";
    public int ParentId { get; set; }
    public long ParentStartedUtc { get; set; }
    public bool? StartHidden { get; set; }
}

/// <summary>
/// The desired executable is also the restart participant. It waits for its
/// authenticated parent to exit, then enters normal startup. No PE resources
/// are modified and the original remains until settings and the window load.
/// These jobs authorize only same-user, unelevated, same-folder renames.
/// </summary>
internal static class AppIdentity
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nstrap", "identity");
    private static readonly string[] JobFiles = ["job.json", "ready", "commit", "result"];
    public static string CurrentProcessName => Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Nstrap";

    public static string NormalizeProcessName(string? value)
    {
        string name = (value ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length is < 1 or > 64 || name[0] == '.' || name.EndsWith('.') || name.EndsWith(' ')
            || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.Surrogate))
            throw new ArgumentException("Use a filename of 1–64 characters, without a folder path, control characters, or a leading or trailing dot. The .exe extension is added automatically.");
        string device = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$"
            || (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(device[3])))
            throw new ArgumentException("Windows reserves this filename. Choose a different process name.");
        return name;
    }

    internal static bool IsExecutableName(string? name)
    {
        if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        try { return string.Equals(NormalizeProcessName(name) + ".exe", name, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    public static bool NameIsCurrent(string name) =>
        string.Equals(NormalizeProcessName(name), CurrentProcessName, StringComparison.OrdinalIgnoreCase);

    public static async Task StartRenameAsync(string name, bool startHidden = false)
    {
        string normalized = NormalizeProcessName(name);
        if (NameIsCurrent(normalized)) throw new InvalidOperationException("This executable already uses that process name.");
        UpdateHandoff.RequireInstallable();
        string source = Environment.ProcessPath!;
        string target = Path.Combine(Path.GetDirectoryName(source)!, normalized + ".exe");
        RequireApplicationPath(source);
        RequireApplicationPath(target);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException("That filename already exists in the app folder. Choose another name; existing files are never replaced.");

        UpdateHandoff.CreatePrivateDirectory(Root);
        CleanCompletedJobs();
        var ticket = new IdentityTicket(Guid.NewGuid().ToString("N"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        using var restart = EmergencyActions.CreateRestartGuard(ticket.Token);
        string directory = JobDirectory(ticket);
        UpdateHandoff.CreatePrivateDirectory(directory);
        bool copied = false;
        try
        {
            // Deny source replacement during hashing, copying and the handshake.
            using var sourceFile = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var parent = Process.GetCurrentProcess();
            var job = new IdentityJob
            {
                Token = ticket.Token, Source = source, Target = target,
                Hash = Convert.ToHexString(SHA256.HashData(sourceFile)),
                ParentId = parent.Id, ParentStartedUtc = parent.StartTime.ToUniversalTime().Ticks,
                StartHidden = startHidden
            };
            AtomicFile.WriteJson(Path.Combine(directory, "job.json"), job);
            // No overwrite even when a pre-existing target contains identical
            // bytes. Use the Windows file copy so file attributes are preserved.
            File.Copy(source, target, false);
            copied = true;
            UpdateHandoff.RequireRegularPath(target);
            using var targetFile = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(targetFile)) != job.Hash)
                throw new IOException("The renamed copy failed verification.");
            var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
            start.ArgumentList.Add("--identity-restart");
            start.ArgumentList.Add(ticket.Id);
            start.ArgumentList.Add(ticket.Token);
            using var process = Process.Start(start) ?? throw new IOException("Windows did not start the renamed executable.");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(12))
            {
                if (ReadMarker(directory, "ready") == ticket.Token)
                {
                    if (process.HasExited) throw new IOException("The renamed executable exited before the handoff.");
                    WriteMarker(directory, "commit", ticket.Token);
                    restart.Complete();
                    return; // The caller now shuts down; it must not start another operation.
                }
                if (process.HasExited) throw new IOException("The renamed executable could not prepare its restart.");
                await Task.Delay(100);
            }
            throw new IOException("The renamed executable did not become ready. The current app will remain open.");
        }
        catch (Exception ex)
        {
            try { WriteMarker(directory, "result", "Not completed."); } catch { }
            // An uncertain or running copy is never killed or deleted. The child
            // requires commit AND parent exit and has a bounded wait.
            throw new IOException($"Process rename did not complete. The original is preserved at {source}."
                + (copied ? $" A copy remains at {target}; close it before removing it or trying another name."
                    : $" Check {target} for a partial copy before trying this name again.")
                + "\n\n" + ex.Message, ex);
        }
    }

    public static async Task AwaitRestartAsync(IdentityTicket ticket)
    {
        UpdateHandoff.RequireInstallable();
        var job = LoadJob(ticket);
        RequireCurrentTarget(job);
        string directory = JobDirectory(ticket);
        if (ReadMarker(directory, "result") is not null)
            throw new IOException("This process rename has already ended. Open the original executable to try again.");
        using var parent = Process.GetProcessById(job.ParentId);
        if (parent.StartTime.ToUniversalTime().Ticks != job.ParentStartedUtc
            || !string.Equals(parent.MainModule?.FileName, job.Source, StringComparison.OrdinalIgnoreCase)
            || !UpdateHandoff.HashMatches(job.Source, job.Hash))
            throw new IOException("The process rename's original application no longer matches.");
        WriteMarker(directory, "ready", ticket.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await parent.WaitForExitAsync(timeout.Token);
        if (ReadMarker(directory, "commit") != ticket.Token || ReadMarker(directory, "result") is not null)
            throw new IOException("The original application did not complete the process rename handoff.");
        RequireCurrentTarget(job);
        // Normal startup now owns the single-instance mutex. The old executable
        // still exists if loading profiles or creating the window fails.
    }

    public static void CompleteRestart(IdentityTicket ticket)
    {
        UpdateHandoff.RequireUnelevated();
        var job = LoadJob(ticket);
        RequireCurrentTarget(job);
        string directory = JobDirectory(ticket);
        if (ReadMarker(directory, "commit") != ticket.Token || ReadMarker(directory, "result") is not null)
            throw new IOException("This process rename is not awaiting completion.");
        try
        {
            using var parent = Process.GetProcessById(job.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks == job.ParentStartedUtc && !parent.HasExited)
                throw new IOException("The original application is still running; its executable was preserved.");
        }
        catch (ArgumentException) { /* The original process is no longer present. */ }

        try
        {
            // Only the old exact Run command may follow this authenticated
            // rename. If repair fails, preserve the original EXE so its existing
            // sign-in command is not broken. Windows' disabled flag is untouched.
            StartupRegistration.RepairAfterRename(job.Source, job.Target);
            // Verify and delete using ONE locked handle: a pathname cannot be
            // swapped between the hash check and deletion. No source tree scan.
            DeleteOriginal(job);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            try { WriteMarker(directory, "result", "Renamed; original preserved."); } catch { }
            throw new IOException($"The new process name is active. Automatic cleanup stopped at {job.Source}; any remaining file was preserved. {ex.Message}", ex);
        }
        try { WriteMarker(directory, "result", "Completed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The new process name is active and the old executable was removed, but the rename status could not be saved. " + ex.Message, ex);
        }
    }

    internal static bool? GetRestartStartHidden(IdentityTicket ticket)
    {
        var job = LoadJob(ticket);
        RequireCurrentTarget(job);
        return job.StartHidden;
    }

    private static void RequireCurrentTarget(IdentityJob job)
    {
        if (!string.Equals(Environment.ProcessPath, job.Target, StringComparison.OrdinalIgnoreCase)
            || !UpdateHandoff.HashMatches(job.Target, job.Hash))
            throw new IOException("This process rename does not match the running executable.");
    }

    private static void RequireApplicationPath(string path)
    {
        UpdateHandoff.RequireRegularPath(path);
        if (!Path.IsPathFullyQualified(path)
            || !string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
            || !IsExecutableName(Path.GetFileName(path))
            || path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(UpdateHandoff.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The process rename must use an ordinary executable in the app's local folder.");
    }

    private static string JobDirectory(IdentityTicket ticket)
    {
        if (!Guid.TryParseExact(ticket.Id, "N", out _) || !UpdateHandoff.IsHash(ticket.Token))
            throw new ArgumentException("Invalid process rename identifier.");
        string directory = Path.Combine(Root, ticket.Id);
        UpdateHandoff.RequireRegularPath(directory);
        return directory;
    }

    private static IdentityJob LoadJob(IdentityTicket ticket)
    {
        string directory = JobDirectory(ticket), path = Path.Combine(directory, "job.json");
        UpdateHandoff.RequirePrivateDirectory(Root);
        UpdateHandoff.RequirePrivateDirectory(directory);
        UpdateHandoff.RequireRegularPath(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024)
            throw new IOException("The process rename record is missing or too large.");
        var job = JsonSerializer.Deserialize<IdentityJob>(File.ReadAllBytes(path))
            ?? throw new IOException("Invalid process rename record.");
        if (job.Token != ticket.Token || !UpdateHandoff.IsHash(job.Hash) || job.ParentId <= 0 || job.ParentStartedUtc <= 0
            || string.IsNullOrEmpty(job.Source) || string.IsNullOrEmpty(job.Target))
            throw new IOException("The process rename has unexpected fields.");
        RequireApplicationPath(job.Source);
        RequireApplicationPath(job.Target);
        if (string.Equals(job.Source, job.Target, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(job.Source), Path.GetDirectoryName(job.Target), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The process rename cannot move the app to another folder.");
        foreach (string file in JobFiles) UpdateHandoff.RequireRegularPath(Path.Combine(directory, file));
        return job;
    }

    private static string? ReadMarker(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        UpdateHandoff.RequireRegularPath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4096) throw new IOException("A process rename status record is invalid.");
        return File.ReadAllText(path);
    }

    private static void WriteMarker(string directory, string name, string value)
    {
        string path = Path.Combine(directory, name);
        UpdateHandoff.RequireRegularPath(path);
        AtomicFile.Write(path, Encoding.UTF8.GetBytes(value));
    }

    private static void CleanCompletedJobs()
    {
        // Only small terminal records, never executables or paths from old jobs.
        int cleaned = 0;
        foreach (string directory in Directory.EnumerateDirectories(Root).Take(100))
        {
            if (cleaned >= 5) break;
            try
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)
                    || Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddDays(-1)) continue;
                UpdateHandoff.RequirePrivateDirectory(directory);
                if (ReadMarker(directory, "result") is null) continue;
                foreach (string name in JobFiles)
                {
                    string path = Path.Combine(directory, name);
                    UpdateHandoff.RequireRegularPath(path);
                    if (File.Exists(path)) File.Delete(path);
                }
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
                cleaned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { /* Leave changed, active or inaccessible records in place. */ }
        }
    }

    private static void DeleteOriginal(IdentityJob job)
    {
        UpdateHandoff.RequireRegularPath(job.Source);
        const uint genericRead = 0x80000000, deleteAccess = 0x00010000, openExisting = 3, openReparsePoint = 0x00200000;
        using var handle = CreateFile(job.Source, genericRead | deleteAccess, FileShare.Read, IntPtr.Zero,
            openExisting, openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        UpdateHandoff.RequireRegularPath(job.Source);
        using var stream = new FileStream(handle, FileAccess.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), job.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The original executable changed after the rename was requested.");
        byte delete = 1; // FILE_DISPOSITION_INFO contains a one-byte BOOLEAN.
        if (!SetFileInformationByHandle(handle, 4, ref delete, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref byte information, uint size);
}
