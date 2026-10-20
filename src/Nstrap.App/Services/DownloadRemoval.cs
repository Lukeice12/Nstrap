using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Nstrap.App.Services;

internal sealed class DownloadArchiveProof
{
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>
/// Optional cleanup of the standard release download layout. Names only select
/// candidates: published archive SHA256 and the sole entry's bytes prove content.
/// All discovery and read pins are complete before the shortcut becomes ready.
/// </summary>
internal sealed partial class DownloadRemoval : IDisposable
{
    internal const int MaxProofs = 32;
    private const int MaxArchives = 8, MaxCopies = 16;
    private const long MaxArchiveBytes = 256L * 1024 * 1024, MaxExecutableBytes = 512L * 1024 * 1024;
    private readonly RemovalFiles.Pins _downloadsParents = new();
    private readonly List<(RemovalFiles.PreparedDownload File, RemovalFiles.Pins Parents)> _files = [];
    private readonly Dictionary<string, RemovalFiles.PreparedEmptyDirectory> _directories = new(StringComparer.OrdinalIgnoreCase);
    public string Status { get; private set; } = "Downloads could not be verified; downloaded ZIPs and extra copies will be kept.";

    [GeneratedRegex(@"^Nstrap-win-x64(?: \([1-9][0-9]{0,5}\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackageStem();

    internal static async Task<DownloadArchiveProof[]?> ReadProofsAsync()
    {
        try { return await GitHubUpdates.ReadRemovalProofsAsync().ConfigureAwait(false); }
        catch { return null; } // Offline preparation must still permit the original removal scope.
    }

    internal static DownloadRemoval Prepare(DownloadArchiveProof[]? proofs, string currentTarget)
    {
        var prepared = new DownloadRemoval();
        int archives = 0, copies = 0;
        bool incomplete = false;
        var budget = new PreparationBudget();
        try
        {
            string downloads = DownloadsPath();
            UpdateHandoff.RequireRegularPath(downloads);
            // A custom known-folder location must not hold our app-data tree
            // open against the independent core cleanup, or reach a checkout.
            if (string.Equals(downloads, RemovalSession.DataRoot, StringComparison.OrdinalIgnoreCase)
                || downloads.StartsWith(RemovalSession.DataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || IsDevelopmentFolder(downloads)) return prepared;
            prepared._downloadsParents.AddAncestors(downloads);
            // The running EXE is independently authenticated by RemovalSession.
            // Only a standard extraction folder immediately in Downloads is
            // eligible for empty-folder cleanup, never Downloads itself.
            string targetDirectory = Path.GetDirectoryName(currentTarget)!;
            if (IsChild(targetDirectory, downloads) && !IsDevelopmentFolder(targetDirectory))
                prepared.RememberDirectory(targetDirectory);
            if (proofs is null || proofs.Length == 0 || proofs.Length > MaxProofs
                || proofs.Any(proof => proof is null || !UpdateHandoff.IsHash(proof.Sha256)
                    || proof.Size <= 0 || proof.Size > MaxArchiveBytes)) return prepared;

            var archiveHashes = proofs.Select(proof => proof.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var executableHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
            int considered = 0;
            foreach (string path in Directory.EnumerateFiles(downloads, "Nstrap-win-x64*.zip", options))
            {
                budget.VisitEntry();
                if (!IsChild(path, downloads) || !PackageStem().IsMatch(Path.GetFileNameWithoutExtension(path))) continue;
                if (++considered > MaxArchives || budget.Exhausted) { incomplete = true; break; }
                RemovalFiles.PreparedDownload? file = null;
                try
                {
                    file = new(path, archiveHashes, MaxArchiveBytes, budget.Consume);
                    if (!proofs.Any(proof => proof.Size == file.Length && string.Equals(proof.Sha256, file.Hash, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("The archive size differs from its release.");
                    using var archive = new ZipArchive(file.Content, ZipArchiveMode.Read, leaveOpen: true);
                    if (archive.Entries.Count != 1) throw new IOException("Unexpected release archive contents.");
                    var entry = archive.Entries[0];
                    int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (entry.FullName != "Nstrap.exe" || entry.Name != "Nstrap.exe"
                        || (entry.ExternalAttributes & (int)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                        || (unixType != 0 && unixType != 0x8000)
                        || entry.Length < 1024 * 1024 || entry.Length > MaxExecutableBytes)
                        throw new IOException("Unexpected release executable.");
                    using var content = entry.Open();
                    string executableHash = HashEntry(content, entry.Length, budget);
                    prepared._files.Add((file, new RemovalFiles.Pins()));
                    file = null;
                    executableHashes.Add(executableHash);
                    archives++;
                }
                catch (Exception error) when (OptionalFailure(error)) { incomplete = true; }
                finally { file?.Dispose(); }
            }

            if (executableHashes.Count != 0 && !budget.Exhausted)
            {
                IEnumerable<string> CopyCandidates()
                {
                    yield return Path.Combine(downloads, "Nstrap.exe");
                    foreach (string directory in Directory.EnumerateDirectories(downloads, "Nstrap-win-x64*", options))
                    {
                        budget.VisitEntry();
                        if (IsChild(directory, downloads) && PackageStem().IsMatch(Path.GetFileName(directory)))
                            yield return Path.Combine(directory, "Nstrap.exe");
                    }
                }
                considered = 0;
                foreach (string path in CopyCandidates())
                {
                    if (++considered > MaxCopies || budget.Exhausted) { incomplete = true; break; }
                    if (string.Equals(path, currentTarget, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                    string directory = Path.GetDirectoryName(path)!;
                    if (IsDevelopmentFolder(directory)) { incomplete = true; continue; }
                    var parents = new RemovalFiles.Pins();
                    RemovalFiles.PreparedDownload? file = null;
                    try
                    {
                        parents.AddAncestors(directory);
                        file = new(path, executableHashes, MaxExecutableBytes, budget.Consume);
                        if (!string.Equals(directory, downloads, StringComparison.OrdinalIgnoreCase))
                            prepared.RememberDirectory(directory);
                        prepared._files.Add((file, parents));
                        file = null;
                        parents = null!;
                        copies++;
                    }
                    catch (Exception error) when (OptionalFailure(error)) { incomplete = true; }
                    finally { file?.Dispose(); parents?.Dispose(); }
                }
            }
            else if (budget.Exhausted) incomplete = true;
            prepared.Status = $"Downloads prepared: {archives} ZIP(s), {copies} extra executable(s). "
                + (incomplete ? "Unverified or unchecked files will be kept." : "Other files will be kept.");
        }
        catch (Exception error) when (OptionalFailure(error))
        {
            prepared.Status = $"Downloads prepared: {archives} ZIP(s), {copies} extra executable(s). Unverified or unchecked files will be kept.";
        }
        return prepared;
    }

    internal void DeleteFiles()
    {
        foreach (var item in _files)
        {
            try { item.File.Delete(); }
            catch (Exception error) when (OptionalFailure(error)) { }
            finally { item.File.Dispose(); item.Parents.Dispose(); }
        }
        _files.Clear();
    }

    // Called only after RemovalSession releases its running EXE's ancestor pins.
    internal void DeleteEmptyDirectories()
    {
        foreach (var directory in _directories.Values)
        {
            try { directory.DeleteIfEmpty(); }
            catch (Exception error) when (OptionalFailure(error)) { }
        }
    }

    private void RememberDirectory(string path)
    {
        if (!PackageStem().IsMatch(Path.GetFileName(path)) || _directories.ContainsKey(path)) return;
        try
        {
            // Downloads can itself be relocated to a broad parent such as the
            // user profile. Never remove Desktop, Documents or another Windows
            // special folder (or one of its ancestors), even when it is empty.
            foreach (var folder in Enum.GetValues<Environment.SpecialFolder>())
            {
                string known = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
                if (known.Length == 0) continue;
                known = Path.TrimEndingDirectorySeparator(Path.GetFullPath(known));
                if (string.Equals(path, known, StringComparison.OrdinalIgnoreCase)
                    || known.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            }
            _directories.Add(path, new(path));
        }
        catch (Exception error) when (OptionalFailure(error))
        { /* Unavailable folder metadata cannot authorize removing a directory. */ }
    }

    private static bool IsChild(string path, string parent) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), parent, StringComparison.OrdinalIgnoreCase);

    private static bool IsDevelopmentFolder(string path)
    {
        if (path.Split(Path.DirectorySeparatorChar).Any(part =>
            part.Equals(".codex", StringComparison.OrdinalIgnoreCase)
            || part.Equals(".agents", StringComparison.OrdinalIgnoreCase)
            || part.Equals(".tools", StringComparison.OrdinalIgnoreCase))) return true;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (new[] { ".git", ".hg", ".svn" }
                .Any(name => File.Exists(Path.Combine(current, name)) || Directory.Exists(Path.Combine(current, name)))
                || File.Exists(Path.Combine(current, "Nstrap.slnx"))) return true;
        }
        return false;
    }

    private static string HashEntry(Stream stream, long expectedLength, PreparationBudget budget)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long length = 0;
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            if (read > expectedLength - length) throw new IOException("The release entry exceeds its declared length.");
            budget.Consume(read);
            hash.AppendData(buffer, 0, read);
            length += read;
        }
        if (length != expectedLength) throw new IOException("The release entry is incomplete.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class PreparationBudget
    {
        private readonly long _started = Environment.TickCount64;
        private long _bytes;
        private int _entries;
        public bool Exhausted => _bytes >= 1024L * 1024 * 1024 || _entries > 256 || Environment.TickCount64 - _started >= 4_000;
        public void VisitEntry()
        {
            _entries++;
            if (Exhausted) throw new IOException("Download preparation reached its limit.");
        }
        public void Consume(int bytes)
        {
            _bytes += bytes;
            if (Exhausted) throw new IOException("Download preparation reached its limit.");
        }
    }

    private static bool OptionalFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or ExternalException or ArgumentException or NotSupportedException or InvalidOperationException;

    private static string DownloadsPath()
    {
        Guid id = new("374DE290-123F-4565-9164-39C4925E467B");
        IntPtr value = IntPtr.Zero;
        try
        {
            // DONT_VERIFY retrieves the current per-user location without creating
            // it or probing a redirected network share. Regular local paths only.
            int result = SHGetKnownFolderPath(ref id, 0x4000, IntPtr.Zero, out value);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            string path = Marshal.PtrToStringUni(value) ?? throw new IOException("Windows did not return a Downloads folder.");
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("Downloads cleanup requires a local drive.");
            string root = Path.GetPathRoot(path) ?? throw new IOException("Downloads has no drive root.");
            if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable))
                throw new IOException("Downloads cleanup requires a local drive.");
            UpdateHandoff.RequireRegularPath(path);
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        finally { if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value); }
    }

    public void Dispose()
    {
        foreach (var item in _files) { item.File.Dispose(); item.Parents.Dispose(); }
        _files.Clear();
        foreach (var directory in _directories.Values) directory.Dispose();
        _directories.Clear();
        _downloadsParents.Dispose();
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
}
