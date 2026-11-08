using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nstrap.Core;

namespace RuntimeWindows;

internal static class Candidate
{
    internal const string Path = @"C:\Users\Lukej\AppData\Local\Roblox\Versions\version-f0ea5da9412f4e35\RobloxPlayerBeta.exe";
    internal const string Hash = "f98b351febde8a4f89f8b25b8b07af62e6dd772cc3eee374a4f77f0dcf477713";
    internal const ulong ImageSize = 155062272, Rva = 0x81715B0;
    internal const string Flag = "DFIntTaskSchedulerTargetFps";
    internal static readonly RuntimeFlagMapDescriptor Map = new(Hash, ImageSize, [new(Flag, Rva, RuntimeFlagScalarType.Int32)]);
    internal static void VerifyEvidence(WindowsRuntimeFlagMemory memory)
    {
        VerifyName(memory);
        if (!ReadEvidence(memory, 0x2A43C20, 6, "consumer-DWORD-load").AsSpan().SequenceEqual(new byte[] { 0x8B, 0x05, 0x8A, 0xD9, 0x72, 0x05 }))
            throw new IOException("Validated DWORD-load instruction no longer matches this map.");
    }
    internal static void VerifyName(WindowsRuntimeFlagMemory memory)
    {
        byte[] pointer = ReadEvidence(memory, 0x81715B8, 8, "name-pointer");
        if (BinaryPrimitives.ReadUInt64LittleEndian(pointer) != checked(memory.ModuleBase + 0x738AFF0))
            throw new IOException("Flag-name pointer no longer matches the validated map.");
        if (BinaryPrimitives.ReadUInt64LittleEndian(ReadEvidence(memory, 0x81715C0, 8, "name-length")) != 22)
            throw new IOException("Flag-name length no longer matches the validated map.");
        if (!ReadEvidence(memory, 0x738AFF0, 22, "name-string").AsSpan().SequenceEqual("TaskSchedulerTargetFps"u8))
            throw new IOException("Flag-name bytes no longer match the validated map.");
    }
    private static byte[] ReadEvidence(WindowsRuntimeFlagMemory memory, ulong rva, int length, string step)
    {
        try { return Scalar.Read(memory, rva, length); }
        catch (Exception error) { throw new IOException($"Evidence step '{step}' failed: {error.Message}", error); }
    }
}

internal static class Scalar
{
    internal static byte[] Encode(int value) { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return bytes; }
    internal static byte[] Read(IRuntimeFlagMemory memory, ulong rva, int count = 4)
    {
        byte[] bytes = new byte[count];
        int read = memory.Read(rva, bytes);
        if (read != count) throw new IOException($"Incomplete scalar read: {read}/{count}.");
        return bytes;
    }
    internal static int ReadInt(IRuntimeFlagMemory memory, ulong rva) => BinaryPrimitives.ReadInt32LittleEndian(Read(memory, rva));
    internal static string Restore(IRuntimeFlagMemory memory, ulong rva, int original, int intended)
    {
        int current = ReadInt(memory, rva);
        if (current == original) return "OriginalAlreadyPresent";
        if (current != intended) return $"ExternalChangePreserved(current={current})";
        // The wrapper repeats this comparison immediately before the native write.
        var guarded = new ConditionalScalarMemory(memory, rva, original, intended, allowInitialRestore: true);
        if (guarded.Write(rva, Encode(original)) != 4) throw new IOException("Restoration returned an incomplete write.");
        if (ReadInt(memory, rva) != original) throw new IOException("Restoration read-back mismatch.");
        return "OriginalRestoredReadBack";
    }
}

// Restricts writes to the persisted original/intended values and re-reads immediately
// before each write. This cannot provide atomic CAS against a target/external writer.
internal sealed class ConditionalScalarMemory(IRuntimeFlagMemory inner, ulong rva, int original, int intended, bool allowInitialRestore = false) : IRuntimeFlagMemory
{
    internal bool NativeWriteAttempted { get; private set; }
    public RuntimeFlagSession Identity => inner.Identity;
    public int Read(ulong address, Span<byte> destination) => inner.Read(address, destination);
    public int Write(ulong address, ReadOnlySpan<byte> source)
    {
        if (address != rva || source.Length != 4) throw new IOException("Write is outside the single persisted scalar.");
        int next = BinaryPrimitives.ReadInt32LittleEndian(source);
        int expected = next == intended ? original : next == original ? intended : throw new IOException("Write does not match a persisted value.");
        if (next == original && !NativeWriteAttempted && !allowInitialRestore)
            throw new IOException("No native write was attempted by this transaction; external intended bytes will not be restored.");
        if (Scalar.ReadInt(inner, rva) != expected) throw new IOException("Observed concurrent change; conditional write skipped.");
        NativeWriteAttempted = true;
        return inner.Write(rva, source);
    }
}

internal sealed record ReceiptPayload(int Version, DateTimeOffset CreatedUtc, string Flag, string Path,
    string ExecutableSha256, ulong ImageSize, int ProcessId, long CreationTime, string SessionId,
    ulong ModuleBase, ulong RelativeVirtualAddress, int OriginalValue, int IntendedValue);
internal sealed record ProbeReceipt(ReceiptPayload Payload, string ProtectedPayload, string Status);

