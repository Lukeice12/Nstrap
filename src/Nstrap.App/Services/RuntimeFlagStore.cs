using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nstrap.Core;

namespace Nstrap.App.Services;

internal sealed class RuntimeFlagStore
{
    // Ownership is shared across profile directories. Retain the previous local
    // candidate store when present so an update does not lose live baselines.
    private const string LegacyDirectory = @"J:\SKILLHELPER\nstrap-runtime-state";
    internal static string DefaultDirectory => ResolveDefaultDirectory(
        Environment.GetEnvironmentVariable("NSTRAP_RUNTIME_DIRECTORY"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Directory.Exists);
    internal static string ResolveDefaultDirectory(string? overrideDirectory, string localApplicationData, Func<string, bool> directoryExists)
    {
        if (!string.IsNullOrEmpty(overrideDirectory)) return RequireAbsoluteDirectory(overrideDirectory);
        if (directoryExists(LegacyDirectory)) return LegacyDirectory;
        return RequireAbsoluteDirectory(Path.Combine(RequireAbsoluteDirectory(localApplicationData), "Nstrap", "runtime-state"));
    }
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal string DirectoryPath { get; }
    internal RuntimeFlagStore(string directory) => DirectoryPath = RequireAbsoluteDirectory(directory);
    private static string RequireAbsoluteDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Runtime receipts require an absolute directory.", nameof(directory));
        return Path.GetFullPath(directory);
    }
    private string Key(RuntimeFlagSession session) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.ProcessSessionId + ":" + session.ExecutableSha256)));
    internal IDisposable Acquire(RuntimeFlagSession session)
    {
        EnsureDirectory();
        string file = Path.Combine(DirectoryPath, Key(session) + ".lock");
        CheckRegular(file);
        try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("Another Nstrap operation holds this process-lifetime lease; runtime writes skipped.", error); }
    }
    internal RuntimeOwnershipState Load(string imagePath, RuntimeFlagSession session, RuntimeFlagMapDescriptor map)
    {
        string path = Path.Combine(DirectoryPath, Key(session) + ".ownership.json");
        CheckRegular(path);
        if (!File.Exists(path)) return new(1, imagePath, session, []);
        if (new FileInfo(path).Length is <= 0 or > 1024 * 1024) throw new IOException("Runtime ownership receipt has an invalid size.");
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllBytes(path), Json) ?? throw new IOException("Runtime ownership receipt is missing.");
        byte[] authenticated = RuntimeReceiptProtection.Unprotect(Convert.FromBase64String(receipt.ProtectedPayload));
        byte[] readable = JsonSerializer.SerializeToUtf8Bytes(receipt.Payload, Json);
        if (!CryptographicOperations.FixedTimeEquals(authenticated, readable)) throw new IOException("Runtime ownership receipt was edited or belongs to another Windows account.");
        RuntimeFlagOwnership.Validate(receipt.Payload, imagePath, session, map);
        return receipt.Payload;
    }
    internal void Save(RuntimeOwnershipState state)
    {
        EnsureDirectory();
        string path = Path.Combine(DirectoryPath, Key(state.Session) + ".ownership.json");
        CheckRegular(path);
        var receipt = new Receipt(state, Convert.ToBase64String(RuntimeReceiptProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(state, Json))));
        AtomicFile.WriteJson(path, receipt);
    }
    internal string SaveReport(RuntimeFlagReport report)
    {
        EnsureDirectory();
        string path = Path.Combine(DirectoryPath, $"report-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
        AtomicFile.WriteJson(path, report);
        return path;
    }
    private void EnsureDirectory()
    {
        CheckRegular(DirectoryPath); Directory.CreateDirectory(DirectoryPath); CheckRegular(DirectoryPath);
    }
    private static void CheckRegular(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Runtime receipts cannot use symbolic links or junctions.");
    }
    private sealed record Receipt(RuntimeOwnershipState Payload, string ProtectedPayload);
}
