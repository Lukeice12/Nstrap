using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nstrap.Core;

public sealed record RobloxCacheCleanResult(
    int ExaminedEntries,
    int DeletedFiles,
    long DeletedBytes,
    int SkippedFiles,
    bool LimitReached,
    bool Cancelled,
    bool StoppedBecauseRobloxRunning,
    bool CacheExists)
{
    public int SkippedDirectories { get; init; }
    public int UnreadableEntries { get; init; }
    public int SkippedEntries => SkippedFiles + SkippedDirectories + UnreadableEntries;
    public string? FailureMessage { get; init; }
}

/// <summary>
/// Manual maintenance of this account's shared Roblox asset cache. Never scans on
/// construction, accepts no caller-supplied path, and never deletes directories.
/// DeletedBytes counts logical file lengths, not guaranteed reclaimed disk space.
/// </summary>
public sealed class RobloxCacheCleaner
{
    public const int MinimumAgeDays = 14;
    public const int MaximumExaminedEntries = 10_000;
    public const int MaximumDeletedFiles = 200;
    public const int MaximumDepth = 8;
    public const int MaximumSeconds = 15;

    public static string CacheDirectory
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local))
                throw new IOException("Windows did not provide an absolute local application data directory.");
            return Path.GetFullPath(Path.Combine(local, "Roblox", "rbx-storage"));
        }
    }

    /// <summary>
    /// The caller must check Player and Studio for the current Windows user, across
    /// sessions. A failed process check stops cleanup and preserves partial results.
    /// Run off the UI thread. The time/cancellation budget is checked between native
    /// operations; an individual filesystem or process query can take longer.
    /// </summary>
    public RobloxCacheCleanResult Clean(Func<bool> isRobloxRunning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isRobloxRunning);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Roblox cache cleanup is supported on Windows.");

        var operation = new CleanupOperation(isRobloxRunning, cancellationToken);
        if (operation.ShouldStop(checkProcess: true)) return operation.Result();

        var ancestors = new List<SafeFileHandle>();
        try
        {
            string root = NormalizePath(CacheDirectory);
            string volumeRoot = Path.GetPathRoot(root)
                ?? throw new IOException("The Roblox cache has no filesystem root.");
            string path = volumeRoot;
            uint? volumeSerial = null;
            // Keep every ancestor open without delete sharing until all cleanup is
            // finished. A directory rename cannot move an opened candidate outside
            // the verified cache between validation and deletion.
            var components = new[] { "" }.Concat(
                root[volumeRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
            foreach (string component in components)
            {
                if (operation.ShouldStop()) return operation.Result();
                if (component.Length != 0) path = Path.Combine(path, component);
                var handle = Open(path, FileReadAttributes | FileListDirectory, FileShareRead);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (error is ErrorFileNotFound or ErrorPathNotFound) return operation.Result();
                    throw new IOException("The Roblox cache path could not be secured for cleanup.",
                        new Win32Exception(error));
                }
                ancestors.Add(handle);
                if (!TryInspect(handle, path, out var info)
                    || (info.Attributes & FileAttributeDirectory) == 0
                    || (volumeSerial.HasValue && info.VolumeSerialNumber != volumeSerial.Value))
                    throw new IOException("Cache cleanup cannot use links, junctions, or a changed cache path.");
                volumeSerial = info.VolumeSerialNumber;
            }

            operation.CacheExists = true;
            Walk(root, root, volumeSerial!.Value, depth: 0, operation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            operation.FailureMessage = ex.Message;
        }
        finally
        {
            for (int i = ancestors.Count - 1; i >= 0; i--) ancestors[i].Dispose();
        }
        return operation.Result();
    }

    private static void Walk(string directory, string root, uint volumeSerial, int depth, CleanupOperation operation)
    {
        if (operation.ShouldStop()) return;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                BufferSize = 4096
            };
            // Only one directory iterator per level is alive. No entire tree or
            // unbounded list of pending paths is accumulated.
            using var entries = Directory.EnumerateFileSystemEntries(directory, "*", options).GetEnumerator();
            while (!operation.ShouldStop())
            {
                if (operation.ExaminedEntries >= MaximumExaminedEntries)
                {
                    operation.LimitReached = true;
                    return;
                }
                if (!entries.MoveNext()) return;
                operation.ExaminedEntries++;
                if (operation.ShouldStop()) return;

                string candidate = NormalizePath(entries.Current);
                if (!IsInside(candidate, root)
                    || !string.Equals(Path.GetDirectoryName(candidate), directory, StringComparison.OrdinalIgnoreCase))
                {
                    operation.UnreadableEntries++;
                    continue;
                }

                FileAttributes attributes;
                try { attributes = File.GetAttributes(candidate); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    operation.UnreadableEntries++;
                    continue;
                }
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (isDirectory) operation.SkippedDirectories++;
                    else operation.SkippedFiles++;
                    continue;
                }

                if (isDirectory)
                {
                    if (depth >= MaximumDepth)
                    {
                        operation.SkippedDirectories++;
                        operation.LimitReached = true;
                        continue;
                    }
                    using var child = Open(candidate, FileReadAttributes | FileListDirectory, FileShareRead);
                    if (!TryInspect(child, candidate, out var info)
                        || (info.Attributes & FileAttributeDirectory) == 0
                        || info.VolumeSerialNumber != volumeSerial)
                    {
                        operation.SkippedDirectories++;
                        continue;
                    }
                    // Hold this directory's identity while its children are processed.
                    Walk(candidate, root, volumeSerial, depth + 1, operation);
                }
                else
                {
                    DeleteOldFile(candidate, root, volumeSerial, attributes, operation);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            operation.UnreadableEntries++;
        }
    }

    private static void DeleteOldFile(string path, string root, uint volumeSerial,
        FileAttributes attributes, CleanupOperation operation)
    {
        if ((attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Offline)) != 0
            || IsDatabaseFile(Path.GetFileName(path)))
        {
            operation.SkippedFiles++;
            return;
        }
        if (operation.ShouldStop()) return;

        // Exclusive access rejects active readers/writers and denies subsequent
        // writes, deletion or rename. OPEN_REPARSE_POINT never follows the final
        // component if a file was swapped for a link since enumeration.
        using var handle = Open(path, FileReadAttributes | DeleteAccess, share: 0);
        if (!TryInspect(handle, path, out var info)
            || info.VolumeSerialNumber != volumeSerial
            || (info.Attributes & (FileAttributeDirectory | FileAttributeReadOnly | FileAttributeSystem | FileAttributeOffline)) != 0
            || info.NumberOfLinks != 1
            || !IsOld(info.CreationTime, operation.Cutoff)
            || !IsOld(info.LastWriteTime, operation.Cutoff))
        {
            operation.SkippedFiles++;
            return;
        }
        ulong size = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
        if (size > (ulong)(long.MaxValue - operation.DeletedBytes))
        {
            operation.SkippedFiles++;
            return;
        }

        // Check immediately before the only destructive operation. If process
        // inspection fails, return the counts already accumulated instead of
        // concealing a partially completed cleanup behind an exception.
        if (operation.ShouldStop(checkProcess: true)) return;
        if (!IsInside(path, root) || !TryInspect(handle, path, out var finalInfo)
            || finalInfo.VolumeSerialNumber != volumeSerial
            || (finalInfo.Attributes & (FileAttributeDirectory | FileAttributeReadOnly | FileAttributeSystem | FileAttributeOffline)) != 0
            || finalInfo.NumberOfLinks != 1
            || !IsOld(finalInfo.CreationTime, operation.Cutoff)
            || !IsOld(finalInfo.LastWriteTime, operation.Cutoff))
        {
            operation.SkippedFiles++;
            return;
        }
        if (operation.ShouldStop()) return;
        // FILE_DISPOSITION_INFO contains a one-byte BOOLEAN. This marks the
        // verified open file for deletion; no path-based DeleteFile call follows.
        byte deleteFile = 1;
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref deleteFile, 1))
        {
            operation.SkippedFiles++;
            return;
        }
        operation.DeletedFiles++;
        operation.DeletedBytes += (long)size;
        if (operation.DeletedFiles >= MaximumDeletedFiles) operation.LimitReached = true;
    }

    private static bool IsDatabaseFile(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wal", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".shm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".journal", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOld(NativeFileTime time, ulong cutoff)
    {
        ulong value = ((ulong)time.High << 32) | time.Low;
        return value != 0 && value < cutoff;
    }

    private static bool IsInside(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static SafeFileHandle Open(string path, uint access, uint share) =>
        CreateFileW(ToExtendedPath(path), access, share, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);

    private static string ToExtendedPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal)
        ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static bool TryInspect(SafeFileHandle handle, string expectedPath, out NativeFileInformation info)
    {
        info = default;
        if (handle.IsInvalid || GetFileType(handle) != FileTypeDisk
            || !GetFileInformationByHandle(handle, out info)
            || (info.Attributes & FileAttributeReparsePoint) != 0)
            return false;

        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length > 32_768) return false;
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return false;
        }
        string finalPath = buffer.ToString();
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            finalPath = @"\\" + finalPath[8..];
        else if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            finalPath = finalPath[4..];
        else return false;
        return string.Equals(NormalizePath(finalPath), NormalizePath(expectedPath), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CleanupOperation(Func<bool> isRobloxRunning, CancellationToken cancellationToken)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _lastProcessCheck = TimeSpan.MinValue;
        public readonly ulong Cutoff = (ulong)DateTime.UtcNow.AddDays(-MinimumAgeDays).ToFileTimeUtc();
        public int ExaminedEntries;
        public int DeletedFiles;
        public long DeletedBytes;
        public int SkippedFiles;
        public int SkippedDirectories;
        public int UnreadableEntries;
        public bool LimitReached;
        public bool Cancelled;
        public bool StoppedBecauseRobloxRunning;
        public bool CacheExists;
        public string? FailureMessage;

        public bool ShouldStop(bool checkProcess = false)
        {
            if (Cancelled || StoppedBecauseRobloxRunning || FailureMessage is not null) return true;
            if (cancellationToken.IsCancellationRequested)
            {
                Cancelled = true;
                return true;
            }
            if (_clock.Elapsed >= TimeSpan.FromSeconds(MaximumSeconds) || DeletedFiles >= MaximumDeletedFiles)
            {
                LimitReached = true;
                return true;
            }
            if (checkProcess || _lastProcessCheck == TimeSpan.MinValue
                || _clock.Elapsed - _lastProcessCheck >= TimeSpan.FromMilliseconds(250))
            {
                try { StoppedBecauseRobloxRunning = isRobloxRunning(); }
                catch (Exception)
                {
                    FailureMessage = "Roblox's running state could not be checked. Cache cleanup stopped.";
                    return true;
                }
                _lastProcessCheck = _clock.Elapsed;
                if (StoppedBecauseRobloxRunning) return true;
                if (cancellationToken.IsCancellationRequested)
                {
                    Cancelled = true;
                    return true;
                }
                if (_clock.Elapsed >= TimeSpan.FromSeconds(MaximumSeconds))
                {
                    LimitReached = true;
                    return true;
                }
            }
            return false;
        }

        public RobloxCacheCleanResult Result() =>
            new(ExaminedEntries, DeletedFiles, DeletedBytes, SkippedFiles,
                LimitReached, Cancelled, StoppedBecauseRobloxRunning, CacheExists)
            {
                SkippedDirectories = SkippedDirectories,
                UnreadableEntries = UnreadableEntries,
                FailureMessage = FailureMessage
            };
    }

    private const uint FileReadAttributes = 0x80;
    // Attribute-only opens do not participate in Windows sharing checks. Request
    // actual directory read access so omitting FILE_SHARE_DELETE pins its name.
    private const uint FileListDirectory = 0x1;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 1;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReadOnly = 0x1;
    private const uint FileAttributeSystem = 0x4;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileAttributeOffline = 0x1000;
    private const uint FileTypeDisk = 1;
    private const int FileDispositionInfo = 4;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public uint Attributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out NativeFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        ref byte information, uint size);
}
