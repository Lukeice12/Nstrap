using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nstrap.Core;

namespace Nstrap.App.Services;

// This adapter never discovers addresses. The service supplies build-pinned RVAs.
// A held kernel handle identifies one process object even after its PID is reused.
internal sealed class WindowsRuntimeFlagMemory : IRuntimeFlagMemory, IDisposable
{
    private readonly SafeProcessHandle handle;
    private readonly FileStream imageFile;
    private readonly long creationTime;
    private readonly RuntimeFlagSession identity;
    private readonly bool writable;
    private readonly uint allocationType;
    private readonly string? mappedFilePath;
    private readonly int? mappedFileError;
    private bool disposed;
    internal int ProcessId { get; }
    internal string ImagePath { get; }
    internal ulong ModuleBase { get; }
    internal ulong ImageSize => identity.ModuleImageSize;
    internal long CreationTime => creationTime;
    public RuntimeFlagSession Identity { get { EnsureAlive(); return identity; } }

    private WindowsRuntimeFlagMemory(SafeProcessHandle handle, FileStream imageFile, int pid,
        string path, string hash, long creation, ulong moduleBase, ulong imageSize, bool writable, uint allocationType)
    {
        this.handle = handle; this.imageFile = imageFile; ProcessId = pid; ImagePath = path;
        creationTime = creation; ModuleBase = moduleBase; this.writable = writable;
        this.allocationType = allocationType;
        (mappedFilePath, mappedFileError) = MappedFileFor(handle, moduleBase);
        identity = new($"pid:{pid};created:{creation:X16};base:{moduleBase:X16}", hash, imageSize);
    }

