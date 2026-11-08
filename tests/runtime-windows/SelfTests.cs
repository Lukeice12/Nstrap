using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nstrap.Core;

namespace RuntimeWindows;

internal static class SelfTests
{
    internal static int Run()
    {
        var passed = new List<string>();
        nint allocation = Native.VirtualAlloc(0, 4096, Native.MemCommit | Native.MemReserve, Native.PageReadWrite);
        nint readOnlyAllocation = Native.VirtualAlloc(0, 4096, Native.MemCommit | Native.MemReserve, Native.PageReadOnly);
        if (allocation == 0 || readOnlyAllocation == 0) throw new IOException("Cannot allocate own self-test fixture.");
        try
        {
            Marshal.WriteInt32(allocation, 40);
            using var memory = WindowsRuntimeFlagMemory.OpenOwnAllocation(allocation, 4096, writable: true);
            Check(Scalar.ReadInt(memory, 0) == 40, "native-own-allocation-read", passed);
            var map = new RuntimeFlagMapDescriptor(memory.Identity.ExecutableSha256, memory.ImageSize,
                [new("FixtureInt", 0, RuntimeFlagScalarType.Int32), new("FixtureBool", 8, RuntimeFlagScalarType.Boolean)]);
            var apply = RuntimeFlags.Apply(memory, memory.Identity, map,
                [new("FixtureInt", new RuntimeFlagValue.Int32(30)), new("FixtureBool", new RuntimeFlagValue.Boolean(true))]);
            Check(apply.AllWritesReadBack && Marshal.ReadInt32(allocation) == 30 && Marshal.ReadByte(allocation, 8) == 1,
                "native-core-int32-and-one-byte-boolean-apply-readback", passed);
            Check(Scalar.Restore(memory, 0, 40, 30) == "OriginalRestoredReadBack" && Marshal.ReadInt32(allocation) == 40,
                "native-conditional-restore-readback", passed);
            Check(Scalar.Restore(memory, 0, 40, 30) == "OriginalAlreadyPresent", "restore-idempotence", passed);
            Marshal.WriteInt32(allocation, 41);
            var guard = new ConditionalScalarMemory(memory, 0, 40, 30);
            ExpectThrow(() => guard.Write(0, Scalar.Encode(30)), "observed-prewrite-concurrency-refusal", passed);
            Check(!guard.NativeWriteAttempted, "conditional-refusal-records-no-native-write", passed);
            Check(Marshal.ReadInt32(allocation) == 41 && Scalar.Restore(memory, 0, 40, 30).StartsWith("ExternalChangePreserved"),
                "restore-preserves-external-value", passed);
            Marshal.WriteInt32(allocation, 40);
            var racing = new RacingMemory(memory, () => Marshal.WriteInt32(allocation, 30));
            var racingGuard = new ConditionalScalarMemory(racing, 0, 40, 30);
            var raceResult = RuntimeFlags.Apply(racingGuard, memory.Identity,
                new(memory.Identity.ExecutableSha256, memory.ImageSize, [new("FixtureInt", 0, RuntimeFlagScalarType.Int32)]),
                [new("FixtureInt", new RuntimeFlagValue.Int32(30))]);
            Check(!raceResult.AllWritesReadBack && !racingGuard.NativeWriteAttempted && racing.NativeWrites == 0
                && Marshal.ReadInt32(allocation) == 30, "external-intended-value-preserved-when-native-write-never-attempted", passed);
            ExpectThrow(() => memory.Read(4094, new byte[4]), "bounds-end-refusal", passed);
            ExpectThrow(() => memory.Write(ulong.MaxValue, Scalar.Encode(30)), "overflow-range-refusal", passed);
            using (var readOnly = WindowsRuntimeFlagMemory.OpenOwnAllocation(allocation, 4096, writable: false))
                ExpectThrow(() => readOnly.Write(0, Scalar.Encode(30)), "readonly-handle-write-refusal", passed);
            using (var readOnlyPage = WindowsRuntimeFlagMemory.OpenOwnAllocation(readOnlyAllocation, 4096, writable: true))
                ExpectThrow(() => readOnlyPage.Write(0, Scalar.Encode(30)), "readonly-page-write-refusal-no-protection-change", passed);
            byte[] testPayload = "receipt integrity fixture"u8.ToArray();
            Check(DataProtection.Unprotect(DataProtection.Protect(testPayload)).SequenceEqual(testPayload), "dpapi-receipt-roundtrip", passed);
            var payload = new ReceiptPayload(1, DateTimeOffset.UtcNow, Candidate.Flag, Candidate.Path, Candidate.Hash,
                Candidate.ImageSize, 123, 456, "fixture-session", 0x100000, Candidate.Rva, 0, 30);
            var receipt = Receipts.Seal(payload, "FixtureOnly");
            Receipts.Validate(receipt);
            ExpectThrow(() => Receipts.Validate(receipt with { Payload = payload with { OriginalValue = 99 } }), "edited-original-receipt-refusal", passed);
            ExpectThrow(() => Receipts.Validate(receipt with { Status = "AppliedReadBackAwaitingRestore" }), "edited-receipt-status-refusal", passed);
            ExpectThrow(() => Receipts.Bind(receipt, memory), "receipt-other-process-lifetime-refusal", passed);
            VerifyMappedViewGuards(passed);
            VerifyImageAndProcessExit(passed);
        }
        finally
        {
            Native.VirtualFree(allocation, 0, Native.MemRelease);
            Native.VirtualFree(readOnlyAllocation, 0, Native.MemRelease);
        }
        Runner.Emit(new { mode = "self-test", passed = passed.Count, checks = passed, robloxAccessed = false,
            caveat = "Own-process native I/O and refusal tests do not establish Roblox write access or visible FPS behavior." });
        return 0;
    }

