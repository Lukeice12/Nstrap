using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Nstrap.Core;

namespace Nstrap.App.Services;

internal sealed class UpdateJob
{
    public UpdateJob() { }
    public string Token { get; set; } = "";
    public string Target { get; set; } = "";
    public int ParentId { get; set; }
    public long ParentStartedUtc { get; set; }
    public string OldVersion { get; set; } = "";
    public string NewVersion { get; set; } = "";
    public string OldHash { get; set; } = "";
    public string NewHash { get; set; } = "";
}

internal sealed class UpdateStatus
{
    public UpdateStatus() { }
    public string? AttemptedVersion { get; set; }
    public string Message { get; set; } = "";
}

internal sealed record UpdateTicket(string Id, string Token);

/// <summary>
/// Unelevated, same-user portable updates only. No scripts, arbitrary download
/// locations, recursive deletion or changes to profile/Roblox data. The helper
/// owns replacement; normal app startup owns the acknowledgement.
/// </summary>
internal static class UpdateHandoff
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nstrap", "updates");
    private const string HelperName = "Nstrap.Update.exe";
    private static readonly string[] JobFiles = ["package.zip", "update.exe", HelperName, "job.json", "ready", "commit", "ack", "result"];

    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static void RequireRegularPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || full.StartsWith(@"\\", StringComparison.Ordinal)
            || full.Length < 3 || full[1] != ':' || full[2] != '\\' || full[2..].Contains(':'))
            throw new IOException("Updates require an ordinary local drive path.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Updates cannot use symbolic links, junctions, or other redirected paths. Use a regular local folder.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    internal static void RequireUnelevated()
    {
        if (IsElevated())
            throw new InvalidOperationException("Restart N strap normally, without Run as administrator, to install updates or rename the executable.");
    }

    internal static void CreatePrivateDirectory(string path)
    {
        RequireUnelevated();
        RequireRegularPath(path);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Windows could not identify the update owner.");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!Directory.Exists(path)) security.CreateDirectory(path);
        else new DirectoryInfo(path).SetAccessControl(security);
        RequirePrivateDirectory(path);
    }

    internal static void RequirePrivateDirectory(string path)
    {
        RequireRegularPath(path);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Windows could not identify the update owner.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected
            || security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(rule => rule.AccessControlType == AccessControlType.Allow
                    && !rule.IdentityReference.Equals(user) && !rule.IdentityReference.Equals(system)))
            throw new IOException("The update staging folder has unexpected permissions. Download the update manually.");
    }

    private static void ProtectHelperFile(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Windows could not identify the update owner.");
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    public static void RequireInstallable()
    {
        RequireUnelevated();
#pragma warning disable IL3000 // Empty Location intentionally identifies the supported single-file publish.
        if (!Environment.Is64BitProcess || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64
            || typeof(UpdateHandoff).Assembly.Location.Length != 0
            || !AppIdentity.IsExecutableName(Path.GetFileName(Environment.ProcessPath)))
            throw new InvalidOperationException("This action requires the portable Windows x64 N strap executable. Download and extract the ZIP from releases.");
#pragma warning restore IL3000
        string target = Environment.ProcessPath!;
        RequireRegularPath(target);
        RequireRegularPath(Root);
        GitHubUpdates.ValidateExecutable(target, GitHubUpdates.CurrentVersion);
    }

    public static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public static bool HashMatches(string path, string expected) =>
        IsHash(expected) && string.Equals(Hash(path), expected, StringComparison.OrdinalIgnoreCase);

    public static UpdateStatus ReadStatus()
    {
        string path = Path.Combine(Root, "status.json");
        RequireRegularPath(path);
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 16 * 1024) throw new IOException("The update status file is too large. Use the releases page to update manually.");
        var status = JsonSerializer.Deserialize<UpdateStatus>(File.ReadAllBytes(path)) ?? throw new IOException("The update status file is invalid.");
        if (status.Message is null || status.Message.Length > 4096
            || (status.AttemptedVersion is not null && !Version.TryParse(status.AttemptedVersion, out _)))
            throw new IOException("The update status file is invalid.");
        return status;
    }

    private static void WriteStatus(string? attemptedVersion, string message)
    {
        RequireRegularPath(Root);
        Directory.CreateDirectory(Root);
        string path = Path.Combine(Root, "status.json");
        RequireRegularPath(path);
        AtomicFile.WriteJson(path, new UpdateStatus { AttemptedVersion = attemptedVersion, Message = message[..Math.Min(message.Length, 4000)] });
    }

    private static string JobDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid update job identifier.");
        string directory = Path.Combine(Root, id);
        RequireRegularPath(directory);
        return directory;
    }

    private static UpdateJob LoadJob(UpdateTicket ticket)
    {
        if (!IsHash(ticket.Token)) throw new ArgumentException("Invalid update job token.");
        string directory = JobDirectory(ticket.Id), path = Path.Combine(directory, "job.json");
        RequirePrivateDirectory(Root);
        RequirePrivateDirectory(directory);
        RequireRegularPath(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024) throw new IOException("The update job is missing or invalid.");
        var job = JsonSerializer.Deserialize<UpdateJob>(File.ReadAllBytes(path)) ?? throw new IOException("Invalid update job.");
        if (job.Token != ticket.Token || !IsHash(job.OldHash) || !IsHash(job.NewHash)
            || job.ParentId <= 0 || job.ParentStartedUtc <= 0
            || !Version.TryParse(job.OldVersion, out var oldVersion)
            || !Version.TryParse(job.NewVersion, out var newVersion) || newVersion <= oldVersion
            || newVersion.Revision != -1 || oldVersion.Revision != -1
            || !Path.IsPathFullyQualified(job.Target)
            || !AppIdentity.IsExecutableName(Path.GetFileName(job.Target))
            || !string.Equals(job.Target, Path.GetFullPath(job.Target), StringComparison.OrdinalIgnoreCase)
            || job.Target.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The update job has unexpected fields.");
        RequireRegularPath(job.Target);
        foreach (string file in JobFiles) RequireRegularPath(Path.Combine(directory, file));
        return job;
    }

    private static string Candidate(UpdateJob job, UpdateTicket ticket) =>
        Path.Combine(Path.GetDirectoryName(job.Target)!, $".nstrap-update-{ticket.Id}.exe");
    private static string Backup(UpdateJob job, UpdateTicket ticket) =>
        Path.Combine(Path.GetDirectoryName(job.Target)!, $".nstrap-backup-{ticket.Id}.exe");

    public static async Task<UpdateTicket> PrepareAsync(AppRelease release, IProgress<string> progress)
    {
        RequireInstallable();
        if (release.Version <= GitHubUpdates.CurrentVersion) throw new IOException("An update cannot replace N strap with the same or an older version.");
        // Check write access before a large download. No request for elevation.
        string probe = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, $".nstrap-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("N strap cannot update this folder. Move the portable app to a writable local folder, or download the update manually.", ex);
        }
        var ticket = new UpdateTicket(Guid.NewGuid().ToString("N"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        string directory = JobDirectory(ticket.Id);
        CreatePrivateDirectory(Root);
        CreatePrivateDirectory(directory);
        var previousStatus = ReadStatus();
        try
        {
            using var parent = Process.GetCurrentProcess();
            var job = new UpdateJob
            {
                Token = ticket.Token, Target = Environment.ProcessPath!, ParentId = parent.Id,
                ParentStartedUtc = parent.StartTime.ToUniversalTime().Ticks,
                OldVersion = GitHubUpdates.CurrentVersion.ToString(3), NewVersion = release.Version.ToString(3),
                OldHash = Hash(Environment.ProcessPath!)
            };
            progress.Report("Downloading update...");
            await GitHubUpdates.DownloadAsync(release, directory, progress);
            job.NewHash = Hash(Path.Combine(directory, "update.exe"));
            File.Copy(job.Target, Path.Combine(directory, HelperName), false);
            ProtectHelperFile(Path.Combine(directory, HelperName));
            if (!HashMatches(Path.Combine(directory, HelperName), job.OldHash))
                throw new IOException("N strap changed while the update was being prepared. Restart it and try again.");
            AtomicFile.WriteJson(Path.Combine(directory, "job.json"), job);
            return ticket;
        }
        catch (Exception ex)
        {
            // Download errors remain retryable on the next start. Preserve any
            // previous unresolved handoff instead of declaring a new one here.
            WriteStatus(previousStatus.AttemptedVersion, "Update preparation failed. " + FriendlyError(ex));
            WriteMarker(directory, "result", "Preparation failed.");
            throw;
        }
    }

    public static async Task StartHelperAsync(UpdateTicket ticket)
    {
        RequireInstallable();
        var job = LoadJob(ticket);
        if (job.Target != Environment.ProcessPath || job.ParentId != Environment.ProcessId
            || !HashMatches(job.Target, job.OldHash))
            throw new IOException("The running application no longer matches this update.");
        string directory = JobDirectory(ticket.Id), helper = Path.Combine(directory, HelperName);
        if (!HashMatches(helper, job.OldHash)) throw new IOException("The update helper changed.");
        using var process = StartProcess(helper, "--update-helper", ticket);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(12))
        {
            if (ReadMarker(directory, "ready") == ticket.Token)
            {
                // Persist before permitting process exit/replacement, so a crash
                // during the handoff cannot produce an automatic restart loop.
                WriteStatus(job.NewVersion, $"The last update to {job.NewVersion} did not complete. Check for updates to retry.");
                WriteMarker(directory, "commit", ticket.Token);
                return;
            }
            if (process.HasExited) throw new IOException("The update helper could not prepare the replacement. Check folder permissions or use releases.");
            await Task.Delay(100);
        }
        throw new IOException("The update helper did not become ready in time. N strap will remain open.");
    }

    private static Process StartProcess(string executable, string option, UpdateTicket ticket)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add(option);
        start.ArgumentList.Add(ticket.Id);
        start.ArgumentList.Add(ticket.Token);
        return Process.Start(start) ?? throw new IOException("Windows did not start N strap.");
    }

    public static void ValidateResume(UpdateTicket ticket, bool rollback)
    {
        RequireUnelevated();
        var job = LoadJob(ticket);
        if (!string.Equals(Environment.ProcessPath, job.Target, StringComparison.OrdinalIgnoreCase)
            || !HashMatches(job.Target, rollback ? job.OldHash : job.NewHash)
            || GitHubUpdates.CurrentVersion != Version.Parse(rollback ? job.OldVersion : job.NewVersion))
            throw new IOException("This update restart does not match the installed executable.");
    }

    public static void AcknowledgeStartup(UpdateTicket ticket, bool rollback)
    {
        ValidateResume(ticket, rollback);
        var job = LoadJob(ticket);
        if (!rollback)
        {
            WriteMarker(JobDirectory(ticket.Id), "ack", ticket.Token);
            WriteStatus(null, $"Updated to {job.NewVersion}.");
        }
        // Rollback status and failed-version marker are retained for the Updates tab.
    }

    public static async Task<int> RunHelperAsync(UpdateTicket ticket)
    {
        UpdateJob? job = null;
        Process? launched = null;
        bool replacementAttempted = false, parentExited = false, committed = false;
        string directory = "";
        try
        {
            RequireUnelevated();
            directory = JobDirectory(ticket.Id);
            job = LoadJob(ticket);
            string expectedSelf = Path.Combine(directory, HelperName);
            if (!string.Equals(Environment.ProcessPath, expectedSelf, StringComparison.OrdinalIgnoreCase)
                || !HashMatches(expectedSelf, job.OldHash) || !HashMatches(job.Target, job.OldHash)
                || GitHubUpdates.CurrentVersion != Version.Parse(job.OldVersion))
                throw new IOException("The update helper does not match the application being replaced.");
            GitHubUpdates.ValidateExecutable(job.Target, Version.Parse(job.OldVersion));
            string staged = Path.Combine(directory, "update.exe"), candidate = Candidate(job, ticket), backup = Backup(job, ticket);
            RequireRegularPath(candidate); RequireRegularPath(backup);
            if (File.Exists(candidate) || File.Exists(backup)) throw new IOException("An update replacement path already exists.");
            if (!HashMatches(staged, job.NewHash)) throw new IOException("The staged update changed.");
            GitHubUpdates.ValidateExecutable(staged, Version.Parse(job.NewVersion));
            using var parent = Process.GetProcessById(job.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != job.ParentStartedUtc
                || !string.Equals(parent.MainModule?.FileName, job.Target, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The update's original process no longer matches.");
            // Prepare the same-volume replacement before asking the application to exit.
            File.Copy(staged, candidate, false);
            if (!HashMatches(candidate, job.NewHash)) throw new IOException("The replacement copy failed verification.");
            WriteMarker(directory, "ready", ticket.Token);
            using (var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                await parent.WaitForExitAsync(exitTimeout.Token);
            parentExited = true;
            if (ReadMarker(directory, "commit") != ticket.Token)
                throw new IOException("The application did not approve the update handoff.");
            committed = true;
            RequireRegularPath(job.Target); RequireRegularPath(candidate); RequireRegularPath(backup);
            if (!HashMatches(job.Target, job.OldHash) || !HashMatches(candidate, job.NewHash))
                throw new IOException("The application changed before replacement.");
            replacementAttempted = true;
            File.Replace(candidate, job.Target, backup, false);
            if (!HashMatches(backup, job.OldHash) || !HashMatches(job.Target, job.NewHash))
                throw new IOException("Replacement verification failed.");
            launched = StartProcess(job.Target, "--update-resume", ticket);
            var started = Stopwatch.StartNew();
            while (started.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (ReadMarker(directory, "ack") == ticket.Token)
                {
                    WriteMarker(directory, "result", "Updated successfully.");
                    // Acknowledgement means the replacement loaded the existing data and window.
                    // Keep the original backup until a later normal start cleans completed jobs.
                    return 0;
                }
                if (launched.HasExited) throw new IOException("The updated application exited before startup completed.");
                await Task.Delay(100);
            }
            throw new IOException("The updated application has not confirmed startup. Close it and use the releases page if it does not respond. The previous executable is preserved.");
        }
        catch (Exception ex)
        {
            if (job is null) return 1;
            string message = "Update failed. " + FriendlyError(ex);
            try
            {
                // Never replace or kill a process that may still be running.
                if (parentExited && committed && (launched is null || launched.HasExited))
                {
                    string backup = Backup(job, ticket);
                    RequireRegularPath(job.Target); RequireRegularPath(backup);
                    // File.Replace can fail after making some filesystem changes.
                    // Inspect exact hashes rather than assuming success is all-or-nothing.
                    if (replacementAttempted && File.Exists(backup) && HashMatches(backup, job.OldHash))
                    {
                        if (!File.Exists(job.Target)) File.Move(backup, job.Target, false);
                        else if (HashMatches(job.Target, job.NewHash)) File.Replace(backup, job.Target, null, false);
                    }
                    if (File.Exists(job.Target) && HashMatches(job.Target, job.OldHash))
                    {
                        message += " The previous version is available.";
                        WriteStatus(job.NewVersion, message);
                        WriteMarker(directory, "result", message);
                        using var restored = StartProcess(job.Target, "--update-rollback", ticket);
                        return 1;
                    }
                    message += " The replacement could not be safely rolled back; the backup was preserved.";
                }
                WriteStatus(ReadStatus().AttemptedVersion, message);
                WriteMarker(directory, "result", message);
            }
            catch { /* Preserve all remaining files if status or rollback cannot be written. */ }
            return 1;
        }
        finally { launched?.Dispose(); }
    }

    private static string? ReadMarker(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        RequireRegularPath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8192) throw new IOException("An update status marker is invalid.");
        return File.ReadAllText(path);
    }
    private static void WriteMarker(string directory, string name, string value)
    {
        string path = Path.Combine(directory, name);
        RequireRegularPath(path);
        AtomicFile.Write(path, Encoding.UTF8.GetBytes(value));
    }

    public static string FriendlyError(Exception error) => error switch
    {
        OperationCanceledException => "The operation timed out. Check your connection or try again later.",
        System.Net.Http.HttpRequestException => "GitHub could not be reached. Check your connection or open the releases page.",
        UnauthorizedAccessException => "Windows denied access to an update file. Use a writable local folder or download from releases.",
        _ => error.Message
    };

    public static void CleanCompletedJobs()
    {
        // Cleanup never follows links, deletes recursively, or touches arbitrary filenames.
        if (IsElevated()) return;
        RequireRegularPath(Root);
        if (!Directory.Exists(Root)) return;
        int cleaned = 0;
        foreach (string directory in Directory.EnumerateDirectories(Root).Take(100))
        {
            if (cleaned >= 5) break;
            try
            {
                string id = Path.GetFileName(directory);
                if (!Guid.TryParseExact(id, "N", out _) || Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddDays(-1)) continue;
                RequireRegularPath(directory);
                string? result = ReadMarker(directory, "result");
                string jobPath = Path.Combine(directory, "job.json");
                RequireRegularPath(jobPath);
                if (File.Exists(jobPath))
                {
                    if (new FileInfo(jobPath).Length > 16 * 1024) continue;
                    var stored = JsonSerializer.Deserialize<UpdateJob>(File.ReadAllBytes(jobPath));
                    if (stored is null) continue;
                    var ticket = new UpdateTicket(id, stored.Token);
                    var job = LoadJob(ticket);
                    // A committed handoff without a terminal result may need its
                    // staged files to recover, even when no backup exists yet.
                    if (result is null && ReadMarker(directory, "commit") is not null) continue;
                    // Unknown/failed replacements retain their backup for manual recovery.
                    if (result == "Updated successfully." && ReadMarker(directory, "ack") == ticket.Token)
                    {
                        string backup = Backup(job, ticket);
                        RequireRegularPath(backup);
                        if (File.Exists(backup) && HashMatches(backup, job.OldHash)) File.Delete(backup);
                        else if (File.Exists(backup)) continue;
                    }
                    else if (File.Exists(Backup(job, ticket))) continue;
                    string candidate = Candidate(job, ticket);
                    RequireRegularPath(candidate);
                    if (File.Exists(candidate))
                    {
                        if (!HashMatches(candidate, job.NewHash)) continue;
                        File.Delete(candidate);
                    }
                }
                else if (ReadMarker(directory, "commit") is not null || ReadMarker(directory, "ready") is not null
                    || ReadMarker(directory, "ack") is not null) continue;
                foreach (string name in JobFiles)
                {
                    string path = Path.Combine(directory, name);
                    RequireRegularPath(path);
                    if (File.Exists(path)) File.Delete(path);
                }
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
                cleaned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
            { /* A running helper, changed file or inaccessible job is left alone. */ }
        }
    }
}