internal static class Receipts
{
    internal const string DirectoryPath = @"J:\SKILLHELPER\nstrap-runtime-work\tests\runtime-windows\receipts";
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static byte[] Canonical(ReceiptPayload payload, string status) => JsonSerializer.SerializeToUtf8Bytes(new { payload, status });
    internal static ProbeReceipt Seal(ReceiptPayload payload, string status) => new(payload,
        Convert.ToBase64String(DataProtection.Protect(Canonical(payload, status))), status);
    internal static ProbeReceipt Create(WindowsRuntimeFlagMemory memory, int original, int intended)
    {
        var payload = new ReceiptPayload(1, DateTimeOffset.UtcNow, Candidate.Flag, memory.ImagePath, memory.Identity.ExecutableSha256,
            memory.ImageSize, memory.ProcessId, memory.CreationTime, memory.Identity.ProcessSessionId,
            memory.ModuleBase, Candidate.Rva, original, intended);
        return Seal(payload, "PreparedBeforeWrite");
    }
    internal static string PersistNew(ProbeReceipt receipt)
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = System.IO.Path.Combine(DirectoryPath, $"fps-probe-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
        Persist(path, receipt, FileMode.CreateNew);
        return path;
    }
    internal static void Update(string path, ProbeReceipt receipt, string status)
    {
        // Keep the original recovery receipt intact if interrupted during a status update.
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Persist(temporary, Seal(receipt.Payload, status), FileMode.CreateNew);
        File.Move(temporary, path, overwrite: true);
    }
    private static void Persist(string path, ProbeReceipt receipt, FileMode mode)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Json);
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }
    internal static ProbeReceipt LoadValidated(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        if (!fullPath.StartsWith(System.IO.Path.GetFullPath(DirectoryPath) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Recovery receipt must be inside the runner's J: receipts directory.");
        var info = new FileInfo(fullPath);
        if (info.Length is <= 0 or > 16384) throw new IOException("Invalid receipt size.");
        ProbeReceipt receipt = JsonSerializer.Deserialize<ProbeReceipt>(File.ReadAllText(fullPath), Json) ?? throw new IOException("Receipt is missing.");
        Validate(receipt);
        return receipt;
    }
    internal static void Validate(ProbeReceipt receipt)
    {
        if (receipt.Payload is null || string.IsNullOrEmpty(receipt.ProtectedPayload)) throw new IOException("Receipt payload is missing.");
        byte[] protectedCopy = DataProtection.Unprotect(Convert.FromBase64String(receipt.ProtectedPayload));
        if (!CryptographicOperations.FixedTimeEquals(protectedCopy, Canonical(receipt.Payload, receipt.Status))) throw new IOException("Receipt payload/status was edited or authentication failed.");
        var p = receipt.Payload;
        if (p.Version != 1 || p.Flag != Candidate.Flag || !WindowsRuntimeFlagMemory.SamePath(p.Path, Candidate.Path)
            || !p.ExecutableSha256.Equals(Candidate.Hash, StringComparison.OrdinalIgnoreCase) || p.ImageSize != Candidate.ImageSize
            || p.RelativeVirtualAddress != Candidate.Rva || p.IntendedValue is not (30 or 60) || p.OriginalValue == p.IntendedValue
            || p.ProcessId <= 0 || p.CreationTime <= 0 || p.ModuleBase == 0)
            throw new IOException("Receipt is outside the pinned single-flag probe contract.");
    }
    internal static void Bind(ProbeReceipt receipt, WindowsRuntimeFlagMemory memory)
    {
        Validate(receipt);
        var p = receipt.Payload;
        if (p.ProcessId != memory.ProcessId || p.CreationTime != memory.CreationTime || p.ModuleBase != memory.ModuleBase
            || p.SessionId != memory.Identity.ProcessSessionId || !WindowsRuntimeFlagMemory.SamePath(p.Path, memory.ImagePath)
            || p.ExecutableSha256 != memory.Identity.ExecutableSha256 || p.ImageSize != memory.ImageSize)
            throw new IOException("Receipt belongs to a different process lifetime/build/base; recovery refused.");
    }
}

// A held exclusive file handle works across await/thread switches and releases after
// a process crash. Separate runner instances cannot modify the same process lifetime.
internal sealed class ProbeLease : IDisposable
{
    private readonly FileStream file;
    private ProbeLease(FileStream file) => this.file = file;
    internal static ProbeLease Acquire(RuntimeFlagSession session)
    {
        string directory = System.IO.Path.Combine(Receipts.DirectoryPath, "locks");
        Directory.CreateDirectory(directory);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.ProcessSessionId + ":" + session.ExecutableSha256)));
        try { return new(new FileStream(System.IO.Path.Combine(directory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException error) { throw new IOException("Another runner may own this process-lifetime probe/recovery lease; no write permitted.", error); }
    }
    public void Dispose() => file.Dispose();
}

// Windows DPAPI authenticates the readable receipt payload for this Windows account.
// It detects edited receipts; it is not a security boundary against that same account.
internal static class DataProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal int Length; internal nint Data; }
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nstrap.RuntimeWindows.FixedFpsProbe.Receipt.v1");
    internal static byte[] Protect(byte[] bytes) => Transform(bytes, true);
    internal static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
    private static unsafe byte[] Transform(byte[] bytes, bool protect)
    {
        fixed (byte* data = bytes)
        fixed (byte* entropy = Entropy)
        {
            var input = new Blob { Length = bytes.Length, Data = (nint)data };
            var optionalEntropy = new Blob { Length = Entropy.Length, Data = (nint)entropy };
            Blob output = default;
            bool success = protect ? CryptProtectData(ref input, "Nstrap reversible FPS probe", ref optionalEntropy, 0, 0, 1, out output)
                : CryptUnprotectData(ref input, 0, ref optionalEntropy, 0, 0, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Receipt protection failed.");
            try { byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { if (output.Data != 0) LocalFree(output.Data); }
        }
    }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, ref Blob entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
