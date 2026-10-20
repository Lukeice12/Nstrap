using System.Security.Cryptography;
using System.Text.Json;

namespace Nstrap.Core;

public sealed class ManagedValue
{
    public bool OriginalExisted { get; set; }
    public JsonElement? Original { get; set; }
    public JsonElement Applied { get; set; }
}
public sealed class ApplyManifest
{
    public string TargetPath { get; set; } = "";
    public bool OriginalFileExisted { get; set; }
    public Dictionary<string, ManagedValue> Managed { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset AppliedAt { get; set; }
    public string ProfileName { get; set; } = "";
}
public sealed class ApplyJournal
{
    public string TargetPath { get; set; } = "";
    public string BeforeHash { get; set; } = "";
    public string AfterHash { get; set; } = "";
    public ApplyManifest Next { get; set; } = new();
}
public sealed record ApplyResult(int ManagedCount, string? BackupPath);

/// <summary>
/// Owns only keys it has applied. The journal bridges the atomic target replacement and
/// atomic manifest replacement; recovery accepts exactly the before or after state.
/// </summary>
public sealed class SettingsApplier(string dataDirectory)
{
    public string RecoveryDirectory { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "recovery");
    public string BackupDirectory { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "backups");
    public ApplyResult Apply(RobloxInstallation installation, FlagProfile profile)
    {
        var desired = FlagValidator.Validate(profile.Flags);
        EnsureInstallationExists(installation);
        return Change(installation.ConfigPath, desired, profile.Name, reapplyProfile: true);
    }
    public ApplyResult Restore(RobloxInstallation installation)
    {
        EnsureInstallationExists(installation);
        return Change(installation.ConfigPath, new(StringComparer.Ordinal), "", reapplyProfile: false);
    }
    private static void EnsureInstallationExists(RobloxInstallation installation)
    {
        if (!File.Exists(installation.ExecutablePath))
            throw new IOException("The selected Roblox installation no longer exists. Refresh installations, select the current client, and apply again.");
    }

    private ApplyResult Change(string target, Dictionary<string, JsonElement> desired, string profileName, bool reapplyProfile)
    {
        target = Path.GetFullPath(target);
        CheckPath(target);
        Directory.CreateDirectory(RecoveryDirectory);
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(target.ToUpperInvariant())))[..24];
        var manifestPath = Path.Combine(RecoveryDirectory, key + ".json");
        var journalPath = Path.Combine(RecoveryDirectory, key + ".pending.json");
        using var gate = new FileStream(Path.Combine(RecoveryDirectory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Recover(target, manifestPath, journalPath);
        var currentBytes = ReadCurrent(target);
        var values = currentBytes is null ? new Dictionary<string, JsonElement>(StringComparer.Ordinal) : FlagValidator.ReadObject(currentBytes);
        var previous = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<ApplyManifest>(File.ReadAllBytes(manifestPath)) ?? throw new InvalidDataException("Recovery record is unreadable.")
            : new ApplyManifest { TargetPath = target, OriginalFileExisted = currentBytes is not null };
        if (!string.Equals(previous.TargetPath, target, StringComparison.OrdinalIgnoreCase) || previous.Managed is null)
            throw new InvalidDataException("Recovery record does not match this installation.");
        if (previous.Managed.Count == 0) previous.OriginalFileExisted = currentBytes is not null;
        if (previous.Managed.Count == 0 && desired.Count == 0) return new(0, null);

        // Explicit application uses the profile's desired values. Changed owned values
        // become fresh restore baselines; restoration itself remains conflict-protected.
        var externalChanges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, managed) in previous.Managed)
        {
            if (!values.TryGetValue(name, out var current) || !Same(current, managed.Applied))
            {
                if (!reapplyProfile)
                    throw new InvalidDataException($"“{name}” was changed outside N strap. Restore stopped to keep that edit. Apply a profile to use its enabled values and record the current values for restoration, or inspect the backup. No settings were changed.");
                externalChanges.Add(name);
            }
        }

