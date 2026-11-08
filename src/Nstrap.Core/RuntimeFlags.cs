using System.Buffers.Binary;

namespace Nstrap.Core;

public enum RuntimeFlagScalarType { Boolean, Int32, String }
public abstract record RuntimeFlagValue
{
    private RuntimeFlagValue() { }
    public sealed record Boolean(bool Value) : RuntimeFlagValue;
    public sealed record Int32(int Value) : RuntimeFlagValue;
    public sealed record String(string Value) : RuntimeFlagValue;
}

public sealed record RuntimeFlagRequest(string Name, RuntimeFlagValue Value);
public sealed record RuntimeFlagLocation(string Name, ulong RelativeVirtualAddress, RuntimeFlagScalarType Type);

/// <summary>
/// A caller-supplied trusted map for one exact executable build. There is deliberately
/// no address discovery, JSON loader, or inference from a flag's name or prefix.
/// </summary>
public sealed record RuntimeFlagMapDescriptor(string ExecutableSha256, ulong ModuleImageSize,
    IReadOnlyList<RuntimeFlagLocation> Locations);

/// <summary>ProcessSessionId must identify one process lifetime, not merely a reusable PID.</summary>
public sealed record RuntimeFlagSession(string ProcessSessionId, string ExecutableSha256, ulong ModuleImageSize);

/// <summary>
/// Adapter operations are relative to one pinned main module in one process lifetime.
/// Read/Write return the actual byte count, or throw. An adapter must not retarget a
/// reused PID. This interface does not provide synchronization with external writers.
/// </summary>
public interface IRuntimeFlagMemory
{
    RuntimeFlagSession Identity { get; }
    int Read(ulong relativeVirtualAddress, Span<byte> destination);
    int Write(ulong relativeVirtualAddress, ReadOnlySpan<byte> source);
}

public enum RuntimeFlagState
{
    NotAttempted, PreflightRejected, SnapshotFailed, AppliedReadBack,
    WriteFailed, ReadbackFailed, RolledBack, RollbackConflict, RollbackFailed, SessionChanged
}

/// <summary>Original/intended/last-observed bytes are retained for diagnosis and recovery.</summary>
public sealed record RuntimeFlagOutcome(string Name, RuntimeFlagState State, string Detail,
    IReadOnlyList<byte>? OriginalBytes, IReadOnlyList<byte>? IntendedBytes,
    IReadOnlyList<byte>? LastObservedBytes, int? ReportedWrittenBytes);

/// <summary>
/// Read-back success verifies bytes only, never a flag's gameplay-visible effect.
/// Failure can leave partial or externally changed values; inspect every outcome.
/// Rollback comparison and write are not atomic against concurrent external writers.
/// </summary>
public sealed record RuntimeFlagBatchResult(bool PreflightPassed, bool AllWritesReadBack,
    IReadOnlyList<RuntimeFlagOutcome> Flags, string? Error);

