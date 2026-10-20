using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nstrap.App.Services;

internal sealed record AppRelease(Version Version, bool Preview, Uri Download, string Sha256, long Size);

/// <summary>
/// The trust source is the fixed public GitHub repository over HTTPS. Its asset
/// digest detects a damaged or substituted download; it is not a code signature.
/// </summary>
internal static partial class GitHubUpdates
{
    public const string ReleasesPage = "https://github.com/Lukeice12/Nstrap/releases";
    private const string AssetName = "Nstrap-win-x64.zip";
    private const long MaxArchive = 256L * 1024 * 1024;
    private const long MaxExecutable = 512L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    public static Version CurrentVersion => Normalize(typeof(GitHubUpdates).Assembly.GetName().Version!);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nstrap/" + CurrentVersion.ToString(3));
        return client;
    }

    public static Version Normalize(Version version) => new(version.Major, version.Minor, version.Build);

    [GeneratedRegex(@"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    public static async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/Lukeice12/Nstrap/releases?per_page=100");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new IOException("GitHub is limiting update requests. Try again later or open the releases page.");
        response.EnsureSuccessStatusCode();
        await using var memory = new MemoryStream();
        await CopyBoundedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), memory, 2 * 1024 * 1024, timeout.Token);
        using var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new IOException("GitHub returned an invalid release list.");
        var candidates = new List<(Version Version, JsonElement Release)>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!TagPattern().IsMatch(tag) || !Version.TryParse(tag[1..], out var version)) continue;
            if (version > CurrentVersion) candidates.Add((version, release));
        }
        if (candidates.Count == 0) return null;
        var selected = candidates.OrderByDescending(item => item.Version).First();
        var assets = selected.Release.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == AssetName).ToArray();
        if (assets.Length != 1) throw new IOException("The newest release does not have one supported Windows x64 package. Open releases to download it manually.");
        var asset = assets[0];
        string expectedUrl = $"{ReleasesPage}/download/v{selected.Version.ToString(3)}/{AssetName}";
        string? digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
        long size = asset.GetProperty("size").GetInt64();
        if (asset.GetProperty("state").GetString() != "uploaded"
            || asset.GetProperty("browser_download_url").GetString() != expectedUrl
            || size <= 0 || size > MaxArchive
            || digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)
            || !UpdateHandoff.IsHash(digest[7..]))
            throw new IOException("The release package has incomplete or unexpected integrity information. Use the releases page.");
        return new(selected.Version, selected.Release.GetProperty("prerelease").GetBoolean(),
            new Uri(expectedUrl), digest[7..].ToUpperInvariant(), size);
    }

    public static async Task DownloadAsync(AppRelease release, string jobDirectory, IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        string archivePath = Path.Combine(jobDirectory, "package.zip");
        using (var response = await GetDownloadAsync(release.Download, timeout.Token))
        {
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new IOException("The download size does not match the published asset.");
            await using var archiveFile = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await CopyBoundedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), archiveFile,
                release.Size, timeout.Token, progress, release.Size);
            if (archiveFile.Length != release.Size) throw new IOException("The update download is incomplete.");
            await archiveFile.FlushAsync(timeout.Token);
        }
        timeout.Token.ThrowIfCancellationRequested();
        progress.Report("Checking the download...");
        if (!UpdateHandoff.HashMatches(archivePath, release.Sha256)) throw new IOException("The update's SHA256 does not match GitHub. No installation files were changed.");
        timeout.Token.ThrowIfCancellationRequested();
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            if (archive.Entries.Count != 1) throw new IOException("The update archive must contain exactly Nstrap.exe.");
            var entry = archive.Entries[0];
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (entry.FullName != "Nstrap.exe" || entry.Name != "Nstrap.exe"
                || (entry.ExternalAttributes & (int)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || (unixType != 0 && unixType != 0x8000) || entry.Length < 1024 * 1024 || entry.Length > MaxExecutable)
                throw new IOException("The update archive has an unexpected executable entry.");
            await using var extracted = new FileStream(Path.Combine(jobDirectory, "update.exe"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await CopyBoundedAsync(entry.Open(), extracted, entry.Length, timeout.Token);
            if (extracted.Length != entry.Length) throw new IOException("The extracted executable is incomplete.");
            await extracted.FlushAsync(timeout.Token);
        }
        ValidateExecutable(Path.Combine(jobDirectory, "update.exe"), release.Version);
    }

    private static async Task<HttpResponseMessage> GetDownloadAsync(Uri url, CancellationToken cancellation)
    {
        for (int redirects = 0; redirects <= 4; redirects++)
        {
            if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length != 0
                || !(url.Host == "github.com" || url.Host == "release-assets.githubusercontent.com" || url.Host == "objects.githubusercontent.com"))
                throw new IOException("GitHub redirected the update to an unsupported download host.");
            if (url.Host == "github.com" && !url.AbsolutePath.StartsWith("/Lukeice12/Nstrap/releases/download/", StringComparison.Ordinal))
                throw new IOException("The update URL is outside the N strap repository.");
            var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new IOException("The update redirect has no destination.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new IOException("The update download redirected too many times.");
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken cancellation,
        IProgress<string>? progress = null, long total = 0)
    {
        await using (source)
        {
            var buffer = new byte[81920];
            long copied = 0;
            int lastPercent = -1, read;
            while ((read = await source.ReadAsync(buffer, cancellation)) != 0)
            {
                copied += read;
                if (copied > limit) throw new IOException("The update response exceeds its permitted size.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellation);
                int percent = total == 0 ? 0 : (int)(copied * 100 / total);
                if (progress is not null && percent != lastPercent) { progress.Report($"Downloading update... {percent}%"); lastPercent = percent; }
            }
        }
    }

    public static void ValidateExecutable(string path, Version expected)
    {
        UpdateHandoff.RequireRegularPath(path);
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName != "N strap" || info.FileMajorPart != expected.Major
            || info.FileMinorPart != expected.Minor || info.FileBuildPart != expected.Build || info.FilePrivatePart != 0)
            throw new IOException("The update executable's product or version does not match the release.");
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64
            || (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0
            || (pe.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) != 0)
            throw new IOException("The update is not a Windows x64 executable.");
    }
}