    private static void VerifyMappedViewGuards(List<string> passed)
    {
        using var mappedFile = MemoryMappedFile.CreateNew(null, 4096);
        using var view = mappedFile.CreateViewAccessor(0, 4096, MemoryMappedFileAccess.ReadWrite);
        ulong address = (ulong)view.SafeMemoryMappedViewHandle.DangerousGetHandle();
        using var process = Native.OpenProcess(Native.QueryInformation, false, Environment.ProcessId);
        if (process.IsInvalid || Native.VirtualQueryEx(process, (nint)address, out var page,
            (nuint)Marshal.SizeOf<Native.MemoryBasicInformation>()) != (nuint)Marshal.SizeOf<Native.MemoryBasicInformation>())
            throw new IOException("Cannot query own mapped fixture.");
        Check(page.Type == Native.MemMapped && WindowsRuntimeFlagMemory.IsSupportedMainMappingType(page.Type)
            && WindowsRuntimeFlagMemory.MatchesPinnedPage(page, address, Native.MemMapped), "native-mapped-view-pinned-type-base-acceptance", passed);
        Check(!WindowsRuntimeFlagMemory.MatchesPinnedPage(page, address, Native.MemImage), "mapped-view-type-mismatch-refusal", passed);
        Check(!WindowsRuntimeFlagMemory.MatchesPinnedPage(page, address + 4096, Native.MemMapped), "mapped-view-base-mismatch-refusal", passed);
        Check(!WindowsRuntimeFlagMemory.IsSupportedMainMappingType(Native.MemPrivate), "private-main-module-type-refusal", passed);
        ExpectThrow(() => WindowsRuntimeFlagMemory.OpenOwnAllocation((nint)address, 4096, false).Dispose(),
            "private-fixture-does-not-accept-mapped-view", passed);
    }

    private static void VerifyImageAndProcessExit(List<string> passed)
    {
        string imagePath = Environment.ProcessPath ?? throw new IOException("Self process path missing.");
        string hash; ulong imageSize;
        using (var file = File.OpenRead(imagePath))
        {
            hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            file.Position = 0;
            using var pe = new PEReader(file);
            imageSize = (ulong)pe.PEHeaders.PEHeader!.SizeOfImage;
        }
        using (var image = WindowsRuntimeFlagMemory.OpenExact(Environment.ProcessId, imagePath, hash, imageSize, writable: false))
            Check(Scalar.Read(image, 0, 2).SequenceEqual(new byte[] { (byte)'M', (byte)'Z' }), "pinned-main-image-path-hash-size-and-header", passed);
        ExpectThrow(() => WindowsRuntimeFlagMemory.OpenExact(Environment.ProcessId, imagePath, new string('0', 64), imageSize, false).Dispose(),
            "wrong-build-hash-refusal", passed);
        ExpectThrow(() => WindowsRuntimeFlagMemory.OpenExact(Environment.ProcessId, imagePath, hash, imageSize + 1, false).Dispose(),
            "wrong-image-size-refusal", passed);
        var start = new ProcessStartInfo(imagePath) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(imagePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(SelfTests).Assembly.Location);
        start.ArgumentList.Add("--fixture-child");
        using var child = Process.Start(start) ?? throw new IOException("Cannot start own child fixture.");
        try
        {
            if (child.StandardOutput.ReadLine() != "fixture-ready") throw new IOException("Own child fixture did not become ready.");
            using var pinned = WindowsRuntimeFlagMemory.OpenExact(child.Id, imagePath, hash, imageSize, writable: false);
            child.StandardInput.WriteLine("exit"); child.StandardInput.Flush();
            if (!child.WaitForExit(5000)) throw new IOException("Own child fixture did not exit.");
            ExpectThrow(() => pinned.Read(0, new byte[2]), "exited-pinned-process-read-refusal", passed);
            ExpectThrow(() => { _ = pinned.Identity; }, "exited-pinned-process-identity-refusal", passed);
        }
        finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
    }

    private static void Check(bool condition, string name, List<string> passed)
    {
        if (!condition) throw new IOException("Self-test failed: " + name);
        passed.Add(name);
    }
    private sealed class RacingMemory(IRuntimeFlagMemory inner, Action externalChange) : IRuntimeFlagMemory
    {
        private int reads;
        internal int NativeWrites { get; private set; }
        public RuntimeFlagSession Identity => inner.Identity;
        public int Read(ulong rva, Span<byte> destination)
        {
            if (++reads == 3) externalChange();
            return inner.Read(rva, destination);
        }
        public int Write(ulong rva, ReadOnlySpan<byte> source) { NativeWrites++; return inner.Write(rva, source); }
    }
    private static void ExpectThrow(Action action, string name, List<string> passed)
    {
        try { action(); }
        catch { passed.Add(name); return; }
        throw new IOException("Self-test expected refusal: " + name);
    }
}
