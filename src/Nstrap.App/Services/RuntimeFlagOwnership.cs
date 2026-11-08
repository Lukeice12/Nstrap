using System.Buffers.Binary;
using System.IO;
using Nstrap.Core;

namespace Nstrap.App.Services;

internal sealed record RuntimeOwnedFlag(string Name, ulong Rva, RuntimeFlagScalarType Type,
    byte[] Original, byte[] Intended, byte[]? Pending);
internal sealed record RuntimeOwnershipState(int Version, string ImagePath, RuntimeFlagSession Session, List<RuntimeOwnedFlag> Flags);
internal sealed record RuntimeFlagLine(string Name, string Status, string Detail);
internal sealed record RuntimeOwnedApplyResult(RuntimeFlagBatchResult? Transaction, IReadOnlyList<RuntimeFlagLine> Lines);

/// <summary>Caller holds a process-lifetime lease for this complete operation.</summary>
internal static class RuntimeFlagOwnership
{
    internal static void Validate(RuntimeOwnershipState state, string path, RuntimeFlagSession session, RuntimeFlagMapDescriptor map)
    {
        if (state.Version != 1 || !string.Equals(Path.GetFullPath(state.ImagePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
            || state.Session != session || state.Flags is null || state.Flags.Count > map.Locations.Count
            || !string.Equals(session.ExecutableSha256, map.ExecutableSha256, StringComparison.OrdinalIgnoreCase)
            || session.ModuleImageSize != map.ModuleImageSize)
            throw new IOException("Runtime ownership belongs to a different installation, build or process lifetime.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in state.Flags)
        {
            var location = map.Locations.SingleOrDefault(item => item.Name == flag.Name);
            int width = flag.Type == RuntimeFlagScalarType.Boolean ? 1 : flag.Type == RuntimeFlagScalarType.Int32 ? 4 : 0;
            if (location is null || !seen.Add(flag.Name) || location.RelativeVirtualAddress != flag.Rva || location.Type != flag.Type
                || width == 0 || flag.Original?.Length != width || flag.Intended?.Length != width || (flag.Pending is not null && flag.Pending.Length != width)
                || (width == 1 && (flag.Original[0] > 1 || flag.Intended[0] > 1 || (flag.Pending is not null && flag.Pending[0] > 1))))
                throw new IOException("Runtime ownership has an invalid or unsupported scalar entry.");
        }
    }

    internal static RuntimeOwnedApplyResult Apply(IRuntimeFlagMemory memory, RuntimeFlagMapDescriptor map,
        RuntimeOwnershipState state, IReadOnlyList<RuntimeFlagRequest> requests, Action<RuntimeOwnershipState> persist, bool requireSameValue = false)
    {
        Validate(state, state.ImagePath, memory.Identity, map);
        var lines = new List<RuntimeFlagLine>();
        var eligible = new List<RuntimeFlagRequest>();
        var prepared = new List<RuntimeOwnedFlag>(state.Flags);
        var guards = new Dictionary<ulong, (byte[] Before, byte[] Intended)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            var location = map.Locations.SingleOrDefault(item => item.Name == request.Name);
            byte[]? intended = Encode(request.Value);
            if (location is null || intended is null || (location.Type == RuntimeFlagScalarType.Boolean ? intended.Length != 1 : location.Type != RuntimeFlagScalarType.Int32 || intended.Length != 4))
            { lines.Add(new(request.Name, "Skipped", "Unknown or unsupported runtime scalar/type.")); continue; }
            if (!names.Add(request.Name)) throw new IOException("Duplicate runtime request; no batch was written.");
            EnsureSession(memory, state.Session);
            byte[] current = Read(memory, location.RelativeVirtualAddress, intended.Length);
            if (requireSameValue && !current.SequenceEqual(intended))
            { lines.Add(new(request.Name, "Current changed — skipped", "Same-value check refused a different current value; no write made.")); continue; }
            var prior = prepared.SingleOrDefault(item => item.Name == request.Name);
            if (prior?.Pending is not null)
            { lines.Add(new(request.Name, "Unresolved pending ownership", "An earlier write lacks a committed receipt. Use Restore to inspect it; this apply was skipped.")); continue; }
            if (prior is not null && !current.SequenceEqual(prior.Original) && !current.SequenceEqual(prior.Intended)
                )
            { lines.Add(new(request.Name, "External change preserved", "Current bytes differ from all values owned by Nstrap. Restore or review this conflict first.")); continue; }
            // Never replace an earlier baseline with our own last write.
            var next = prior is null
                ? new RuntimeOwnedFlag(request.Name, location.RelativeVirtualAddress, location.Type, current, current, intended)
                : prior with { Pending = intended };
            if (prior is not null) prepared.Remove(prior);
            prepared.Add(next);
            guards.Add(location.RelativeVirtualAddress, (current, intended));
            eligible.Add(request);
        }
        if (eligible.Count == 0) return new(null, lines);
        EnsureSession(memory, state.Session);
        var journal = state with { Flags = prepared };
        // The authenticated journal precedes writes, but cannot prove a write
        // occurred. Pending values are not automatically treated as owned values.
        persist(journal);
        state.Flags.Clear(); state.Flags.AddRange(prepared);
        var transaction = RuntimeFlags.Apply(new PreparedMemory(memory, guards), state.Session, map, eligible);
        foreach (var result in transaction.Flags)
        {
            var request = eligible.Single(item => item.Name == result.Name);
            string requested = request.Value switch { RuntimeFlagValue.Boolean value => value.Value ? "true" : "false", RuntimeFlagValue.Int32 value => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => "unsupported" };
            lines.Add(new(result.Name, result.State.ToString(), $"Requested value: {requested}. {result.Detail}"));
            int index = state.Flags.FindIndex(flag => flag.Name == result.Name);
            var flag = state.Flags[index];
            if (result.State == RuntimeFlagState.AppliedReadBack)
                state.Flags[index] = flag with { Intended = flag.Pending!, Pending = null };
            else if (result.State is RuntimeFlagState.RolledBack or RuntimeFlagState.NotAttempted or RuntimeFlagState.PreflightRejected or RuntimeFlagState.SnapshotFailed)
                state.Flags[index] = flag with { Pending = null };
        }
        try { persist(state); }
        catch (Exception error) { lines.Add(new("Ownership journal", "Unresolved pending ownership", "Final receipt update failed. The pending journal does not prove ownership; automatic recovery will preserve ambiguous values. " + error.Message)); }
        return new(transaction, lines);
    }

    internal static IReadOnlyList<RuntimeFlagLine> Restore(IRuntimeFlagMemory memory, RuntimeFlagMapDescriptor map,
        RuntimeOwnershipState state, Action<RuntimeOwnershipState> persist, ISet<string>? onlyNames = null)
    {
        Validate(state, state.ImagePath, memory.Identity, map);
        var lines = new List<RuntimeFlagLine>();
        foreach (var flag in state.Flags.Where(flag => onlyNames is null || onlyNames.Contains(flag.Name)).ToArray())
        {
            try
            {
                EnsureSession(memory, state.Session);
                byte[] current = Read(memory, flag.Rva, flag.Original.Length);
                if (!current.SequenceEqual(flag.Original))
                {
                    if (flag.Pending is not null && !current.SequenceEqual(flag.Intended))
                    { lines.Add(new(flag.Name, "Unresolved pending ownership", "Prepared journal cannot prove the last write occurred. Current value preserved; manual review required.")); continue; }
                    if (!current.SequenceEqual(flag.Intended))
                    { lines.Add(new(flag.Name, "External change preserved", "Current bytes are not an owned value; runtime restore skipped.")); continue; }
                    EnsureSession(memory, state.Session);
                    // Repeat the comparison immediately before writing. This is still
                    // not atomic against another process writing at the same moment.
                    if (!Read(memory, flag.Rva, current.Length).SequenceEqual(current))
                    { lines.Add(new(flag.Name, "External change preserved", "Value changed during restore; no write made.")); continue; }
                    if (memory.Write(flag.Rva, flag.Original) != flag.Original.Length) throw new IOException("Incomplete runtime restore write.");
                    EnsureSession(memory, state.Session);
                    if (!Read(memory, flag.Rva, flag.Original.Length).SequenceEqual(flag.Original)) throw new IOException("Runtime restore read-back mismatch.");
                }
                EnsureSession(memory, state.Session);
                state.Flags.Remove(flag);
                persist(state);
                string original = flag.Type == RuntimeFlagScalarType.Boolean ? (flag.Original[0] == 1 ? "true" : "false")
                    : BinaryPrimitives.ReadInt32LittleEndian(flag.Original).ToString(System.Globalization.CultureInfo.InvariantCulture);
                lines.Add(new(flag.Name, "Restored", $"Original value {original} is present and verified."));
            }
            catch (Exception error) { lines.Add(new(flag.Name, "Restore error", error.Message + " Recovery evidence retained.")); }
        }
        return lines;
    }

    internal static byte[] Read(IRuntimeFlagMemory memory, ulong rva, int count)
    {
        byte[] bytes = new byte[count];
        if (memory.Read(rva, bytes) != count) throw new IOException("Incomplete runtime scalar read.");
        return bytes;
    }
    private static void EnsureSession(IRuntimeFlagMemory memory, RuntimeFlagSession expected)
    { if (memory.Identity != expected) throw new IOException("The runtime process lifetime changed; no further operation is permitted."); }
    private static byte[]? Encode(RuntimeFlagValue value)
    {
        if (value is RuntimeFlagValue.Boolean boolean) return [boolean.Value ? (byte)1 : (byte)0];
        if (value is RuntimeFlagValue.Int32 integer)
        { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, integer.Value); return bytes; }
        return null;
    }
    // The journal snapshot, not the core's later snapshot, authorizes a new write.
    // Both new writes and rollback are restricted to this prepared two-value pair.
    private sealed class PreparedMemory(IRuntimeFlagMemory inner, Dictionary<ulong, (byte[] Before, byte[] Intended)> guards) : IRuntimeFlagMemory
    {
        private readonly HashSet<ulong> nativeWriteAttempted = [];
        public RuntimeFlagSession Identity => inner.Identity;
        public int Read(ulong rva, Span<byte> destination) => inner.Read(rva, destination);
        public int Write(ulong rva, ReadOnlySpan<byte> source)
        {
            if (!guards.TryGetValue(rva, out var guard)) throw new IOException("Write is outside the prepared runtime batch.");
            bool applying = source.SequenceEqual(guard.Intended);
            byte[] expected = applying ? guard.Before
                : source.SequenceEqual(guard.Before) ? guard.Intended : throw new IOException("Write bytes are outside the prepared runtime values.");
            // A failed prewrite comparison never authorizes undoing a value that
            // another writer happened to set to our intended bytes. Track this
            // separately for each RVA; another flag's attempt is not ownership.
            if (!applying && !nativeWriteAttempted.Contains(rva))
                throw new IOException("No native write was attempted for this flag; external intended bytes will not be restored.");
            if (!RuntimeFlagOwnership.Read(inner, rva, source.Length).SequenceEqual(expected))
                throw new IOException("Runtime bytes changed after the prepared journal snapshot; write refused.");
            // Set before entering the adapter: partial/throwing native operations
            // may still require the core's conditional, read-back-verified rollback.
            nativeWriteAttempted.Add(rva);
            return inner.Write(rva, source);
        }
    }
}
