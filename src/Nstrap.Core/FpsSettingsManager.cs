using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace Nstrap.Core;

public sealed record FpsChangeResult(bool Deferred, int? Cap, bool KeptExternal = false);
public sealed record GraphicsQualityChangeResult(bool Deferred, int? Level, bool KeptExternal = false);

/// <summary>
/// Changes Player's existing FramerateCap or SavedQualityLevel property.
/// Both use the same target lock and recover each other's journals before writing the shared XML.
/// </summary>
public sealed class FpsSettingsManager(string dataDirectory, string targetPath)
{
    public const int UnlockedCap = 9999;
    private const int MaximumBytes = 2 * 1024 * 1024;
    public string TargetPath { get; } = Path.GetFullPath(targetPath);
    private string RecoveryDirectory { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "recovery");
    private string BackupDirectory { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "backups", "player-fps");
    private static readonly SettingDefinition Fps = new("fps", "FramerateCap", "int", "FPS cap");
    private static readonly SettingDefinition Quality = new("graphics-quality", "SavedQualityLevel", "token", "graphics quality");

    public FpsChangeResult Change(bool enabled, Func<bool> isPlayerRunning) =>
        Change(enabled, UnlockedCap, isPlayerRunning);

    public FpsChangeResult Change(bool enabled, int cap, Func<bool> isPlayerRunning)
    {
        if (cap is < 1 or > UnlockedCap)
            throw new ArgumentOutOfRangeException(nameof(cap), "Choose a whole FPS cap from 1 to 9,999.");
        var result = ChangeSetting<FpsManifest>(Fps, enabled, cap, isPlayerRunning);
        return new(result.Deferred, result.Value, result.KeptExternal);
    }

    public GraphicsQualityChangeResult ChangeGraphicsQuality(bool enabled, int level, Func<bool> isPlayerRunning)
    {
        if (level is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(level), "Choose Automatic or a graphics quality level from 1 to 10.");
        var result = ChangeSetting<QualityManifest>(Quality, enabled, level, isPlayerRunning);
        return new(result.Deferred, result.Value, result.KeptExternal);
    }

