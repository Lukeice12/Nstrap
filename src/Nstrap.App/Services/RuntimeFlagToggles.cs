using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using Nstrap.Core;

namespace Nstrap.App.Services;

internal sealed record RuntimeLiveFlagState(string Name, string SavedValue, string CurrentValue, bool IsApplied,
    bool CanToggle, string Status, string Detail);
internal sealed record RuntimeLiveSnapshot(RuntimeFlagSession? Session, string Summary, IReadOnlyList<RuntimeLiveFlagState> Flags);

internal sealed partial class RuntimeFlagService
{
    // Read-only process access. Neither refresh nor profile selection applies a flag.
    internal RuntimeLiveSnapshot ReadLiveFlags(RobloxInstallation installation, FlagProfile profile)
    {
        try
        {
            var processes = find(installation.ExecutablePath);
            if (installation.Kind != ClientKind.Player || processes.Count != 1)
                throw new IOException("Select one running Player, then refresh live status.");
            using var target = inspectOpen(processes[0], installation.ExecutablePath, map.Descriptor);
            var session = target.Memory.Identity;
            using var lease = store.Acquire(session);
            var ownership = store.Load(target.ImagePath, session, map.Descriptor);
            RuntimeFlagOwnership.Validate(ownership, target.ImagePath, session, map.Descriptor);
            var rows = new List<RuntimeLiveFlagState>();
            foreach (var flag in profile.Flags)
            {
                // Profile Enabled is deliberately not consulted: this control is
                // an explicit per-flag action, separate from batch profile selection.
                bool validSavedValue = map.TryRequest(flag, out var request, out string reason);
                var location = map.Descriptor.Locations.SingleOrDefault(item => item.Name == flag.Name);
                if (location is null)
                { rows.Add(new(flag.Name, flag.Value, "—", false, false, "Unsupported", reason)); continue; }
                try
                {
                    map.VerifyName(target.Memory, target.ModuleBase, flag.Name);
                    byte[] current = RuntimeFlagOwnership.Read(target.Memory, location.RelativeVirtualAddress, location.Type == RuntimeFlagScalarType.Boolean ? 1 : 4);
                    string value = FormatScalar(location.Type, current);
                    var owned = ownership.Flags.SingleOrDefault(item => item.Name == flag.Name);
                    if (owned?.Pending is not null)
                    { rows.Add(new(flag.Name, flag.Value, value, false, false, "Pending review", "An earlier write has no committed receipt. Current value is preserved; review Live results.")); continue; }
                    if (owned is not null && current.SequenceEqual(owned.Intended))
                    {
                        bool sameSaved = request?.Value switch
                        {
                            RuntimeFlagValue.Boolean boolean => current[0] == (boolean.Value ? 1 : 0),
                            RuntimeFlagValue.Int32 integer => BinaryPrimitives.ReadInt32LittleEndian(current) == integer.Value,
                            _ => false
                        };
                        rows.Add(new(flag.Name, flag.Value, value, true, true, sameSaved ? "On — applied" : "On — previous value",
                            sameSaved ? "Committed owned value matches current memory. Off restores this flag's original value."
                                : validSavedValue ? "Current memory matches an earlier committed apply, not this profile's saved value. Off/on applies the new saved value."
                                : "The saved value is invalid. Off remains available to restore this flag's committed original."));
                    }
                    else if (owned is not null && !current.SequenceEqual(owned.Original))
                        rows.Add(new(flag.Name, flag.Value, value, false, false, "Conflict", "Current memory differs from Nstrap's owned values. External change preserved."));
                    else if (location.Type == RuntimeFlagScalarType.Boolean && current[0] > 1)
                        rows.Add(new(flag.Name, flag.Value, value, false, false, "Unverified", "Boolean storage is outside the reviewed 0/1 domain."));
                    else rows.Add(new(flag.Name, flag.Value, value, false, validSavedValue, !validSavedValue ? "Invalid saved value" : owned is null ? "Off — not owned" : "Off — original",
                        !validSavedValue ? reason : owned is null ? "No committed value is owned by Nstrap for this flag in this process." : "The original value is currently present."));
                }
                catch (Exception error) { rows.Add(new(flag.Name, flag.Value, "—", false, false, "Unavailable", error.Message)); }
            }
            if (target.Memory.Identity != session) throw new IOException("Player lifetime changed while reading status; refresh before toggling.");
            return new(session, $"{rows.Count(row => row.IsApplied)} owned live values on · {rows.Count(row => row.CanToggle)} controls ready · PID {processes[0]}", rows);
        }
        catch (Exception error)
        {
            return new(null, error.Message, profile.Flags.Select(flag =>
            {
                bool supported = map.TryRequest(flag, out _, out var reason);
                return new RuntimeLiveFlagState(flag.Name, flag.Value, "—", false, false, supported ? "Unavailable" : "Unsupported", supported ? error.Message : reason);
            }).ToArray());
        }
    }