public static class RuntimeFlags
{
    public static RuntimeFlagBatchResult Apply(IRuntimeFlagMemory memory, RuntimeFlagSession expectedSession,
        RuntimeFlagMapDescriptor trustedMap, IEnumerable<RuntimeFlagRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(expectedSession);
        ArgumentNullException.ThrowIfNull(trustedMap);
        ArgumentNullException.ThrowIfNull(requests);
        var work = requests.Select(request => new Work(request)).ToArray();
        string? mapError = ValidateMap(trustedMap, expectedSession, out var locations);
        var duplicates = work.GroupBy(item => item.Request.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var item in work)
        {
            if (mapError is not null) Reject(item, mapError);
            else if (string.IsNullOrWhiteSpace(item.Request.Name)) Reject(item, "A flag name is required.");
            else if (duplicates.Contains(item.Request.Name)) Reject(item, "Duplicate requested flag.");
            else if (!locations.TryGetValue(item.Request.Name, out var location)) Reject(item, "Unknown flag: no entry in the trusted map.");
            else if (item.Request.Value is RuntimeFlagValue.String || location.Type == RuntimeFlagScalarType.String)
                Reject(item, "String flags are unsupported.");
            else
            {
                item.Location = location;
                item.Intended = Encode(item.Request.Value, location.Type);
                if (item.Intended is null) Reject(item, "Requested value type does not match the trusted map.");
            }
        }
        if (mapError is not null || work.Any(item => item.State == RuntimeFlagState.PreflightRejected))
            return Result(false, false, work, mapError ?? "The entire batch was rejected before memory I/O.");
        if (!SameSession(memory, expectedSession, out var identityError))
        {
            foreach (var item in work) Reject(item, identityError!);
            return Result(false, false, work, identityError);
        }

        // Snapshot the entire batch before the first write. A failed read leaves all memory untouched.
        foreach (var item in work)
        {
            try
            {
                EnsureSession(memory, expectedSession);
                item.Original = ReadExact(memory, item.Location!.RelativeVirtualAddress, item.Intended!.Length);
                EnsureSession(memory, expectedSession);
            }
            catch (Exception error)
            {
                item.State = error is SessionException ? RuntimeFlagState.SessionChanged : RuntimeFlagState.SnapshotFailed;
                item.Detail = error.Message;
                return Result(true, false, work, "Snapshot failed; no writes were attempted. " + error.Message);
            }
        }

        foreach (var item in work)
        {
            try
            {
                EnsureSession(memory, expectedSession);
                // Do not overwrite changes made since the initial snapshot.
                item.Observed = ReadExact(memory, item.Location!.RelativeVirtualAddress, item.Intended!.Length);
                if (!item.Observed.SequenceEqual(item.Original!))
                    throw new IOException("The value changed after its snapshot; this write was skipped.");
                EnsureSession(memory, expectedSession);
                item.WriteAttempted = true;
                item.ReportedWritten = memory.Write(item.Location.RelativeVirtualAddress, item.Intended);
                if (item.ReportedWritten != item.Intended.Length)
                    throw new IOException($"Incomplete write: reported {item.ReportedWritten} of {item.Intended.Length} bytes.");
                item.State = RuntimeFlagState.ReadbackFailed;
                EnsureSession(memory, expectedSession);
                item.Observed = ReadExact(memory, item.Location.RelativeVirtualAddress, item.Intended.Length);
                if (!item.Observed.SequenceEqual(item.Intended)) throw new IOException("Written bytes did not match read-back.");
                EnsureSession(memory, expectedSession);
                item.State = RuntimeFlagState.AppliedReadBack;
                item.Detail = "Requested bytes read back; runtime-visible effect is not verified.";
            }
            catch (Exception error)
            {
                item.State = error is SessionException ? RuntimeFlagState.SessionChanged
                    : item.State == RuntimeFlagState.ReadbackFailed ? RuntimeFlagState.ReadbackFailed : RuntimeFlagState.WriteFailed;
                item.Detail = error.Message;
                RollBack(memory, expectedSession, work);
                return Result(true, false, work, "Apply failed. " + error.Message);
            }
        }
        return Result(true, true, work, null);
    }

    private static void RollBack(IRuntimeFlagMemory memory, RuntimeFlagSession session, Work[] work)
    {
        foreach (var item in work.Reverse().Where(item => item.WriteAttempted))
        {
            string failure = item.Detail;
            try
            {
                EnsureSession(memory, session);
                item.Observed = ReadExact(memory, item.Location!.RelativeVirtualAddress, item.Intended!.Length);
                if (item.Observed.SequenceEqual(item.Original!))
                {
                    item.State = RuntimeFlagState.RolledBack;
                    item.Detail = "Original bytes are present; no rollback write needed. " + failure;
                    continue;
                }
                // Partial/throwing writes are ambiguous. Only a complete intended value
                // can be attributed for restoration; retain all other observed bytes.
                if (!item.Observed.SequenceEqual(item.Intended))
                {
                    item.State = RuntimeFlagState.RollbackConflict;
                    item.Detail = "Current bytes differ from both original and intended bytes; preserved for review. " + failure;
                    continue;
                }
                EnsureSession(memory, session);
                int restored = memory.Write(item.Location.RelativeVirtualAddress, item.Original!);
                if (restored != item.Original!.Length) throw new IOException("Rollback write was incomplete.");
                EnsureSession(memory, session);
                item.Observed = ReadExact(memory, item.Location.RelativeVirtualAddress, item.Original.Length);
                if (!item.Observed.SequenceEqual(item.Original)) throw new IOException("Rollback bytes did not match read-back.");
                EnsureSession(memory, session);
                item.State = RuntimeFlagState.RolledBack;
                item.Detail = "Original bytes restored and read back. " + failure;
            }
            catch (Exception error)
            {
                item.State = error is SessionException ? RuntimeFlagState.SessionChanged : RuntimeFlagState.RollbackFailed;
                item.Detail = "Rollback stopped: " + error.Message + " " + failure;
            }
        }
        // Comparisons preserve observed external changes. They are not an atomic CAS:
        // a writer racing between a comparison and Write requires target synchronization.
    }

