using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace Nstrap.Core;

public sealed record FpsChangeResult(bool Deferred, int? Cap, bool KeptExternal = false);

/// <summary>Changes only Player's existing FramerateCap property. No FastFlags or process memory are involved.</summary>
public sealed class FpsSettingsManager(string dataDirectory, string targetPath)
{
    public const int UnlockedCap = 9999;
    private const int MaximumBytes = 2 * 1024 * 1024;
    public string TargetPath { get; } = Path.GetFullPath(targetPath);
    private string RecoveryDirectory { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "recovery");
    private string BackupDirectory { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "backups", "player-fps");

    public FpsChangeResult Change(bool enabled, Func<bool> isPlayerRunning)
    {
        if (isPlayerRunning()) return new(true, null);
        CheckPath(TargetPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TargetPath.ToUpperInvariant())))[..24];
        using var gate = new Mutex(false, @"Local\Nstrap-FPS-" + key);
        bool acquired;
        try { acquired = gate.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Another N strap window is updating Player's FPS setting. Try again after it finishes.");
        try
        {
            Directory.CreateDirectory(RecoveryDirectory);
            var manifestPath = Path.Combine(RecoveryDirectory, "fps-" + key + ".json");
            var journalPath = Path.Combine(RecoveryDirectory, "fps-" + key + ".pending.json");
            Recover(manifestPath, journalPath);
            var previous = File.Exists(manifestPath)
                ? ReadRecord<FpsManifest>(manifestPath) : new FpsManifest { TargetPath = TargetPath };
            ValidateManifest(previous);
            if (!enabled && !previous.Managed) return new(false, null);

            var before = ReadCurrent() ?? throw new FileNotFoundException(
                "Player's settings file does not exist yet. Open Roblox Player once, close it, then select Retry.");
            var document = ReadDocument(before, out var property, out int currentCap);
            if (!enabled && previous.Managed && currentCap != UnlockedCap)
            {
                // A stable external preference supersedes our old baseline. Turning
                // off releases ownership without overwriting that preference.
                AtomicFile.WriteJson(manifestPath, new FpsManifest { TargetPath = TargetPath });
                return new(false, currentCap, KeptExternal: true);
            }

            int desiredCap = enabled ? UnlockedCap : previous.OriginalCap;
            var next = new FpsManifest
            {
                TargetPath = TargetPath,
                Managed = enabled,
                // Reconcile only on an explicit action/startup/launch, never continuously.
                // If Roblox or another tool changed the cap, remember that new preference.
                OriginalCap = enabled ? (previous.Managed && currentCap == UnlockedCap ? previous.OriginalCap : currentCap) : 0
            };
            if (currentCap == desiredCap)
            {
                AtomicFile.WriteJson(manifestPath, next);
                return new(false, desiredCap);
            }

            property.InnerText = desiredCap.ToString(CultureInfo.InvariantCulture);
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

            Directory.CreateDirectory(BackupDirectory);
            var backupPath = Path.Combine(BackupDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.xml");
            using (var backup = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                backup.Write(before);
                backup.Flush(true);
            }
            var journal = new FpsJournal { TargetPath = TargetPath, BeforeHash = Hash(before), AfterHash = Hash(after), Next = next };
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
            return new(false, desiredCap);
        }
        finally { gate.ReleaseMutex(); }
    }

    private void Recover(string manifestPath, string journalPath)
    {
        if (!File.Exists(journalPath)) return;
        var journal = ReadRecord<FpsJournal>(journalPath);
        if (!string.Equals(journal.TargetPath, TargetPath, StringComparison.OrdinalIgnoreCase) || journal.Next is null)
            throw new InvalidDataException("The FPS recovery record does not match Player's settings.");
        ValidateManifest(journal.Next);
        var currentHash = Hash(ReadCurrent());
        if (currentHash == journal.AfterHash) AtomicFile.WriteJson(manifestPath, journal.Next);
        else if (currentHash != journal.BeforeHash)
            throw new InvalidDataException("Player's settings changed after an interrupted FPS update. Backups and recovery records were kept for review.");
        File.Delete(journalPath);
    }

    private void ValidateManifest(FpsManifest manifest)
    {
        if (!string.Equals(manifest.TargetPath, TargetPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The FPS recovery record is invalid. Existing settings were left untouched.");
    }

    private byte[]? ReadCurrent()
    {
        if (!File.Exists(TargetPath)) return null;
        if (new FileInfo(TargetPath).Length > MaximumBytes)
            throw new InvalidDataException("Player's settings file exceeds 2 MB; it was left untouched.");
        return File.ReadAllBytes(TargetPath);
    }

    private static XmlDocument ReadDocument(byte[] bytes, out XmlElement property, out int value)
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
        var matches = properties?.Count == 1 ? properties[0]?.SelectNodes("*[@name='FramerateCap']") : null;
        if (root is null || root.Name != "roblox" || root.GetAttribute("version") != "4"
            || matches?.Count != 1 || matches[0] is not XmlElement cap || cap.Name != "int"
            || cap.ChildNodes.OfType<XmlElement>().Any()
            || !int.TryParse(cap.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            throw new InvalidDataException("Player's existing FramerateCap setting is missing or has an unsupported format. Open Player, choose a maximum frame rate in its settings, close Player, then select Retry.");
        property = cap;
        return document;
    }

    private static T ReadRecord<T>(string path) where T : class
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("The FPS recovery record is too large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("The FPS recovery record is unreadable.");
    }
    private static string Hash(byte[]? bytes) => bytes is null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes));
    private static void CheckPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Player's FPS setting cannot be changed through symbolic links or junctions.");
    }

    private sealed class FpsManifest
    {
        [JsonRequired] public string TargetPath { get; set; } = "";
        [JsonRequired] public bool Managed { get; set; }
        [JsonRequired] public int OriginalCap { get; set; }
    }
    private sealed class FpsJournal
    {
        [JsonRequired] public string TargetPath { get; set; } = "";
        [JsonRequired] public string BeforeHash { get; set; } = "";
        [JsonRequired] public string AfterHash { get; set; } = "";
        [JsonRequired] public FpsManifest Next { get; set; } = new();
    }
}
