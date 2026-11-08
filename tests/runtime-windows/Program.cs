using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Nstrap.Core;
using RuntimeWindows;

return await Runner.Run(args);

internal static class Runner
{
    internal static async Task<int> Run(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Use the Windows x64 runner.");
            if (args is ["--self-test"]) return SelfTests.Run();
            if (args is ["--fixture-child"])
            {
                // Used only by --self-test to verify stale-handle rejection after exit.
                Console.WriteLine("fixture-ready");
                Console.ReadLine();
                return 0;
            }
            if (args.Length == 0 || args is ["--inspect"])
            {
                using var memory = OpenCandidate(writable: false);
                Candidate.VerifyEvidence(memory);
                Emit(new { mode = "inspect-read-only", pid = memory.ProcessId, path = memory.ImagePath,
                    session = memory.Identity.ProcessSessionId, sha256 = memory.Identity.ExecutableSha256,
                    imageSize = memory.ImageSize, moduleBase = $"0x{memory.ModuleBase:X}", flag = Candidate.Flag,
                    rva = $"0x{Candidate.Rva:X}", value = Scalar.ReadInt(memory, Candidate.Rva), evidenceMatches = true,
                    memory = memory.DescribeRange(Candidate.Rva, 4), writesAttempted = false,
                    caveat = "Scalar bytes and code/name evidence only; visible FPS effect remains unverified." });
                return 0;
            }
            if (args is ["--restore", var receiptPath]) return RestoreReceipt(receiptPath);
            int fps, seconds = 20;
            if (args is ["--probe-fps", var value] && int.TryParse(value, out fps)
                || args is ["--probe-fps", var value2, "--hold-seconds", var hold]
                    && int.TryParse(value2, out fps) && int.TryParse(hold, out seconds))
            {
                if (fps is not (30 or 60) || seconds is < 1 or > 30) throw new ArgumentException("Probe FPS must be 30 or 60; hold duration must be 1..30 seconds.");
                return await Probe(fps, seconds);
            }
            Console.Error.WriteLine("Usage: RuntimeWindows.Checks [--inspect | --self-test | --probe-fps <30|60> [--hold-seconds <1..30>] | --restore <receipt.json>]");
            return 2;
        }
        catch (Exception error)
        {
            Emit(new { ok = false, error = error.Message, errorType = error.GetType().Name });
            return 1;
        }
    }

    private static WindowsRuntimeFlagMemory OpenCandidate(bool writable)
    {
        var pids = new List<int>();
        var candidates = Process.GetProcessesByName("RobloxPlayerBeta");
        try
        {
            foreach (var process in candidates)
            {
                using var handle = WindowsRuntimeFlagMemory.OpenQueryHandle(process.Id);
                if (WindowsRuntimeFlagMemory.SamePath(WindowsRuntimeFlagMemory.ImagePathFor(handle), Candidate.Path)) pids.Add(process.Id);
            }
        }
        finally { foreach (var process in candidates) process.Dispose(); }
        if (pids.Count != 1) throw new IOException($"Expected exactly one running process at the pinned Roblox path; found {pids.Count}.");
        return WindowsRuntimeFlagMemory.OpenExact(pids[0], Candidate.Path, Candidate.Hash, Candidate.ImageSize, writable);
    }

    private static async Task<int> Probe(int fps, int holdSeconds)
    {
        using var memory = OpenCandidate(writable: true);
        using var lease = ProbeLease.Acquire(memory.Identity);
        Candidate.VerifyEvidence(memory);
        var session = memory.Identity;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        string? path = null;
        ProbeReceipt? receipt = null;
        bool applied = false;
        ConditionalScalarMemory? guarded = null;
        string restore = "NotNeeded";
        Exception? failure = null;
        try
        {
            Emit(new { mode = "probe-countdown", pid = memory.ProcessId, requestedFps = fps, holdSeconds,
                secondsUntilSnapshotAndWrite = 5, note = "Focus Roblox now. Automatic conditional restoration follows the hold; Ctrl+C also requests restoration." });
            await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
            Candidate.VerifyEvidence(memory);
            int original = Scalar.ReadInt(memory, Candidate.Rva);
            if (original == fps) throw new IOException("The requested value is already present; no useful A/B probe and no write attempted.");
            receipt = Receipts.Create(memory, original, fps);
            path = Receipts.PersistNew(receipt); // flushed to disk before any write
            Receipts.Bind(receipt, memory);
            if (Scalar.ReadInt(memory, Candidate.Rva) != original) throw new IOException("Value changed while persisting the receipt; write skipped.");
            cancellation.Token.ThrowIfCancellationRequested();
            Emit(new { mode = "probe-prepared", receipt = path, originalValue = original, intendedValue = fps });
            guarded = new ConditionalScalarMemory(memory, Candidate.Rva, original, fps);
            RuntimeFlagBatchResult result = RuntimeFlags.Apply(guarded, session, Candidate.Map, [new(Candidate.Flag, new RuntimeFlagValue.Int32(fps))]);
            applied = result.AllWritesReadBack;
            Emit(new { mode = "probe-applied", appliedReadBack = applied, flagState = result.Flags.Single().State.ToString(), detail = result.Flags.Single().Detail,
                reportedWrittenBytes = result.Flags.Single().ReportedWrittenBytes, holdSeconds = applied ? holdSeconds : 0,
                caveat = "Byte read-back does not establish visible FPS effect. Comparisons and writes are not atomic against external writers." });
            if (!applied) throw new IOException(result.Error ?? "Core did not read back the intended bytes.");
            Receipts.Update(path, receipt, "AppliedReadBackAwaitingRestore");
            await Task.Delay(TimeSpan.FromSeconds(holdSeconds), cancellation.Token);
        }
        catch (OperationCanceledException) { failure = new IOException("Probe interrupted; conditional restoration requested."); }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (guarded?.NativeWriteAttempted == true && receipt is not null)
            {
                try
                {
                    Receipts.Bind(receipt, memory);
                    Candidate.VerifyName(memory);
                    restore = Scalar.Restore(memory, Candidate.Rva, receipt.Payload.OriginalValue, receipt.Payload.IntendedValue);
                }
                catch (Exception ex) { restore = "RestoreFailed: " + ex.Message; }
            }
            if (receipt is not null && path is not null)
            {
                try { Receipts.Update(path, receipt, restore); }
                catch (Exception ex) { Emit(new { receiptUpdateFailed = ex.Message, receipt = path }); }
            }
            Console.CancelKeyPress -= cancel;
        }
        bool restored = restore is "OriginalAlreadyPresent" or "OriginalRestoredReadBack";
        Emit(new { mode = "probe-finished", appliedReadBack = applied, restore, receipt = path,
            error = failure?.Message, visibleEffectVerified = false });
        return applied && restored && failure is null ? 0 : 1;
    }

    private static int RestoreReceipt(string path)
    {
        var receipt = Receipts.LoadValidated(path);
        if (receipt.Status != "AppliedReadBackAwaitingRestore" && !receipt.Status.StartsWith("RestoreFailed:", StringComparison.Ordinal))
            throw new IOException("Receipt does not establish an outstanding native write; automatic recovery refused. Its original value remains available for review.");
        using var memory = OpenCandidate(writable: true);
        using var lease = ProbeLease.Acquire(memory.Identity);
        Receipts.Bind(receipt, memory);
        Candidate.VerifyName(memory);
        string result = Scalar.Restore(memory, Candidate.Rva, receipt.Payload.OriginalValue, receipt.Payload.IntendedValue);
        Receipts.Update(path, receipt, result);
        Emit(new { mode = "receipt-recovery", pid = memory.ProcessId, result, receipt = Path.GetFullPath(path), visibleEffectVerified = false });
        return result is "OriginalAlreadyPresent" or "OriginalRestoredReadBack" ? 0 : 1;
    }

    internal static void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Receipts.Json));
}