    private SettingChangeResult ChangeSetting<T>(SettingDefinition setting, bool enabled, int value, Func<bool> isPlayerRunning)
        where T : SettingManifest, new()
    {
        if (isPlayerRunning()) return new(true, null);
        CheckPath(TargetPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TargetPath.ToUpperInvariant())))[..24];
        // Keep this name compatible with the previous FPS-only version.
        using var gate = new Mutex(false, @"Local\Nstrap-FPS-" + key);
        bool acquired;
        try { acquired = gate.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Another N strap window is updating Player's settings. Try again after it finishes.");
        try
        {
            CheckPath(RecoveryDirectory);
            Directory.CreateDirectory(RecoveryDirectory);
            // The file lock also serializes this account's N strap windows in different
            // Windows sessions, where a Local\ mutex is scoped to each session.
            var lockPath = Path.Combine(RecoveryDirectory, "player-settings-" + key + ".lock");
            CheckPath(lockPath);
            using var fileGate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Recover<FpsManifest>(Fps, key);
            Recover<QualityManifest>(Quality, key);
            var manifestPath = ManifestPath(setting, key);
            var journalPath = JournalPath(setting, key);
            var previous = File.Exists(manifestPath)
                ? ReadRecord<T>(manifestPath) : new T { TargetPath = TargetPath };
            ValidateManifest(previous);
            if (!enabled && !previous.Managed) return new(false, null);

            var before = ReadCurrent() ?? throw new FileNotFoundException(
                "Player's settings file does not exist yet. Open Roblox Player once, close it, then select Retry.");
            var document = ReadDocument(before, setting, out var property, out int current);
            if (!enabled && previous.Managed && current != previous.AppliedValue)
            {
                // A stable external preference supersedes our old baseline. Turning
                // off releases ownership without overwriting that preference.
                if (isPlayerRunning()) return new(true, null);
                EnsureUnchanged(before);
                AtomicFile.WriteJson(manifestPath, new T { TargetPath = TargetPath });
                return new(false, current, KeptExternal: true);
            }

            int desired = enabled ? value : previous.OriginalValue;
            var next = new T
            {
                TargetPath = TargetPath,
                Managed = enabled,
                // Reconcile only on an explicit action/startup/launch, never continuously.
                // If Roblox or another tool changed the cap, remember that new preference.
                OriginalValue = enabled ? (previous.Managed && current == previous.AppliedValue ? previous.OriginalValue : current) : 0,
                AppliedValue = enabled ? value : 0
            };
            if (current == desired)
            {
                if (isPlayerRunning()) return new(true, null);
                EnsureUnchanged(before);
                AtomicFile.WriteJson(manifestPath, next);
                return new(false, desired);
            }

            property.InnerText = desired.ToString(CultureInfo.InvariantCulture);
            using var output = new MemoryStream();
            using (var writer = XmlWriter.Create(output, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None,
                OmitXmlDeclaration = document.FirstChild is not XmlDeclaration
            }))
                document.Save(writer);
            var after = output.ToArray();
            if (after.Length > MaximumBytes) throw new InvalidDataException("Player's resulting settings exceed 2 MB; no settings were changed.");
            if (isPlayerRunning()) return new(true, null);

            var backupDirectory = setting == Fps ? BackupDirectory
                : Path.Combine(Path.GetDirectoryName(BackupDirectory)!, "player-graphics-quality");
            CheckPath(backupDirectory);
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.xml");
            using (var backup = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                backup.Write(before);
                backup.Flush(true);
            }
            var journal = new SettingJournal<T> { TargetPath = TargetPath, BeforeHash = Hash(before), AfterHash = Hash(after), Next = next };
            AtomicFile.WriteJson(journalPath, journal);
            CheckPath(TargetPath);
            if (Hash(ReadCurrent()) != journal.BeforeHash)
            {
                File.Delete(journalPath);
                throw new IOException("Player's settings changed during the update. No N strap write was made. Close other settings tools and select Retry.");
            }
            if (isPlayerRunning())
            {
                File.Delete(journalPath);
                return new(true, null);
            }
            // Atomic replacement plus a recovery journal bridges the settings and ownership records.
            AtomicFile.Write(TargetPath, after);
            AtomicFile.WriteJson(manifestPath, next);
            File.Delete(journalPath);
            return new(false, desired);
        }
        finally { gate.ReleaseMutex(); }
    }

    private string ManifestPath(SettingDefinition setting, string key) => Path.Combine(RecoveryDirectory, setting.Prefix + "-" + key + ".json");
    private string JournalPath(SettingDefinition setting, string key) => Path.Combine(RecoveryDirectory, setting.Prefix + "-" + key + ".pending.json");

    private void Recover<T>(SettingDefinition setting, string key) where T : SettingManifest, new()
    {
        var manifestPath = ManifestPath(setting, key);
        var journalPath = JournalPath(setting, key);
        CheckPath(manifestPath);
        CheckPath(journalPath);
        if (!File.Exists(journalPath)) return;
        var journal = ReadRecord<SettingJournal<T>>(journalPath);
        if (!string.Equals(journal.TargetPath, TargetPath, StringComparison.OrdinalIgnoreCase) || journal.Next is null)
            throw new InvalidDataException($"The {setting.Description} recovery record does not match Player's settings.");
        ValidateManifest(journal.Next);
        var currentHash = Hash(ReadCurrent());
        if (currentHash == journal.AfterHash) AtomicFile.WriteJson(manifestPath, journal.Next);
        else if (currentHash != journal.BeforeHash)
            throw new InvalidDataException("Player's settings changed after an interrupted update. Backups and recovery records were kept for review.");
        File.Delete(journalPath);
    }

    private void ValidateManifest(SettingManifest manifest)
    {
        if (!string.Equals(manifest.TargetPath, TargetPath, StringComparison.OrdinalIgnoreCase)
            || (manifest.Managed && manifest is FpsManifest && manifest.AppliedValue is < 1 or > UnlockedCap)
            || (manifest.Managed && manifest is QualityManifest && (manifest.AppliedValue is < 0 or > 10 || manifest.OriginalValue is < 0 or > 10)))
            throw new InvalidDataException("The Player settings recovery record is invalid. Existing settings were left untouched.");
    }

    private void EnsureUnchanged(byte[] expected)
    {
        CheckPath(TargetPath);
        if (Hash(ReadCurrent()) != Hash(expected))
            throw new IOException("Player's settings changed during the update. No N strap write was made. Close other settings tools and select Retry.");
    }

    private byte[]? ReadCurrent()
    {
        if (!File.Exists(TargetPath)) return null;
        if (new FileInfo(TargetPath).Length > MaximumBytes)
            throw new InvalidDataException("Player's settings file exceeds 2 MB; it was left untouched.");
        return File.ReadAllBytes(TargetPath);
    }

    private static XmlDocument ReadDocument(byte[] bytes, SettingDefinition setting, out XmlElement property, out int value)
    {
        using var input = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes
        });
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        document.Load(reader);
        var root = document.DocumentElement;
        var items = root?.SelectNodes("Item[@class='UserGameSettings']");
        var properties = items?.Count == 1 ? items[0]?.SelectNodes("Properties") : null;
        var matches = properties?.Count == 1 ? properties[0]?.SelectNodes($"*[@name='{setting.Property}']") : null;
        if (root is null || root.Name != "roblox" || root.GetAttribute("version") != "4"
            || matches?.Count != 1 || matches[0] is not XmlElement cap || cap.Name != setting.Element
            || cap.ChildNodes.OfType<XmlElement>().Any()
            || !int.TryParse(cap.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            || (setting == Quality && value is < 0 or > 10))
            throw new InvalidDataException($"Player's existing {setting.Property} setting is missing or has an unsupported format. Open Player, choose its {setting.Description}, close Player, then select Retry.");
        property = cap;
        return document;
    }

    private static T ReadRecord<T>(string path) where T : class
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("The Player settings recovery record is too large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("The Player settings recovery record is unreadable.");
    }
    private static string Hash(byte[]? bytes) => bytes is null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes));
    private static void CheckPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Player's settings cannot be changed through symbolic links or junctions.");
    }

    private sealed record SettingDefinition(string Prefix, string Property, string Element, string Description);
    private sealed record SettingChangeResult(bool Deferred, int? Value, bool KeptExternal = false);

    private abstract class SettingManifest
    {
        [JsonRequired] public string TargetPath { get; set; } = "";
        [JsonRequired] public bool Managed { get; set; }
        [JsonIgnore] public abstract int OriginalValue { get; set; }
        [JsonIgnore] public abstract int AppliedValue { get; set; }
    }
    private sealed class FpsManifest : SettingManifest
    {
        [JsonRequired] public int OriginalCap { get; set; }
        // Old manifests and journals predate custom caps and always applied 9,999.
        public int AppliedCap { get; set; } = UnlockedCap;
        [JsonIgnore] public override int OriginalValue { get => OriginalCap; set => OriginalCap = value; }
        [JsonIgnore] public override int AppliedValue { get => AppliedCap; set => AppliedCap = value; }
    }
    private sealed class QualityManifest : SettingManifest
    {
        [JsonRequired] public int OriginalLevel { get; set; }
        [JsonRequired] public int AppliedLevel { get; set; }
        [JsonIgnore] public override int OriginalValue { get => OriginalLevel; set => OriginalLevel = value; }
        [JsonIgnore] public override int AppliedValue { get => AppliedLevel; set => AppliedLevel = value; }
    }
    private sealed class SettingJournal<T> where T : SettingManifest, new()
    {
        [JsonRequired] public string TargetPath { get; set; } = "";
        [JsonRequired] public string BeforeHash { get; set; } = "";
        [JsonRequired] public string AfterHash { get; set; } = "";
        [JsonRequired] public T Next { get; set; } = new();
    }
}