    private static string? ValidateMap(RuntimeFlagMapDescriptor map, RuntimeFlagSession session,
        out Dictionary<string, RuntimeFlagLocation> locations)
    {
        locations = new(StringComparer.Ordinal);
        if (!ValidHash(map.ExecutableSha256) || !ValidHash(session.ExecutableSha256)) return "A full executable SHA-256 is required.";
        if (string.IsNullOrWhiteSpace(session.ProcessSessionId)) return "A process-lifetime session identity is required.";
        if (!string.Equals(map.ExecutableSha256, session.ExecutableSha256, StringComparison.OrdinalIgnoreCase)
            || map.ModuleImageSize != session.ModuleImageSize) return "The trusted map does not match this executable build and image size.";
        if (map.ModuleImageSize == 0 || map.Locations is null) return "The trusted map has no valid image range or entries.";
        var ranges = new List<(ulong Start, ulong End)>();
        foreach (var location in map.Locations.ToArray())
        {
            if (location is null || string.IsNullOrWhiteSpace(location.Name) || !locations.TryAdd(location.Name, location))
                return "The trusted map contains a missing or duplicate flag name.";
            int width = location.Type switch { RuntimeFlagScalarType.Boolean => 1, RuntimeFlagScalarType.Int32 => 4, RuntimeFlagScalarType.String => 0, _ => -1 };
            if (width < 0) return "The trusted map contains an unknown type.";
            if (width == 0) continue; // Selected strings are explicitly rejected, never accessed.
            if (map.ModuleImageSize < (ulong)width || location.RelativeVirtualAddress > map.ModuleImageSize - (ulong)width)
                return "A trusted map range is outside the module image.";
            if (width == 4 && location.RelativeVirtualAddress % 4 != 0) return "A trusted map Int32 address is not four-byte aligned.";
            ranges.Add((location.RelativeVirtualAddress, checked(location.RelativeVirtualAddress + (ulong)width)));
        }
        var sorted = ranges.OrderBy(range => range.Start).ToArray();
        for (int index = 1; index < sorted.Length; index++)
            if (sorted[index].Start < sorted[index - 1].End) return "Trusted map scalar ranges overlap.";
        return null;
    }

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static byte[]? Encode(RuntimeFlagValue value, RuntimeFlagScalarType type)
    {
        if (value is RuntimeFlagValue.Boolean boolean && type == RuntimeFlagScalarType.Boolean) return [boolean.Value ? (byte)1 : (byte)0];
        if (value is RuntimeFlagValue.Int32 integer && type == RuntimeFlagScalarType.Int32)
        {
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, integer.Value);
            return bytes;
        }
        return null;
    }
    private static byte[] ReadExact(IRuntimeFlagMemory memory, ulong rva, int length)
    {
        byte[] bytes = new byte[length];
        int read = memory.Read(rva, bytes);
        if (read != length) throw new IOException($"Incomplete read: reported {read} of {length} bytes.");
        return bytes;
    }
    private static bool SameSession(IRuntimeFlagMemory memory, RuntimeFlagSession expected, out string? error)
    {
        try
        {
            var actual = memory.Identity;
            if (actual.ProcessSessionId == expected.ProcessSessionId && actual.ModuleImageSize == expected.ModuleImageSize
                && string.Equals(actual.ExecutableSha256, expected.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            { error = null; return true; }
            error = "The process lifetime or executable build changed.";
        }
        catch (Exception ex) { error = "The process identity could not be checked: " + ex.Message; }
        return false;
    }
    private static void EnsureSession(IRuntimeFlagMemory memory, RuntimeFlagSession session)
    {
        if (!SameSession(memory, session, out string? error)) throw new SessionException(error!);
    }
    private static void Reject(Work work, string detail) { work.State = RuntimeFlagState.PreflightRejected; work.Detail = detail; }
    private static RuntimeFlagBatchResult Result(bool preflight, bool success, Work[] work, string? error) => new(preflight, success,
        Array.AsReadOnly(work.Select(item => new RuntimeFlagOutcome(item.Request.Name, item.State, item.Detail,
            Freeze(item.Original), Freeze(item.Intended), Freeze(item.Observed), item.ReportedWritten)).ToArray()), error);
    private static IReadOnlyList<byte>? Freeze(byte[]? value) => value is null ? null : Array.AsReadOnly(value.ToArray());
    private sealed class SessionException(string message) : IOException(message);
    private sealed class Work(RuntimeFlagRequest request)
    {
        public RuntimeFlagRequest Request { get; } = request ?? throw new ArgumentException("A flag request is missing.");
        public RuntimeFlagLocation? Location;
        public byte[]? Original, Intended, Observed;
        public int? ReportedWritten;
        public bool WriteAttempted;
        public RuntimeFlagState State = RuntimeFlagState.NotAttempted;
        public string Detail = "No write attempted.";
    }
}
