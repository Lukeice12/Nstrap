using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Nstrap.Core;

namespace Nstrap.App.Services;

/// <summary>
/// Best-effort restoration after the app exits and before its data is removed.
/// Recovery records prove ownership only for independently derived targets;
/// their paths and the profile's custom installation roots are never discovery input.
/// </summary>
internal static class EmergencyRestore
{
    private const int MaximumDirectories = 1000;
    private const int MaximumRecordBytes = FlagValidator.MaximumBytes;
    private const uint OpenExisting = 3, ReadAttributes = 0x80, GenericRead = 0x80000000;
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    private const uint FileTypeDisk = 1;

    internal static void TryRestoreOwnedSettings(string fixedDataDirectory)
    {
        try
        {
            string local = LocalPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            string expected = Path.Combine(local, "Nstrap");
            if (!SamePath(LocalPath(fixedDataDirectory), expected) || RobloxIsRunningOrUnknown()) return;

            // Pin existing ancestors before traversing children. Never create a
            // missing data/recovery root just to attempt emergency restoration.
            using var dataPins = new DirectoryPins();
            dataPins.Require(expected);
            string recovery = Path.Combine(expected, "recovery");
            dataPins.Require(recovery);
            var elapsed = Stopwatch.StartNew();

            TryRestorePlayer(expected, recovery, local, dataPins, elapsed);
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(30)) return;

            var roots = new[]
            {
                Path.Combine(local, "Roblox", "Versions"),
                ConventionalVersions(Environment.SpecialFolder.ProgramFilesX86),
                ConventionalVersions(Environment.SpecialFolder.ProgramFiles)
            };
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int remaining = MaximumDirectories;
            foreach (string? root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (remaining <= 0 || elapsed.Elapsed >= TimeSpan.FromSeconds(30)) break;
                if (root is null) continue;
                try
                {
                    using var rootPins = new DirectoryPins();
                    rootPins.Require(root);
                    RestoreDirectory(root);
                    // Default installations are directly beneath Versions.
                    // No recursive traversal, custom roots, or record-supplied paths.
                    foreach (string child in Directory.EnumerateDirectories(root))
                    {
                        if (remaining <= 0 || elapsed.Elapsed >= TimeSpan.FromSeconds(30)) break;
                        RestoreDirectory(child);
                    }
                }
                catch { /* Uninspectable installation roots are outside this attempt. */ }
            }

            void RestoreDirectory(string directory)
            {
                if (--remaining < 0 || !visited.Add(directory)) return;
                try
                {
                    if (elapsed.Elapsed >= TimeSpan.FromSeconds(30) || RobloxIsRunningOrUnknown()) return;
                    using var targetPins = new DirectoryPins();
                    targetPins.Require(directory);
                    foreach (ClientKind kind in Enum.GetValues<ClientKind>())
                    {
                        string executable = Path.Combine(directory,
                            kind == ClientKind.Player ? "RobloxPlayerBeta.exe" : "RobloxStudioBeta.exe");
                        if (!RegularFileExists(executable)) continue;
                        var installation = new RobloxInstallation(directory, executable, kind, default);
                        string target = installation.ConfigPath;
                        // Restoration cannot recover ownership from a missing target.
                        targetPins.Require(Path.GetDirectoryName(target)!);
                        if (!RegularFileExists(target)) return;
                        string key = TargetKey(target);
                        if (!ValidateFlagRecords(recovery, key, target)) return;
                        RequireOptionalRegularFile(Path.Combine(recovery, key + ".lock"));
                        dataPins.Ensure(Path.Combine(expected, "backups", key));
                        if (elapsed.Elapsed >= TimeSpan.FromSeconds(30) || RobloxIsRunningOrUnknown()) return;
                        new SettingsApplier(expected).Restore(installation);
                        // Player and Studio can share a directory/config file.
                        return;
                    }
                }
                catch { /* A conflict, lock or invalid record leaves this target alone. */ }
            }
        }
        catch { /* Emergency removal must not display dialogs or fail on restoration. */ }
    }

    private static void TryRestorePlayer(string data, string recovery, string local, DirectoryPins dataPins, Stopwatch elapsed)
    {
        string target = Path.Combine(local, "Roblox", "GlobalBasicSettings_13.xml");
        string key = TargetKey(target);
        foreach (bool quality in new[] { false, true })
        {
            try
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(30) || RobloxIsRunningOrUnknown()) return;
                using var targetPins = new DirectoryPins();
                targetPins.Require(Path.GetDirectoryName(target)!);
                if (!RegularFileExists(target)) return;

                // Either operation recovers BOTH journals before reading the XML.
                // Validate both pairs before allowing either operation to run.
                bool fpsOwned = ValidatePlayerRecords(recovery, "fps", key, target, quality: false);
                bool qualityOwned = ValidatePlayerRecords(recovery, "graphics-quality", key, target, quality: true);
                if (!(quality ? qualityOwned : fpsOwned)) continue;
                RequireOptionalRegularFile(Path.Combine(recovery, "player-settings-" + key + ".lock"));
                dataPins.Ensure(Path.Combine(data, "backups",
                    quality ? "player-graphics-quality" : "player-fps"));
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(30)) return;
                var settings = new FpsSettingsManager(data, target);
                if (quality) settings.ChangeGraphicsQuality(false, 0, RobloxIsRunningOrUnknown);
                else settings.Change(false, RobloxIsRunningOrUnknown);
            }
            catch { /* Keep external preferences and skip malformed or inaccessible records. */ }
        }
    }

    private static bool ValidateFlagRecords(string recovery, string key, string target)
    {
        bool owned = false;
        using (var manifest = ReadRecord(Path.Combine(recovery, key + ".json")))
            if (manifest is not null) owned = ValidateFlagManifest(manifest.RootElement, target);
        using (var journal = ReadRecord(Path.Combine(recovery, key + ".pending.json")))
            if (journal is not null)
            {
                var next = ValidateJournal(journal.RootElement, target, allowMissing: true);
                owned |= ValidateFlagManifest(next, target);
            }
        return owned;
    }

    private static bool ValidatePlayerRecords(string recovery, string prefix, string key, string target, bool quality)
    {
        bool owned = false;
        using (var manifest = ReadRecord(Path.Combine(recovery, prefix + "-" + key + ".json")))
            if (manifest is not null) owned = ValidatePlayerManifest(manifest.RootElement, target, quality);
        using (var journal = ReadRecord(Path.Combine(recovery, prefix + "-" + key + ".pending.json")))
            if (journal is not null)
            {
                var next = ValidateJournal(journal.RootElement, target, allowMissing: false);
                owned |= ValidatePlayerManifest(next, target, quality);
            }
        return owned;
    }

    private static JsonElement ValidateJournal(JsonElement record, string target, bool allowMissing)
    {
        RequireFields(record, "TargetPath", "BeforeHash", "AfterHash", "Next");
        RequireTarget(record, target);
        foreach (string name in new[] { "BeforeHash", "AfterHash" })
        {
            string? hash = record.GetProperty(name).GetString();
            if (!(allowMissing && hash == "missing")
                && (hash is not { Length: 64 } || !hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')))
                throw new InvalidDataException();
        }
        return record.GetProperty("Next");
    }

    private static bool ValidateFlagManifest(JsonElement record, string target)
    {
        RequireFields(record, "TargetPath", "OriginalFileExisted", "Managed", "AppliedAt", "ProfileName");
        RequireTarget(record, target);
        _ = record.GetProperty("OriginalFileExisted").GetBoolean();
        if (record.TryGetProperty("AppliedAt", out var appliedAt) && !appliedAt.TryGetDateTimeOffset(out _))
            throw new InvalidDataException();
        if (record.TryGetProperty("ProfileName", out var profileName) && profileName.ValueKind != JsonValueKind.String)
            throw new InvalidDataException();
        JsonElement managed = record.GetProperty("Managed");
        if (managed.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in managed.EnumerateObject())
        {
            if (!names.Add(entry.Name) || names.Count > FlagValidator.MaximumFlags) throw new InvalidDataException();
            RequireFields(entry.Value, "OriginalExisted", "Original", "Applied");
            if (entry.Value.TryGetProperty("OriginalExisted", out var existed)) _ = existed.GetBoolean();
            JsonElement applied = entry.Value.GetProperty("Applied");
            if (applied.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String))
                throw new InvalidDataException();
            var checkedValue = FlagValidator.Value(entry.Name,
                applied.ValueKind == JsonValueKind.String ? applied.GetString()! : applied.GetRawText());
            if (!JsonElement.DeepEquals(applied, checkedValue)) throw new InvalidDataException();
            // Original values can have any JSON type: they came from existing
            // settings and restoring them must not reinterpret that preference.
            _ = entry.Value.GetProperty("Original");
        }
        return names.Count > 0;
    }

    private static bool ValidatePlayerManifest(JsonElement record, string target, bool quality)
    {
        if (quality) RequireFields(record, "TargetPath", "Managed", "OriginalLevel", "AppliedLevel");
        else RequireFields(record, "TargetPath", "Managed", "OriginalCap", "AppliedCap");
        RequireTarget(record, target);
        bool managed = record.GetProperty("Managed").GetBoolean();
        int original = record.GetProperty(quality ? "OriginalLevel" : "OriginalCap").GetInt32();
        int applied = record.TryGetProperty(quality ? "AppliedLevel" : "AppliedCap", out var value)
            ? value.GetInt32() : quality ? throw new InvalidDataException() : FpsSettingsManager.UnlockedCap;
        int maximum = quality ? 10 : FpsSettingsManager.UnlockedCap;
        if (original < 0 || original > maximum || applied < 0 || applied > maximum
            || (managed && !quality && applied == 0))
            throw new InvalidDataException();
        return managed;
    }

    private static void RequireTarget(JsonElement record, string target)
    {
        if (!SamePath(record.GetProperty("TargetPath").GetString(), target))
            throw new InvalidDataException();
    }

    private static void RequireFields(JsonElement record, params string[] allowed)
    {
        if (record.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in record.EnumerateObject())
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name, StringComparer.Ordinal))
                throw new InvalidDataException();
    }

    private static JsonDocument? ReadRecord(string path)
    {
        using var handle = OpenRegularFile(path, GenericRead, FileShare.Read, allowMissing: true);
        if (handle is null) return null;
        using var input = new FileStream(handle, FileAccess.Read);
        if (input.Length is < 2 or > MaximumRecordBytes) throw new InvalidDataException();
        byte[] bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static bool RegularFileExists(string path)
    {
        using var handle = OpenRegularFile(path, ReadAttributes, FileShare.ReadWrite | FileShare.Delete, allowMissing: true);
        return handle is not null;
    }

    private static void RequireOptionalRegularFile(string path) => _ = RegularFileExists(path);

    private static SafeFileHandle? OpenRegularFile(string path, uint access, FileShare share, bool allowMissing)
    {
        var handle = CreateFile(LocalPath(path), access, share, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (allowMissing && error is 2 or 3) return null;
            throw new Win32Exception(error);
        }
        try
        {
            if (GetFileType(handle) != FileTypeDisk
                || (File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException();
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static string? ConventionalVersions(Environment.SpecialFolder folder)
    {
        try
        {
            string path = Environment.GetFolderPath(folder);
            return string.IsNullOrEmpty(path) ? null : Path.Combine(LocalPath(path), "Roblox", "Versions");
        }
        catch { return null; }
    }

    private static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new IOException();
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // Reject device, UNC and alternate-stream syntax. Redirected/unsupported
        // account folders are deliberately skipped by this emergency-only path.
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\'
            || full.AsSpan(2).Contains(':'))
            throw new IOException();
        return full;
    }

    private static string TargetKey(string target) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())))[..24];

    private static bool SamePath(string? first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static bool RobloxIsRunningOrUnknown()
    {
        try
        {
            // Program Files installations can be shared with other Windows
            // accounts. Conservatively skip for any Player or Studio session.
            foreach (string name in new[] { "RobloxPlayerBeta", "RobloxStudioBeta", "RobloxPlayerInstaller", "RobloxStudioInstaller" })
            {
                var processes = Process.GetProcessesByName(name);
                bool running = processes.Length > 0;
                foreach (var process in processes) process.Dispose();
                if (running) return true;
            }
            return false;
        }
        catch { return true; }
    }

    private sealed class DirectoryPins : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle> _handles = new(StringComparer.OrdinalIgnoreCase);

        public void Require(string directory) => Pin(directory, create: false);
        public void Ensure(string directory) => Pin(directory, create: true);

        private void Pin(string directory, bool create)
        {
            directory = LocalPath(directory);
            if (_handles.ContainsKey(directory)) return;
            string? parent = Path.GetDirectoryName(directory);
            if (parent is not null) Pin(parent, create);
            if (create)
            {
                // Parent has already been pinned; inspect an existing leaf before
                // CreateDirectory, which otherwise accepts an existing junction.
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException();
                }
                catch (FileNotFoundException) { Directory.CreateDirectory(directory); }
                catch (DirectoryNotFoundException) { Directory.CreateDirectory(directory); }
            }
            var handle = CreateFile(directory, ReadAttributes, FileShare.ReadWrite, IntPtr.Zero,
                OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try
            {
                var attributes = File.GetAttributes(handle);
                if (GetFileType(handle) != FileTypeDisk || (attributes & FileAttributes.Directory) == 0
                    || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new IOException();
                _handles.Add(directory, handle);
            }
            catch { handle.Dispose(); throw; }
        }

        public void Dispose()
        {
            foreach (var handle in _handles.Values.Reverse()) handle.Dispose();
            _handles.Clear();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);
}
