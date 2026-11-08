using System.Diagnostics;
using System.IO;
using Nstrap.Core;

namespace Nstrap.App.Services;

internal sealed record RuntimeFlagReport(DateTimeOffset CreatedUtc, string Operation, string ImagePath, int? ProcessId,
    string Summary, IReadOnlyList<RuntimeFlagLine> Flags, RuntimeFlagBatchResult? Transaction = null, string? ReportPath = null)
{
    internal bool RequiresAttention => Summary.StartsWith("Runtime operation stopped:", StringComparison.Ordinal)
        || Transaction is { AllWritesReadBack: false }
        || Summary.Contains(": live toggle needs attention;", StringComparison.Ordinal)
        || Summary.StartsWith("Runtime readiness timed out", StringComparison.Ordinal)
        || Flags.Any(flag => flag.Status is "Restore error" or "RollbackFailed" or "RollbackConflict" or "SessionChanged"
            or "WriteFailed" or "ReadbackFailed" or "External change preserved" or "Unresolved pending ownership"
            or "Restore unverified — skipped");
    internal string Detail => Summary + "\n" + string.Join("\n", Flags.Select(flag => $"{flag.Name}: {flag.Status} — {flag.Detail}"))
        + (ReportPath is null ? "" : "\n\nReport: " + ReportPath);
}