        var next = new ApplyManifest
        {
            TargetPath = target,
            OriginalFileExisted = externalChanges.Count > 0 ? currentBytes is not null : previous.OriginalFileExisted,
            AppliedAt = DateTimeOffset.UtcNow, ProfileName = profileName
        };
        foreach (var (name, managed) in previous.Managed)
        {
            if (!desired.ContainsKey(name) && !externalChanges.Contains(name))
            {
                if (managed.Original is { } original) values[name] = original;
                else if (managed.OriginalExisted) values[name] = JsonSerializer.SerializeToElement<object?>(null);
                else values.Remove(name);
            }
        }
        foreach (var (name, value) in desired)
        {
            bool retainBaseline = previous.Managed.TryGetValue(name, out var old) && !externalChanges.Contains(name);
            JsonElement? original = retainBaseline
                ? old!.Original
                : values.TryGetValue(name, out var current) ? current : null;
            bool originalExisted = retainBaseline
                ? old!.OriginalExisted || old.Original.HasValue
                : values.ContainsKey(name);
            next.Managed.Add(name, new() { OriginalExisted = originalExisted, Original = original, Applied = value });
            values[name] = value;
        }

        if (values.Count > FlagValidator.MaximumFlags)
            throw new InvalidDataException($"The resulting settings would exceed {FlagValidator.MaximumFlags} entries, including existing settings. No settings were changed.");
        byte[]? afterBytes = !next.OriginalFileExisted && values.Count == 0
            ? null : JsonSerializer.SerializeToUtf8Bytes(values, JsonDefaults.Options);
        if (currentBytes is null && afterBytes is null && previous.Managed.Count == 0)
            return new(0, null);
        if (afterBytes is { Length: > FlagValidator.MaximumBytes })
            throw new InvalidDataException("The resulting settings would exceed 2 MB.");

        string? backup = null;
        if (currentBytes is not null)
        {
            var folder = Path.Combine(BackupDirectory, key);
            Directory.CreateDirectory(folder);
            backup = Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.json");
            using var backupFile = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            backupFile.Write(currentBytes);
            backupFile.Flush(true);
        }
        var journal = new ApplyJournal
        {
            TargetPath = target, BeforeHash = Hash(currentBytes), AfterHash = Hash(afterBytes), Next = next
        };
        AtomicFile.WriteJson(journalPath, journal);
        CheckPath(target);
        // Catch a settings change between reading and commit. A third-party writer which races
        // this final check is outside our lock. Sharing violations fail without forcing access.
        if (Hash(ReadCurrent(target)) != journal.BeforeHash)
        {
            File.Delete(journalPath);
            throw new IOException("Settings changed while applying. No N strap write was made; try again after closing other settings tools.");
        }
        if (afterBytes is null) File.Delete(target);
        else AtomicFile.Write(target, afterBytes);
        AtomicFile.WriteJson(manifestPath, next);
        File.Delete(journalPath);
        return new(next.Managed.Count, backup);
    }

    private static void Recover(string target, string manifestPath, string journalPath)
    {
        if (!File.Exists(journalPath)) return;
        var journal = JsonSerializer.Deserialize<ApplyJournal>(File.ReadAllBytes(journalPath))
            ?? throw new InvalidDataException("An interrupted operation has an unreadable recovery record.");
        if (!string.Equals(journal.TargetPath, target, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(journal.Next.TargetPath, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Interrupted operation belongs to a different settings file.");
        string current = Hash(ReadCurrent(target));
        if (current == journal.AfterHash) AtomicFile.WriteJson(manifestPath, journal.Next);
        else if (current != journal.BeforeHash)
            throw new InvalidDataException("Settings changed after an interrupted operation. Backups and recovery records have been kept for manual review.");
        File.Delete(journalPath);
    }
    private static byte[]? ReadCurrent(string path)
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length > FlagValidator.MaximumBytes) throw new InvalidDataException("The existing settings file exceeds 2 MB; it was left untouched.");
        return File.ReadAllBytes(path);
    }
    private static bool Same(JsonElement a, JsonElement b) => JsonElement.DeepEquals(a, b);
    private static string Hash(byte[]? bytes) => bytes is null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes));
    private static void CheckPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Settings cannot be written through symbolic links or junctions. Select a regular Roblox installation folder.");
    }
}
