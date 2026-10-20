using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nstrap.Core;

namespace Nstrap.App.Services;

internal sealed record BackgroundFlag(string Name, string Value, bool Enabled);

/// <summary>A detached snapshot taken on the dispatcher only after persistence succeeds.</summary>
internal sealed record BackgroundProfileSnapshot(Guid Id, string Name, BackgroundFlag[] Flags, string[] CustomRoots)
{
    internal string ProfileFingerprint { get; } = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new { Id, Flags })));
    internal string Fingerprint { get; } = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new { Id, Flags, CustomRoots })));

    internal FlagProfile ToProfile() => new()
    {
        Id = Id, Name = Name,
        Flags = new ObservableCollection<FlagEntry>(Flags.Select(flag => new FlagEntry
        { Name = flag.Name, Value = flag.Value, Enabled = flag.Enabled }))
    };
}

/// <summary>
/// Event-driven preparation for the next Player start. This service never launches,
/// terminates or attaches to Roblox. The dispatcher owns serialization with UI work.
/// </summary>
internal sealed class BackgroundProfileService : IDisposable
{
    private const int MaximumRoots = 32, MaximumDirectories = 512, MaximumWatchers = 192;
    private const int MaximumRecoveryBytes = 8 * 1024 * 1024;
    private readonly string _data;
    private readonly Action _requestPreparation;
    private readonly object _gate = new();
    private readonly Dictionary<WatchKey, FileSystemWatcher> _watchers = [];
    private readonly HashSet<WatchKey> _failedWatchers = [];
    private readonly Dictionary<string, (string Stamp, string Error)> _failedAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Process> _processes = [];
    private readonly System.Threading.Timer _pendingTimer;
    private bool _disposed;
    private string _watchWarning = "";

