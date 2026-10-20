namespace Nstrap.Core;

public static class InstallationDiscovery
{
    public static IReadOnlyList<RobloxInstallation> Find(IEnumerable<string>? customRoots = null)
    {
        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions")
        };
        if (customRoots is not null) roots.AddRange(customRoots);
        var found = new Dictionary<string, RobloxInstallation>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                Inspect(root, found);
                foreach (var child in Directory.EnumerateDirectories(root))
                {
                    Inspect(child, found);
                    if (Path.GetFileName(child).Equals("Versions", StringComparison.OrdinalIgnoreCase))
                        foreach (var version in Directory.EnumerateDirectories(child)) Inspect(version, found);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found.Values.OrderBy(i => i.Kind).ThenByDescending(i => i.LastModified).ToArray();
    }
    private static void Inspect(string path, Dictionary<string, RobloxInstallation> result)
    {
        foreach (var kind in Enum.GetValues<ClientKind>())
        {
            var exe = Path.Combine(path, kind == ClientKind.Player ? "RobloxPlayerBeta.exe" : "RobloxStudioBeta.exe");
            if (File.Exists(exe)) result[exe] = new(Path.GetFullPath(path), Path.GetFullPath(exe), kind, File.GetLastWriteTimeUtc(exe));
        }
    }
}