    internal static WindowsRuntimeFlagMemory OpenExact(int pid, string expectedPath, string expectedHash,
        ulong expectedSize, bool writable)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Runtime flag memory requires a Windows x64 process.");
        var handle = OpenHandle(pid, writable);
        FileStream? file = null;
        try
        {
            EnsureHandleAlive(handle);
            string path = ImagePathFor(handle);
            if (!SamePath(path, expectedPath)) throw new IOException("Process image path does not match the pinned installation.");
            long creation = CreationFor(handle);
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Executable SHA-256 does not match the pinned build.");
            file.Position = 0;
            using (var pe = new PEReader(file, PEStreamOptions.LeaveOpen))
            {
                if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus
                    || (ulong)pe.PEHeaders.PEHeader.SizeOfImage != expectedSize)
                    throw new IOException("On-disk executable is not the expected x64 image size.");
            }
            ulong moduleBase = MainModuleFor(handle, expectedPath, expectedSize);
            var mainAllocation = Query(handle, moduleBase);
            if (!IsSupportedMainMappingType(mainAllocation.Type))
                throw new IOException($"Main module allocation type 0x{mainAllocation.Type:X} is not MEM_IMAGE or MEM_MAPPED.");
            var memory = new WindowsRuntimeFlagMemory(handle, file, pid, path, hash, creation, moduleBase, expectedSize, writable, mainAllocation.Type);
            ValidateLoadedHeader(memory);
            memory.EnsureAlive();
            return memory;
        }
        catch { file?.Dispose(); handle.Dispose(); throw; }
    }

    public unsafe int Read(ulong relativeVirtualAddress, Span<byte> destination)
    {
        ulong address = ValidateRange(relativeVirtualAddress, destination.Length, false);
        fixed (byte* target = destination)
        {
            bool success = RuntimeFlagNative.ReadProcessMemory(handle, (nint)address, target, (nuint)destination.Length, out nuint actual);
            int error = success ? 0 : Marshal.GetLastWin32Error();
            EnsureAlive();
            if (!success && actual == 0) throw new Win32Exception(error, "ReadProcessMemory failed.");
            return checked((int)actual);
        }
    }

    public unsafe int Write(ulong relativeVirtualAddress, ReadOnlySpan<byte> source)
    {
        if (!writable) throw new InvalidOperationException("This adapter was opened read-only.");
        ulong address = ValidateRange(relativeVirtualAddress, source.Length, true);
        fixed (byte* data = source)
        {
            bool success = RuntimeFlagNative.WriteProcessMemory(handle, (nint)address, data, (nuint)source.Length, out nuint actual);
            int error = success ? 0 : Marshal.GetLastWin32Error();
            EnsureAlive();
            // Return the actual count on a partial operation so the core retains it.
            if (!success && actual == 0) throw new Win32Exception(error, "WriteProcessMemory failed.");
            return checked((int)actual);
        }
    }

    internal object DescribeRange(ulong rva, int length)
    {
        ulong address = ValidateRange(rva, length, false);
        RuntimeFlagNative.MemoryBasicInformation mbi = Query(address);
        uint protection = mbi.Protect & 0xff;
        return new { committed = mbi.State == RuntimeFlagNative.MemCommit, protection = $"0x{mbi.Protect:X}",
            writable = IsWritable(protection), type = $"0x{mbi.Type:X}", pinnedModuleType = $"0x{allocationType:X}",
            allocationBaseMatches = mbi.AllocationBase == ModuleBase, imageBacked = mbi.Type == RuntimeFlagNative.MemImage,
            mainMappedFileQuery = new { path = mappedFilePath, error = mappedFileError,
                backingIdentityVerifiedAgainstDisk = false },
            mappingCaveat = mappedFileError is null ? "Mapped-file query is diagnostic; backing identity was not compared to the disk hash."
                : "Mapped-file identity unavailable. Exact process/module path, on-disk hash, loaded PE size, allocation type/base and live semantic evidence are checked independently." };
    }

    private ulong ValidateRange(ulong rva, int length, bool write)
    {
        EnsureAlive();
        if (length <= 0 || (ulong)length > ImageSize || rva > ImageSize - (ulong)length)
            throw new ArgumentOutOfRangeException(nameof(rva), "Requested range is outside the pinned image.");
        ulong address = checked(ModuleBase + rva), end = checked(address + (ulong)length);
        if (end > long.MaxValue) throw new IOException("Address exceeds user-mode signed pointer range.");
        ulong cursor = address;
        while (cursor < end)
        {
            var mbi = Query(cursor);
            uint protection = mbi.Protect & 0xff;
            if (!MatchesPinnedPage(mbi, ModuleBase, allocationType))
                throw new IOException("Requested memory is not a committed readable range of the pinned image/allocation. "
                    + RangeDiagnostic(rva, length, address, cursor, mbi));
            if (write && !IsWritable(protection)) throw new IOException("Requested memory is not already writable; no protection change is permitted. "
                + RangeDiagnostic(rva, length, address, cursor, mbi));
            ulong next = checked(mbi.BaseAddress + mbi.RegionSize);
            if (next <= cursor) throw new IOException("VirtualQueryEx returned an invalid range.");
            cursor = Math.Min(next, end);
        }
        EnsureAlive();
        return address;
    }

    private string RangeDiagnostic(ulong rva, int length, ulong address, ulong cursor, RuntimeFlagNative.MemoryBasicInformation mbi)
        => $"rva=0x{rva:X}; length={length}; address=0x{address:X}; checkedAddress=0x{cursor:X}; "
            + $"regionBase=0x{mbi.BaseAddress:X}; regionSize=0x{mbi.RegionSize:X}; state=0x{mbi.State:X}; protect=0x{mbi.Protect:X}; "
            + $"type=0x{mbi.Type:X} (expected=0x{allocationType:X}); "
            + $"allocationBase=0x{mbi.AllocationBase:X} (expected=0x{ModuleBase:X}); "
            + $"mainMapping=[{DescribeMapping(ModuleBase)}]; requestedMapping=[{DescribeMapping(cursor)}].";

    private string DescribeMapping(ulong address)
    {
        try
        {
            var mbi = Query(address);
            var (path, error) = MappedFileFor(handle, address);
            string file = error is null ? path! : $"unavailable(error={error})";
            return $"state=0x{mbi.State:X},protect=0x{mbi.Protect:X},type=0x{mbi.Type:X},allocationBase=0x{mbi.AllocationBase:X},mappedFile={file}";
        }
        catch (Exception error) { return "queryFailed=" + error.Message; }
    }

    private RuntimeFlagNative.MemoryBasicInformation Query(ulong address)
        => Query(handle, address);

    private static RuntimeFlagNative.MemoryBasicInformation Query(SafeProcessHandle handle, ulong address)
    {
        nuint returned = RuntimeFlagNative.VirtualQueryEx(handle, (nint)address, out var mbi, (nuint)Marshal.SizeOf<RuntimeFlagNative.MemoryBasicInformation>());
        if (returned != (nuint)Marshal.SizeOf<RuntimeFlagNative.MemoryBasicInformation>()) throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualQueryEx failed.");
        return mbi;
    }

    private static (string? Path, int? Error) MappedFileFor(SafeProcessHandle handle, ulong address)
    {
        var path = new StringBuilder(32768);
        return RuntimeFlagNative.GetMappedFileName(handle, (nint)address, path, (uint)path.Capacity) == 0
            ? (null, Marshal.GetLastWin32Error()) : (path.ToString(), null);
    }

    private static bool IsSupportedMainMappingType(uint type) => type is RuntimeFlagNative.MemImage or RuntimeFlagNative.MemMapped;
    private static bool MatchesPinnedPage(RuntimeFlagNative.MemoryBasicInformation page, ulong expectedBase, uint expectedType)
        => page.State == RuntimeFlagNative.MemCommit && (page.Protect & RuntimeFlagNative.PageGuard) == 0
            && IsReadable(page.Protect & 0xff) && page.AllocationBase == expectedBase && page.Type == expectedType;

    private static bool IsReadable(uint protection) => protection is RuntimeFlagNative.PageReadOnly or RuntimeFlagNative.PageReadWrite
        or RuntimeFlagNative.PageWriteCopy or RuntimeFlagNative.PageExecuteRead or RuntimeFlagNative.PageExecuteReadWrite or RuntimeFlagNative.PageExecuteWriteCopy;

    private static bool IsWritable(uint protection) => protection is RuntimeFlagNative.PageReadWrite or RuntimeFlagNative.PageWriteCopy
        or RuntimeFlagNative.PageExecuteReadWrite or RuntimeFlagNative.PageExecuteWriteCopy;

    private void EnsureAlive()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureHandleAlive(handle);
        if (RuntimeFlagNative.GetProcessId(handle) != (uint)ProcessId || CreationFor(handle) != creationTime)
            throw new IOException("The pinned process lifetime no longer matches.");
    }

    internal static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    internal static string ImagePathFor(SafeProcessHandle handle)
    {
        var text = new StringBuilder(32768); int count = text.Capacity;
        if (!RuntimeFlagNative.QueryFullProcessImageName(handle, 0, text, ref count)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read process image path.");
        return Path.GetFullPath(text.ToString());
    }
    internal static SafeProcessHandle OpenQueryHandle(int pid)
    {
        var handle = RuntimeFlagNative.OpenProcess(RuntimeFlagNative.QueryLimitedInformation, false, pid);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Cannot query process."); }
        return handle;
    }
    private static SafeProcessHandle OpenHandle(int pid, bool writable)
    {
        uint access = RuntimeFlagNative.QueryInformation | RuntimeFlagNative.QueryLimitedInformation | RuntimeFlagNative.VmRead | RuntimeFlagNative.Synchronize;
        if (writable) access |= RuntimeFlagNative.VmWrite | RuntimeFlagNative.VmOperation;
        var handle = RuntimeFlagNative.OpenProcess(access, false, pid);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Cannot open the requested ordinary process handle."); }
        return handle;
    }
    private static long CreationFor(SafeProcessHandle handle)
    {
        if (!RuntimeFlagNative.GetProcessTimes(handle, out long created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read process lifetime.");
        return created;
    }
    private static void EnsureHandleAlive(SafeProcessHandle handle)
    {
        uint wait = RuntimeFlagNative.WaitForSingleObject(handle, 0);
        if (wait != RuntimeFlagNative.WaitTimeout) throw new IOException(wait == 0 ? "The pinned process has exited." : "Cannot verify the pinned process is alive.");
    }
    private static ulong MainModuleFor(SafeProcessHandle handle, string expectedPath, ulong expectedSize)
    {
        var first = new nint[1];
        if (!RuntimeFlagNative.EnumProcessModulesEx(handle, first, (uint)nint.Size, out uint needed, 3) || needed < nint.Size)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify the main module through the pinned process handle.");
        var text = new StringBuilder(32768);
        if (RuntimeFlagNative.GetModuleFileNameEx(handle, first[0], text, text.Capacity) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify the main module path.");
        if (!SamePath(text.ToString(), expectedPath)) throw new IOException("Main module path does not match the pinned executable.");
        if (!RuntimeFlagNative.GetModuleInformation(handle, first[0], out var module, (uint)Marshal.SizeOf<RuntimeFlagNative.ModuleInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query the main module size.");
        if (module.ImageSize != expectedSize || module.BaseOfDll != first[0]) throw new IOException("Loaded main module size/base mismatch.");
        return (ulong)module.BaseOfDll;
    }
    private static void ValidateLoadedHeader(WindowsRuntimeFlagMemory memory)
    {
        byte[] header = ReadExact(memory, 0, 64);
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3c));
        if (header[0] != 'M' || header[1] != 'Z' || peOffset < 64 || peOffset > 1024 * 1024 || (ulong)peOffset + 88 > memory.ImageSize)
            throw new IOException("Loaded image has an invalid DOS/PE header.");
        byte[] pe = ReadExact(memory, (ulong)peOffset, 88);
        if (BinaryPrimitives.ReadUInt32LittleEndian(pe) != 0x4550 || BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(4)) != 0x8664
            || BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(24)) != 0x20b
            || BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(80)) != memory.ImageSize)
            throw new IOException("Loaded executable is not the pinned x64 image size.");
    }
    private static byte[] ReadExact(WindowsRuntimeFlagMemory memory, ulong rva, int count)
    {
        byte[] bytes = new byte[count];
        int read = memory.Read(rva, bytes);
        if (read != count) throw new IOException($"Incomplete scalar read: {read}/{count}.");
        return bytes;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; imageFile.Dispose(); handle.Dispose();
    }
}