    internal BackgroundProfileService(string dataDirectory, Action requestPreparation)
    {
        _data = Path.GetFullPath(dataDirectory);
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nstrap");
        if (!string.Equals(_data, Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Background preparation requires this account's Nstrap data folder.");
        RequireRegularPath(_data);
        _requestPreparation = requestPreparation;
        _pendingTimer = new(_ => CheckPendingProcessExit(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // Only one caller may run Prepare; MainWindow holds IsBusy until it returns.
    internal string Prepare(BackgroundProfileSnapshot snapshot)
    {
        ThrowIfStopped();
        var profile = snapshot.ToProfile();
        var desired = FlagValidator.Validate(profile.Flags);
        var watchKeys = new HashSet<WatchKey>();
        var candidates = Discover(snapshot.CustomRoots, watchKeys, out string discoveryWarning);
        UpdateWatchers(watchKeys);
        if (ObserveRunningClients()) return "Pending: close Roblox to prepare the saved profile for its next start.";
        if (candidates.Count == 0)
            return WithWarning("Waiting for an installed Roblox Player.", discoveryWarning);

        int ready = 0, applied = 0;
        string? failure = null;
        foreach (var installation in candidates)
        {
            ThrowIfStopped();
            string target = installation.ConfigPath;
            string? attemptStamp = null;
            try
            {
                using var pins = new RemovalFiles.Pins();
                pins.AddAncestors(installation.DirectoryPath);
                RequireRegularFile(installation.ExecutablePath, mustExist: true);
                // Hold the same image stable through signature verification and apply.
                using var image = new FileStream(installation.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                RequireRegularFile(target, mustExist: false);
                byte[]? current = ReadBounded(target, FlagValidator.MaximumBytes);
                var values = current is null ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    : FlagValidator.ReadObject(current);
                string key = TargetKey(target);
                var ownership = ReadOwnership(key, target, current);
                attemptStamp = string.Join(":", snapshot.ProfileFingerprint, image.Length,
                    File.GetLastWriteTimeUtc(installation.ExecutablePath).Ticks, Hash(current), ownership.Fingerprint);
                if (_failedAttempts.TryGetValue(target, out var priorFailure) && priorFailure.Stamp == attemptStamp)
                {
                    failure ??= priorFailure.Error;
                    continue;
                }

                // Background work never treats an external edit as authorization
                // to replace it. An explicit Apply can establish a new baseline.
                foreach (var (name, owned) in ownership.Manifest.Managed)
                    if (!values.TryGetValue(name, out var value) || !JsonElement.DeepEquals(value, owned.Applied))
                        throw new InvalidDataException("Managed flags changed outside Nstrap. Review them and use Apply profile to continue.");

                bool matches = desired.Count == ownership.Manifest.Managed.Count
                    && desired.All(flag => ownership.Manifest.Managed.TryGetValue(flag.Key, out var owned)
                        && JsonElement.DeepEquals(flag.Value, owned.Applied));
                if (matches && !ownership.HasJournal) { _failedAttempts.Remove(target); ready++; continue; }

                OfficialClient.VerifyPublisher(installation.ExecutablePath);
                ThrowIfStopped();
                if (ObserveRunningClients()) return "Pending: close Roblox to prepare the saved profile for its next start.";
                pins.AddAncestors(_data);
                EnsurePinnedDirectory(pins, Path.Combine(_data, "recovery"));
                EnsurePinnedDirectory(pins, Path.Combine(_data, "backups"));
                EnsurePinnedDirectory(pins, Path.Combine(_data, "backups", key));
                EnsurePinnedDirectory(pins, Path.GetDirectoryName(target)!);
                RequireRegularFile(Path.Combine(_data, "recovery", key + ".lock"), mustExist: false);
                // Re-read after signature verification and pinning. The core's own
                // final hash check then guards changes during its transaction.
                if (!SameBytes(current, ReadBounded(target, FlagValidator.MaximumBytes)))
                    throw new IOException("Player settings changed while preparation was being checked.");
                ThrowIfStopped();
                if (ObserveRunningClients()) return "Pending: close Roblox to prepare the saved profile for its next start.";
                new SettingsApplier(_data).ApplyPreservingExternalChanges(installation, profile);
                _failedAttempts.Remove(target);
                applied++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                if (attemptStamp is not null) _failedAttempts[target] = (attemptStamp, error.Message);
                failure ??= error.Message;
            }
        }
        if (failure is not null)
            return WithWarning($"Background preparation needs attention. {failure}", discoveryWarning);
        string status = applied > 0
            ? $"Saved profile prepared for {applied + ready} Player installation(s). Changes load on the next start."
            : "Saved profile is ready for Player's next start.";
        return WithWarning(status, discoveryWarning);
    }

    private List<RobloxInstallation> Discover(string[] customRoots, HashSet<WatchKey> watches, out string warning)
    {
        warning = "";
        if (customRoots.Length > MaximumRoots) throw new InvalidDataException("Too many custom installation roots for background preparation.");
        var roots = new List<string>();
        foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            string basis = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(basis)) roots.Add(Path.Combine(basis, "Roblox", "Versions"));
        }
        roots.AddRange(customRoots);
        var found = new Dictionary<string, RobloxInstallation>(StringComparer.OrdinalIgnoreCase);
        int remaining = MaximumDirectories;
        foreach (string raw in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ThrowIfStopped();
            try
            {
                RequireRegularPath(raw);
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
                AddRootWatch(root, watches);
                if (!Directory.Exists(root)) continue;
                using var pins = new RemovalFiles.Pins();
                pins.AddAncestors(root);
                var group = new List<RobloxInstallation>();
                Inspect(root, group);
                foreach (string child in Directory.EnumerateDirectories(root))
                {
                    if (remaining <= 0) break;
                    Inspect(child, group);
                    if (string.Equals(Path.GetFileName(child), "Versions", StringComparison.OrdinalIgnoreCase))
                    {
                        RequireRegularPath(child);
                        AddRootWatch(child, watches);
                        foreach (string version in Directory.EnumerateDirectories(child))
                        {
                            if (remaining <= 0) break;
                            Inspect(version, group);
                        }
                    }
                }
                // Prepare the newest Player under each configured root. Explicit
                // custom version folders remain supported without touching Studio.
                var newest = group.OrderByDescending(client => client.LastModified).FirstOrDefault();
                if (newest is not null) found[newest.ExecutablePath] = newest;
            }
            catch (OperationCanceledException) { throw; }
            catch { warning = "Some installation folders could not be inspected."; }
            if (remaining <= 0) { warning = "Installation discovery reached its limit; use a specific version folder for large custom roots."; break; }
        }
        return found.Values.ToList();

        void Inspect(string directory, List<RobloxInstallation> group)
        {
            if (--remaining < 0) return;
            ThrowIfStopped();
            try
            {
                RequireRegularPath(directory);
                if (!Directory.Exists(directory)) return;
                watches.Add(new(directory, "RobloxPlayerBeta.exe", false));
                string executable = Path.Combine(directory, "RobloxPlayerBeta.exe");
                RequireRegularFile(executable, mustExist: false);
                if (File.Exists(executable))
                    group.Add(new(Path.GetFullPath(directory), Path.GetFullPath(executable), ClientKind.Player,
                        File.GetLastWriteTimeUtc(executable)));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* One inaccessible or redirected version does not authorize traversal. */ }
        }
    }

    private static void AddRootWatch(string root, HashSet<WatchKey> watches)
    {
        string directory = root;
        string filter = "*";
        while (!Directory.Exists(directory))
        {
            filter = Path.GetFileName(directory);
            directory = Path.GetDirectoryName(directory) ?? throw new IOException("The installation root is unavailable.");
            RequireRegularPath(directory);
        }
        watches.Add(new(directory, filter, true));
    }

    private void UpdateWatchers(HashSet<WatchKey> wanted)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            foreach (var key in _watchers.Keys.Except(wanted).ToArray())
            {
                _watchers[key].Dispose();
                _watchers.Remove(key);
            }
            // Directory watches take priority so newly installed versions remain visible.
            foreach (var key in wanted.OrderByDescending(key => key.Directories))
            {
                if (_watchers.ContainsKey(key) || _failedWatchers.Contains(key)) continue;
                if (_watchers.Count >= MaximumWatchers)
                {
                    _watchWarning = "Some version folders exceed the background watch limit.";
                    break;
                }
                try
                {
                    RequireRegularPath(key.Path);
                    var watcher = new FileSystemWatcher(key.Path, key.Filter)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = key.Directories ? NotifyFilters.DirectoryName
                            : NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    watcher.Created += WatchChanged;
                    watcher.Changed += WatchChanged;
                    watcher.Deleted += WatchChanged;
                    watcher.Renamed += WatchRenamed;
                    watcher.Error += (_, _) => WatchFailed(key);
                    _watchers.Add(key, watcher);
                    watcher.EnableRaisingEvents = true;
                }
                catch
                {
                    if (_watchers.Remove(key, out var failed)) failed.Dispose();
                    _failedWatchers.Add(key);
                    _watchWarning = "Some folders cannot be watched; turn Silent mode off and on to retry.";
                }
            }
        }
    }

    private void WatchChanged(object sender, FileSystemEventArgs args) => RequestPreparation();
    private void WatchRenamed(object sender, RenamedEventArgs args) => RequestPreparation();

    private void WatchFailed(WatchKey key)
    {
        lock (_gate)
        {
            if (_disposed || !_failedWatchers.Add(key)) return;
            if (_watchers.Remove(key, out var watcher)) watcher.Dispose();
            _watchWarning = "Folder monitoring was interrupted; turn Silent mode off and on to retry.";
        }
        // One bounded reconciliation after overflow/failure; never restart a
        // failing watcher in a retry loop.
        RequestPreparation();
    }

    private bool ObserveRunningClients()
    {
        lock (_gate)
        {
            ThrowIfStopped();
            ClearProcesses();
            var found = new List<Process>();
            try
            {
                foreach (string name in new[] { "RobloxPlayerBeta", "RobloxStudioBeta", "RobloxPlayerInstaller" })
                    found.AddRange(Process.GetProcessesByName(name));
                if (found.Count == 0) return false;
                if (found.Count > 128) throw new IOException("Too many Roblox sessions to observe safely.");
                bool fallback = false;
                foreach (var process in found)
                {
                    _processes.Add(process);
                    try
                    {
                        process.Exited += ClientExited;
                        process.EnableRaisingEvents = true;
                    }
                    catch { fallback = true; }
                }
                // Used only when a known running process cannot expose an exit
                // notification. No directory or file scanning on this timer.
                if (fallback) _pendingTimer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
                return true;
            }
            catch
            {
                foreach (var process in found.Except(_processes)) process.Dispose();
                ClearProcesses();
                throw new IOException("Windows could not inspect Roblox sessions. Existing settings were left untouched.");
            }
        }
    }

    private void ClientExited(object? sender, EventArgs args) => RequestPreparation();

    internal void PausePendingWait()
    {
        lock (_gate) { if (!_disposed) ClearProcesses(); }
    }

    private void CheckPendingProcessExit()
    {
        try
        {
            if (!ObserveRunningClients()) RequestPreparation();
        }
        catch (OperationCanceledException) { }
        catch { RequestPreparation(); } // One reconciliation reports the inspection failure.
    }

    private void ClearProcesses()
    {
        _pendingTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        foreach (var process in _processes)
        {
            process.Exited -= ClientExited;
            process.Dispose();
        }
        _processes.Clear();
    }

    private (ApplyManifest Manifest, bool HasJournal, string Fingerprint) ReadOwnership(string key, string target, byte[]? current)
    {
        string recovery = Path.Combine(_data, "recovery");
        byte[]? manifestBytes = ReadBounded(Path.Combine(recovery, key + ".json"), MaximumRecoveryBytes);
        var manifest = manifestBytes is null ? new ApplyManifest { TargetPath = target }
            : ReadManifest(JsonDocument.Parse(manifestBytes, new() { MaxDepth = 32 }), target);
        byte[]? journalBytes = ReadBounded(Path.Combine(recovery, key + ".pending.json"), MaximumRecoveryBytes);
        string fingerprint = Hash(manifestBytes) + ":" + Hash(journalBytes);
        if (journalBytes is null) return (manifest, false, fingerprint);
        using var document = JsonDocument.Parse(journalBytes, new() { MaxDepth = 32 });
        var journal = document.RootElement;
        RequireFields(journal, "TargetPath", "BeforeHash", "AfterHash", "Next");
        RequireTarget(journal, target);
        string before = ReadHash(journal.GetProperty("BeforeHash")), after = ReadHash(journal.GetProperty("AfterHash"));
        var next = ReadManifest(journal.GetProperty("Next"), target);
        string currentHash = Hash(current);
        if (currentHash == after) manifest = next;
        else if (currentHash != before) throw new IOException("Settings changed after an interrupted operation. Use Recovery to review them.");
        return (manifest, true, fingerprint);
    }

    private static ApplyManifest ReadManifest(JsonDocument document, string target)
    {
        using (document) return ReadManifest(document.RootElement, target);
    }

    private static ApplyManifest ReadManifest(JsonElement record, string target)
    {
        RequireFields(record, "TargetPath", "OriginalFileExisted", "Managed", "AppliedAt", "ProfileName");
        RequireTarget(record, target);
        _ = record.GetProperty("OriginalFileExisted").GetBoolean();
        var managed = record.GetProperty("Managed");
        if (managed.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid ownership record.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in managed.EnumerateObject())
        {
            if (!names.Add(item.Name) || names.Count > FlagValidator.MaximumFlags) throw new InvalidDataException("Invalid ownership record.");
            RequireFields(item.Value, "OriginalExisted", "Original", "Applied");
            if (item.Value.TryGetProperty("OriginalExisted", out var existed)) _ = existed.GetBoolean();
            _ = item.Value.GetProperty("Original");
            var applied = item.Value.GetProperty("Applied");
            if (applied.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String))
                throw new InvalidDataException("Invalid owned flag value.");
            var parsed = FlagValidator.Value(item.Name, applied.ValueKind == JsonValueKind.String ? applied.GetString()! : applied.GetRawText());
            if (!JsonElement.DeepEquals(parsed, applied)) throw new InvalidDataException("Invalid owned flag value.");
        }
        return record.Deserialize<ApplyManifest>() ?? throw new InvalidDataException("Invalid ownership record.");
    }

    private static void RequireFields(JsonElement record, params string[] allowed)
    {
        if (record.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid ownership record.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in record.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException("Invalid ownership record.");
    }

    private static void RequireTarget(JsonElement record, string target)
    {
        if (!string.Equals(record.GetProperty("TargetPath").GetString(), target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ownership record belongs to another installation.");
    }

    private static string ReadHash(JsonElement value)
    {
        string? hash = value.GetString();
        if (hash != "missing" && (hash is not { Length: 64 } || !hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidDataException("Invalid recovery hash.");
        return hash!;
    }

    private static byte[]? ReadBounded(string path, int maximum)
    {
        RequireRegularFile(path, mustExist: false);
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw new InvalidDataException("A settings or recovery file is too large for background preparation.");
        byte[] bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static void RequireRegularFile(string path, bool mustExist)
    {
        RequireRegularPath(path);
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException("A settings path is not a regular file.");
        }
        catch (FileNotFoundException) when (!mustExist) { }
        catch (DirectoryNotFoundException) when (!mustExist) { }
    }

    private static void RequireRegularPath(string path)
    {
        try { UpdateHandoff.RequireRegularPath(path); }
        catch (Exception error) { throw new IOException("Background preparation requires accessible local folders without links or redirection.", error); }
    }

    private static void EnsurePinnedDirectory(RemovalFiles.Pins pins, string path)
    {
        RequireRegularPath(path);
        Directory.CreateDirectory(path);
        pins.AddAncestors(path);
    }

    private static bool SameBytes(byte[]? first, byte[]? second) => Hash(first) == Hash(second);
    private static string Hash(byte[]? bytes) => bytes is null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes));
    private static string TargetKey(string target) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())))[..24];

    private string WithWarning(string status, string warning)
    {
        lock (_gate) return string.Join(" ", new[] { status, warning, _watchWarning }.Where(text => text.Length != 0));
    }

    private void RequestPreparation()
    {
        lock (_gate) { if (_disposed) return; }
        try { _requestPreparation(); } catch { /* Notifications never escape watcher/process callbacks. */ }
    }

    private void ThrowIfStopped()
    {
        lock (_gate) if (_disposed) throw new OperationCanceledException();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
            ClearProcesses();
            _pendingTimer.Dispose();
        }
    }

    private sealed record WatchKey(string Path, string Filter, bool Directories);
}
