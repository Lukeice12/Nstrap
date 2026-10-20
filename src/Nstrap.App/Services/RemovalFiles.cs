using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nstrap.App.Services;

/// <summary>
/// Removal uses handles, not a recursive path-delete API. Each ancestor is held
/// without delete sharing; each candidate is verified on the same handle used
/// for deletion. Links, extra hard links and changed paths fail closed.
/// </summary>
internal static class RemovalFiles
{
    internal sealed class Pins : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];

        public void AddAncestors(string directory)
        {
            UpdateHandoff.RequireRegularPath(directory);
            string full = Path.GetFullPath(directory), root = Path.GetPathRoot(full)!;
            string path = root;
            foreach (string component in new[] { "" }.Concat(full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries)))
            {
                if (component.Length != 0) path = Path.Combine(path, component);
                var handle = Open(path, ReadAttributes | ReadData, FileShare.Read | FileShare.Write);
                if (!Inspect(handle, path, out var info) || (info.Attributes & DirectoryAttribute) == 0)
                {
                    handle.Dispose();
                    throw new IOException("A removal path is locked, redirected or no longer matches.");
                }
                _handles.Add(handle);
            }
        }

        public void Dispose()
        {
            for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
            _handles.Clear();
        }
    }

    /// <summary>
    /// Reading a live single-file application must remain compatible with its
    /// loader. Deletion rights are acquired only after the parent has exited.
    /// Both handles stay open during that transition, so a replacement at the
    /// same path (even with identical bytes) cannot become the deletion target.
    /// </summary>
    internal sealed class PreparedExecutable : IDisposable
    {
        private readonly string _path, _hash;
        private readonly SafeFileHandle _read;
        private readonly FileIdentity _identity;

        public PreparedExecutable(string path, string expectedHash)
        {
            _path = path; _hash = expectedHash;
            _read = OpenExecutable(path, expectedHash, GenericRead, FileShare.Read | FileShare.Delete);
            try { _identity = IdentityOf(_read); }
            catch { _read.Dispose(); throw; }
        }

        public SafeFileHandle OpenForDeletion()
        {
            ObjectDisposedException.ThrowIf(_read.IsClosed, this);
            var deletion = PinExecutable(_path, _hash);
            try
            {
                if (!IdentityOf(deletion).Equals(_identity))
                    throw new IOException("The portable executable was replaced after removal was prepared.");
                return deletion;
            }
            catch { deletion.Dispose(); throw; }
        }

        public void Dispose() => _read.Dispose();
    }

    internal static SafeFileHandle PinExecutable(string path, string expectedHash) =>
        OpenExecutable(path, expectedHash, GenericRead | DeleteAccess, FileShare.Read);

    private static SafeFileHandle OpenExecutable(string path, string expectedHash, uint access, FileShare share)
    {
        var handle = Open(path, access, share);
        try
        {
            if (!Inspect(handle, path, out var info) || info.NumberOfLinks != 1
                || (info.Attributes & (DirectoryAttribute | ReadOnlyAttribute | SystemAttribute | OfflineAttribute)) != 0)
                throw new IOException("The portable executable is locked, linked or protected.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[128 * 1024];
            long offset = 0;
            int read;
            while ((read = RandomAccess.Read(handle, buffer, offset)) != 0)
            {
                hash.AppendData(buffer, 0, read);
                offset += read;
            }
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The executable changed while removal was being prepared.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static bool DeletePinned(SafeFileHandle handle)
    {
        // On supported Windows filesystems, POSIX disposal can unlink this
        // helper's mapped image. No reboot task, script, service or elevation.
        uint flags = 0x1 | 0x2;
        if (SetDispositionEx(handle, 21, ref flags, sizeof(uint))) return true;
        byte delete = 1;
        return SetDisposition(handle, 4, ref delete, 1);
    }

    internal static void DeleteData(string dataDirectory, string helperDirectory)
    {
        using var ancestors = new Pins();
        ancestors.AddAncestors(Path.GetDirectoryName(dataDirectory)!);
        var budget = new CleanupBudget();
        // Top-level ownership is fixed in code. Unexpected user files in the
        // data folder prevent its removal and are never treated as app data.
        using var root = Open(dataDirectory, ReadData | ReadAttributes | DeleteAccess, FileShare.Read | FileShare.Write);
        if (!Inspect(root, dataDirectory, out var info) || (info.Attributes & DirectoryAttribute) == 0) return;
        foreach (string name in new[] { "profiles.json", "icons", "backups", "recovery", "updates", "identity", "downloads", "removal" })
        {
            if (budget.Exhausted) break;
            DeleteEntry(Path.Combine(dataDirectory, name), dataDirectory, helperDirectory, 0, budget);
        }
        foreach (string path in Directory.EnumerateFiles(dataDirectory, ".nstrap-*.tmp").Take(1000))
        {
            if (budget.Exhausted) break;
            string name = Path.GetFileName(path);
            if (name.Length == 44 && Guid.TryParseExact(name.AsSpan(8, 32), "N", out _))
                DeleteEntry(path, dataDirectory, helperDirectory, 0, budget);
        }
        // The current helper job is removed separately, after all work ends.
    }

    internal static void DeleteOwnJob(string directory, string hash, string helperName)
    {
        try
        {
            if (!AppIdentity.IsExecutableName(helperName)) return;
            using var ancestors = new Pins();
            ancestors.AddAncestors(Path.GetDirectoryName(directory)!);
            using var root = Open(directory, ReadData | ReadAttributes | DeleteAccess, FileShare.Read | FileShare.Write);
            if (!Inspect(root, directory, out var info) || (info.Attributes & DirectoryAttribute) == 0) return;
            var budget = new CleanupBudget();
            DeleteEntry(Path.Combine(directory, "job.json"), directory, "", 0, budget);
            DeleteEntry(Path.Combine(directory, "runtime"), directory, "", 0, budget);
            // Exact hash and one link are required even for our helper image.
            using (var executable = PinExecutable(Path.Combine(directory, helperName), hash))
                DeletePinned(executable);
            DeletePinned(root); // Succeeds only if empty.
        }
        catch { /* A locked helper remains in its documented app-owned folder. */ }
    }

    internal static void RemoveEmptyDirectory(string directory)
    {
        try
        {
            using var ancestors = new Pins();
            ancestors.AddAncestors(Path.GetDirectoryName(directory)!);
            using var handle = Open(directory, ReadData | ReadAttributes | DeleteAccess, FileShare.Read | FileShare.Write);
            if (Inspect(handle, directory, out var info) && (info.Attributes & DirectoryAttribute) != 0)
                DeletePinned(handle);
        }
        catch { }
    }

    private static void DeleteEntry(string path, string root, string excluded, int depth, CleanupBudget budget)
    {
        if (budget.Exhausted || depth > 16
            || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, excluded, StringComparison.OrdinalIgnoreCase)) return;
        budget.Entries++;
        try
        {
            // No delete sharing pins the entry while enumerating it. OPEN_REPARSE_POINT
            // rejects a link inserted since the parent directory was enumerated.
            using var handle = Open(path, ReadData | ReadAttributes | DeleteAccess, FileShare.Read | FileShare.Write);
            if (!Inspect(handle, path, out var info)
                || (info.Attributes & (ReadOnlyAttribute | SystemAttribute | OfflineAttribute)) != 0) return;
            if ((info.Attributes & DirectoryAttribute) == 0)
            {
                if (info.NumberOfLinks == 1) DeletePinned(handle);
                return;
            }
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
            foreach (string child in Directory.EnumerateFileSystemEntries(path, "*", options))
            {
                if (budget.Exhausted) return;
                string full = Path.GetFullPath(child);
                if (string.Equals(Path.GetDirectoryName(full), path, StringComparison.OrdinalIgnoreCase))
                    DeleteEntry(full, root, excluded, depth + 1, budget);
            }
            DeletePinned(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        { /* Changed, locked or untrusted entries remain; other owned entries can proceed. */ }
    }

    private sealed class CleanupBudget
    {
        private readonly long _started = Environment.TickCount64;
        public int Entries;
        public bool Exhausted => Entries >= 50_000 || Environment.TickCount64 - _started >= 60_000;
    }

    private static SafeFileHandle Open(string path, uint access, FileShare share)
    {
        var handle = CreateFileW(@"\\?\" + Path.GetFullPath(path), access, share, IntPtr.Zero, 3,
            0x02000000 | 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static FileIdentity IdentityOf(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 18, out var identity, (uint)Marshal.SizeOf<FileIdentity>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return identity;
    }

    private static bool Inspect(SafeFileHandle handle, string path, out FileInformation info)
    {
        info = default;
        if (handle.IsInvalid || GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out info)
            || (info.Attributes & 0x400) != 0) return false;
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length > 32_768) return false;
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return false;
        }
        string final = buffer.ToString();
        return final.StartsWith(@"\\?\", StringComparison.Ordinal)
            && string.Equals(Path.TrimEndingDirectorySeparator(final[4..]),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase);
    }

    private const uint GenericRead = 0x80000000, ReadData = 1, ReadAttributes = 0x80, DeleteAccess = 0x10000;
    private const uint ReadOnlyAttribute = 1, SystemAttribute = 4, DirectoryAttribute = 0x10, OfflineAttribute = 0x1000;
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileIdentity : IEquatable<FileIdentity>
    {
        private readonly ulong _volume, _low, _high;
        public bool Equals(FileIdentity other) =>
            _volume == other._volume && _low == other._low && _high == other._high;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public NativeFileTime Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out FileIdentity information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDispositionEx(SafeFileHandle file, int kind, ref uint information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle file, int kind, ref byte information, uint size);
}