internal static class RuntimeFlagNative
{
    internal const uint QueryInformation = 0x0400, QueryLimitedInformation = 0x1000, VmRead = 0x0010,
        VmWrite = 0x0020, VmOperation = 0x0008, Synchronize = 0x100000, WaitTimeout = 0x102;
    internal const uint MemCommit = 0x1000,
        MemImage = 0x1000000, MemMapped = 0x40000, PageReadOnly = 0x02,
        PageReadWrite = 0x04, PageWriteCopy = 0x08, PageExecuteRead = 0x20, PageExecuteReadWrite = 0x40, PageExecuteWriteCopy = 0x80, PageGuard = 0x100;
    [StructLayout(LayoutKind.Sequential)] internal struct MemoryBasicInformation
    {
        internal ulong BaseAddress, AllocationBase;
        internal uint AllocationProtect;
        internal ushort PartitionId;
        internal ulong RegionSize;
        internal uint State, Protect, Type;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ModuleInformation { internal nint BaseOfDll; internal uint ImageSize; internal nint EntryPoint; }
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool ReadProcessMemory(SafeProcessHandle process, nint address, void* buffer, nuint size, out nuint bytesRead);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern unsafe bool WriteProcessMemory(SafeProcessHandle process, nint address, void* buffer, nuint size, out nuint bytesWritten);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryBasicInformation information, nuint length);
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumProcessModulesEx(SafeProcessHandle process, [Out] nint[] modules, uint bytes, out uint needed, uint filter);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetModuleFileNameEx(SafeProcessHandle process, nint module, StringBuilder path, int size);
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetModuleInformation(SafeProcessHandle process, nint module, out ModuleInformation information, uint size);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetMappedFileName(SafeProcessHandle process, nint address, StringBuilder path, uint size);
}