    internal RuntimeFlagReport SetLiveFlag(RobloxInstallation installation, FlagEntry flag, bool turnOn,
        RuntimeFlagSession expectedSession, CancellationToken cancellationToken = default, bool requireSameValue = false)
    {
        int? pid = null;
        var lines = new List<RuntimeFlagLine>(); RuntimeFlagBatchResult? transaction = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool validSavedValue = map.TryRequest(flag, out var request, out string reason);
            if (!map.Descriptor.Locations.Any(item => item.Name == flag.Name) || (turnOn && !validSavedValue))
                return Finish(new(DateTimeOffset.UtcNow, "Live toggle", installation.ExecutablePath, null, $"{flag.Name}: live toggle needs attention; this toggle is unavailable.", [new(flag.Name, "Skipped", reason)]));
            var processes = find(installation.ExecutablePath);
            if (installation.Kind != ClientKind.Player || processes.Count != 1) throw new IOException("One exact selected running Player is required.");
            pid = processes[0];
            using var target = open(pid.Value, installation.ExecutablePath, map.Descriptor);
            if (target.Memory.Identity != expectedSession) throw new IOException("Player lifetime changed since the displayed status. Refresh before toggling.");
            using var lease = store.Acquire(expectedSession);
            var state = store.Load(target.ImagePath, expectedSession, map.Descriptor);
            RuntimeFlagOwnership.Validate(state, target.ImagePath, expectedSession, map.Descriptor);
            if (turnOn)
            {
                string proof = map.Verify(target.Memory, target.ModuleBase, flag.Name);
                cancellationToken.ThrowIfCancellationRequested();
                // This is a genuine scoped apply: no removed/disabled reconciliation
                // and no reapplication or restoration of any other owned flag.
                var result = RuntimeFlagOwnership.Apply(target.Memory, map.Descriptor, state, [request!], store.Save, requireSameValue);
                transaction = result.Transaction;
                lines.AddRange(result.Lines.Select(line => line with { Detail = line.Detail + " Evidence: " + proof + ". " + map.Constraint(flag.Name) }));
            }
            else if (state.Flags.Any(item => item.Name == flag.Name))
                lines.AddRange(RestoreVerified(target, state, new HashSet<string>(StringComparer.Ordinal) { flag.Name }));
            else lines.Add(new(flag.Name, "Off", "No committed value is owned by Nstrap for this flag in this process; no write made."));
            bool succeeded = turnOn ? transaction?.AllWritesReadBack == true : lines.All(line => line.Status is "Restored" or "Off");
            string summary = succeeded ? $"{flag.Name}: {(turnOn ? "saved value applied and read back" : "off; original ownership restored")}."
                : $"{flag.Name}: live toggle needs attention; inspect its result.";
            return Finish(new(DateTimeOffset.UtcNow, "Live toggle", installation.ExecutablePath, pid, summary, lines, transaction));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Finish(new(DateTimeOffset.UtcNow, "Live toggle", installation.ExecutablePath, pid,
                "Runtime operation stopped: " + error.Message, [new(flag.Name, "Not changed", error.Message)], transaction));
        }
    }
    private static string FormatScalar(RuntimeFlagScalarType type, byte[] bytes) => type == RuntimeFlagScalarType.Boolean
        ? bytes[0] switch { 0 => "false", 1 => "true", _ => bytes[0].ToString(CultureInfo.InvariantCulture) }
        : BinaryPrimitives.ReadInt32LittleEndian(bytes).ToString(CultureInfo.InvariantCulture);
}