internal sealed partial class RuntimeFlagService
{
    private readonly RuntimeFlagMap map;
    private readonly RuntimeFlagStore store;
    private readonly Func<string, List<int>> find;
    private readonly Func<int, string, RuntimeFlagMapDescriptor, IRuntimeFlagTarget> open;
    private readonly Func<int, string, RuntimeFlagMapDescriptor, IRuntimeFlagTarget> inspectOpen;
    internal RuntimeFlagService(string? runtimeDirectory = null) : this(RuntimeFlagMap.Load(), new(runtimeDirectory ?? RuntimeFlagStore.DefaultDirectory),
        FindExact, (pid, path, descriptor) => new NativeRuntimeFlagTarget(WindowsRuntimeFlagMemory.OpenExact(pid, path, descriptor.ExecutableSha256, descriptor.ModuleImageSize, writable: true)),
        (pid, path, descriptor) => new NativeRuntimeFlagTarget(WindowsRuntimeFlagMemory.OpenExact(pid, path, descriptor.ExecutableSha256, descriptor.ModuleImageSize, writable: false))) { }
    internal RuntimeFlagService(RuntimeFlagMap map, RuntimeFlagStore store, Func<string, List<int>> find,
        Func<int, string, RuntimeFlagMapDescriptor, IRuntimeFlagTarget> open, Func<int, string, RuntimeFlagMapDescriptor, IRuntimeFlagTarget>? inspectOpen = null)
    { this.map = map; this.store = store; this.find = find; this.open = open; this.inspectOpen = inspectOpen ?? open; }
    internal RuntimeFlagReport Apply(RobloxInstallation installation, FlagProfile profile, CancellationToken cancellationToken = default)
        => Run(installation, profile, restore: false, cancellationToken);
    internal RuntimeFlagReport Restore(RobloxInstallation installation) => Run(installation, null, restore: true, CancellationToken.None);
    internal RuntimeFlagReport Inspect(RobloxInstallation installation, FlagProfile profile)
    {
        var lines = new List<RuntimeFlagLine>(); int? pid = null;
        try
        {
            var processes = find(installation.ExecutablePath);
            if (installation.Kind != ClientKind.Player || processes.Count != 1) throw new IOException("Inspection requires one exact selected running Player.");
            pid = processes[0];
            using var target = inspectOpen(pid.Value, installation.ExecutablePath, map.Descriptor);
            foreach (var entry in profile.Flags)
            {
                if (!entry.Enabled) { lines.Add(new(entry.Name, "Disabled", "No live value requested.")); continue; }
                if (!map.TryRequest(entry, out _, out var reason)) { lines.Add(new(entry.Name, "Skipped", reason)); continue; }
                try
                {
                    string proof = map.Verify(target.Memory, target.ModuleBase, entry.Name);
                    var location = map.Descriptor.Locations.Single(item => item.Name == entry.Name);
                    byte[] bytes = RuntimeFlagOwnership.Read(target.Memory, location.RelativeVirtualAddress, location.Type == RuntimeFlagScalarType.Boolean ? 1 : 4);
                    string current = location.Type == RuntimeFlagScalarType.Boolean ? (bytes[0] == 1 ? "true" : "false")
                        : System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    lines.Add(new(entry.Name, "Verified", $"Requested: {entry.Value}. Current: {current}. Evidence: {proof}. {map.Constraint(entry.Name)}"));
                }
                catch (Exception error) { lines.Add(new(entry.Name, "Unverified — skipped", error.Message)); }
            }
            int verified = lines.Count(line => line.Status == "Verified"), enabled = profile.Flags.Count(flag => flag.Enabled);
            return Finish(new(DateTimeOffset.UtcNow, "Inspect", installation.ExecutablePath, pid,
                $"Live values verified: {verified}/{enabled}; {enabled - verified} skipped. Read-only: no process writes. Stored values verified; gameplay effects are not measured.", lines));
        }
        catch (Exception error) { return Finish(new(DateTimeOffset.UtcNow, "Inspect", installation.ExecutablePath, pid, "Runtime operation stopped: " + error.Message, lines)); }
    }
    internal async Task<RuntimeFlagReport> ApplyWhenReadyAsync(RobloxInstallation installation, FlagProfile profile, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        string waiting = "No exact selected Player appeared.";
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = find(installation.ExecutablePath);
            if (processes.Count == 1)
            {
                try
                {
                    // Read-only readiness checks may repeat; actual application runs
                    // once. This avoids racing the client's initial flag registration.
                    bool ready = await Task.Run(() =>
                    {
                        using var target = open(processes[0], installation.ExecutablePath, map.Descriptor);
                        var mapped = profile.Flags.Where(entry => entry.Enabled && map.TryRequest(entry, out _, out _)).ToArray();
                        if (mapped.Length == 0) return true;
                        foreach (var entry in mapped)
                        {
                            try { map.Verify(target.Memory, target.ModuleBase, entry.Name); return true; }
                            catch (Exception error) { waiting = error.Message; }
                        }
                        return false;
                    }, cancellationToken);
                    if (ready) return await Task.Run(() => Apply(installation, profile, cancellationToken), cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException) { waiting = error.Message; }
            }
            else if (processes.Count > 1) waiting = "Multiple exact selected Players make the target ambiguous.";
            await Task.Delay(500, cancellationToken);
        }
        return Finish(new(DateTimeOffset.UtcNow, "Apply", installation.ExecutablePath, null,
            "Runtime readiness timed out; no live values were written. " + waiting, []));
    }
    private RuntimeFlagReport Run(RobloxInstallation installation, FlagProfile? profile, bool restore, CancellationToken token)
    {
        var lines = new List<RuntimeFlagLine>();
        string operation = restore ? "Restore" : "Apply";
        int? pid = null;
        RuntimeFlagBatchResult? transaction = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (installation.Kind != ClientKind.Player)
                return Finish(new(DateTimeOffset.UtcNow, operation, installation.ExecutablePath, null, "Live runtime flags are supported for Player only.", []));
            var processes = find(installation.ExecutablePath);
            if (processes.Count != 1)
            {
                string reason = processes.Count == 0 ? "The exact selected Player is not running; no live values were written."
                    : "More than one exact selected Player is running; runtime target is ambiguous and no live values were written.";
                if (profile is not null) lines.AddRange(profile.Flags.Where(flag => flag.Enabled).Select(flag => new RuntimeFlagLine(flag.Name, "Not applied live", reason)));
                return Finish(new(DateTimeOffset.UtcNow, operation, installation.ExecutablePath, null, reason, lines));
            }
            pid = processes[0];
            using var target = open(pid.Value, installation.ExecutablePath, map.Descriptor);
            var memory = target.Memory;
            using var lease = store.Acquire(memory.Identity);
            var state = store.Load(target.ImagePath, memory.Identity, map.Descriptor);
            if (restore)
            {
                lines.AddRange(RestoreVerified(target, state, state.Flags.Select(flag => flag.Name).ToHashSet(StringComparer.Ordinal)));
                int restored = lines.Count(line => line.Status == "Restored");
                return Finish(new(DateTimeOffset.UtcNow, operation, target.ImagePath, pid,
                    $"Live restore: {restored} original value(s) verified; {lines.Count - restored} unresolved. Only committed owned values were eligible.", lines));
            }
            var activeNames = profile!.Flags.Where(flag => flag.Enabled).Select(flag => flag.Name).ToHashSet(StringComparer.Ordinal);
            var removedNames = state.Flags.Where(flag => !activeNames.Contains(flag.Name)).Select(flag => flag.Name).ToHashSet(StringComparer.Ordinal);
            // Active but unverified entries remain untouched; only removal/disable
            // requests restoration of earlier committed runtime ownership.
            lines.AddRange(RestoreVerified(target, state, removedNames));
            var requests = new List<RuntimeFlagRequest>();
            var proofs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in profile!.Flags)
            {
                token.ThrowIfCancellationRequested();
                if (!entry.Enabled) { lines.Add(new(entry.Name, "Disabled", "No live write requested.")); continue; }
                if (!map.TryRequest(entry, out var request, out var reason)) { lines.Add(new(entry.Name, "Skipped", reason)); continue; }
                try
                {
                    proofs[entry.Name] = map.Verify(memory, target.ModuleBase, entry.Name);
                    requests.Add(request!);
                }
                catch (Exception error) { lines.Add(new(entry.Name, "Unverified — skipped", error.Message)); }
            }
            token.ThrowIfCancellationRequested();
            var applied = RuntimeFlagOwnership.Apply(memory, map.Descriptor, state, requests, store.Save);
            transaction = applied.Transaction;
            lines.AddRange(applied.Lines.Select(line => proofs.TryGetValue(line.Name, out var proof)
                ? line with { Detail = line.Detail + " Evidence: " + proof + ". " + map.Constraint(line.Name) } : line));
            int verified = transaction?.Flags.Count(flag => flag.State == RuntimeFlagState.AppliedReadBack) ?? 0;
            int enabled = profile.Flags.Count(flag => flag.Enabled);
            string summary = $"Live values verified: {verified}/{enabled}; {enabled - verified} skipped, unchanged or failed. "
                + "Stored values verified; gameplay effects are not measured.";
            int restoredRemoved = lines.Count(line => line.Status == "Restored");
            if (restoredRemoved > 0) summary += $" Restored {restoredRemoved} removed or disabled owned live value(s).";
            if (requests.Any(request => request.Name == "DFIntTaskSchedulerTargetFps" && request.Value is RuntimeFlagValue.Int32 { Value: > 0 }))
                summary += " A positive live FPS flag takes precedence over the native frame-rate cap; your profile value was preserved.";
            return Finish(new(DateTimeOffset.UtcNow, operation, target.ImagePath, pid, summary, lines, transaction));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            string reason = "Runtime operation stopped: " + error.Message;
            if (profile is not null)
                foreach (var entry in profile.Flags.Where(flag => flag.Enabled && !lines.Any(line => line.Name == flag.Name)))
                    lines.Add(new(entry.Name, "Not applied live", reason));
            return Finish(new(DateTimeOffset.UtcNow, operation, installation.ExecutablePath, pid,
                reason + " Supported embedded build: " + map.Version + ". Existing settings-file results are separate.", lines, transaction));
        }
    }
    private IReadOnlyList<RuntimeFlagLine> RestoreVerified(IRuntimeFlagTarget target, RuntimeOwnershipState state, ISet<string> names)
    {
        var memory = target.Memory;
        var lines = new List<RuntimeFlagLine>();
        var verified = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            try { map.VerifyName(memory, target.ModuleBase, name); verified.Add(name); }
            catch (Exception error) { lines.Add(new(name, "Restore unverified — skipped", error.Message)); }
        }
        lines.AddRange(RuntimeFlagOwnership.Restore(memory, map.Descriptor, state, store.Save, verified));
        return lines;
    }
    private RuntimeFlagReport Finish(RuntimeFlagReport report)
    {
        try { return report with { ReportPath = store.SaveReport(report) }; }
        catch (Exception error) { return report with { Summary = report.Summary + " Report could not be saved: " + error.Message }; }
    }
    private static List<int> FindExact(string path)
    {
        var result = new List<int>();
        using var own = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != own.SessionId || process.HasExited) continue;
                    using var query = WindowsRuntimeFlagMemory.OpenQueryHandle(process.Id);
                    if (WindowsRuntimeFlagMemory.SamePath(WindowsRuntimeFlagMemory.ImagePathFor(query), path)) result.Add(process.Id);
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
            }
        }
        return result;
    }
}

internal interface IRuntimeFlagTarget : IDisposable
{
    IRuntimeFlagMemory Memory { get; }
    string ImagePath { get; }
    ulong ModuleBase { get; }
}
internal sealed class NativeRuntimeFlagTarget(WindowsRuntimeFlagMemory memory) : IRuntimeFlagTarget
{
    public IRuntimeFlagMemory Memory => memory;
    public string ImagePath => memory.ImagePath;
    public ulong ModuleBase => memory.ModuleBase;
    public void Dispose() => memory.Dispose();
}
