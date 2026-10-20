using System.Text.Json;

namespace Nstrap.Core;

public static class AtomicFile
{
    public static void Write(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".nstrap-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void WriteJson<T>(string path, T value) => Write(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options));
}

public sealed class ProfileStore(string directory)
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string StatePath => Path.Combine(DirectoryPath, "profiles.json");
    public AppState Load()
    {
        if (!File.Exists(StatePath)) return AppState.Create();
        using var input = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumBytes)
            throw new InvalidDataException($"The profiles file exceeds the 10 MB limit and has been preserved at {StatePath}. Make a backup copy, then reduce this file to 10 MB or less before reopening N strap.");
        var state = JsonSerializer.Deserialize<AppState>(input, JsonDefaults.Options)
            ?? throw new InvalidDataException("The profiles file is empty.");
        if (state.SchemaVersion != 1) throw new InvalidDataException("This profiles file was created by an incompatible N strap version.");
        if (state.Profiles is null || state.CustomRoots is null || state.CustomRoots.Any(p => p is null)
            || state.Profiles.Any(p => p is null || p.Name is null || p.Flags is null || p.Flags.Any(f => f is null || f.Name is null || f.Value is null)))
            throw new InvalidDataException("The profiles file contains missing records.");
        if (state.Profiles.Count == 0) throw new InvalidDataException("The profiles file contains no profiles. The file has been preserved for recovery.");
        if (state.Profiles.Select(p => p.Id).Distinct().Count() != state.Profiles.Count)
            throw new InvalidDataException("The profiles file contains duplicate identifiers.");
        ValidatePreferences(state);
        return state;
    }
    public void Save(AppState state)
    {
        ValidatePreferences(state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonDefaults.Options);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The profiles would exceed the 10 MB limit. This change was not saved; the existing profiles file was left untouched. Remove unneeded profiles or reduce their contents before saving.");
        AtomicFile.Write(StatePath, bytes);
    }
    private static void ValidatePreferences(AppState state)
    {
        if (state.Theme is not ("Crimson" or "Dark" or "Light"))
            throw new InvalidDataException("The saved appearance theme must be Crimson, Dark or Light. The profiles file was preserved.");
        if (string.IsNullOrWhiteSpace(state.DisplayName) || state.DisplayName.Length > 48 || state.DisplayName.Any(char.IsControl))
            throw new InvalidDataException("The saved program name must contain 1–48 characters without line breaks or control characters. The profiles file was preserved.");
        if (state.CustomIconFile is not null && (state.CustomIconFile.Length > 80
            || !state.CustomIconFile.StartsWith("icon-", StringComparison.Ordinal)
            || state.CustomIconFile != Path.GetFileName(state.CustomIconFile)
            || state.CustomIconFile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("The saved custom icon reference is invalid. The profiles file was preserved.");
        if (state.FpsCap is < 1 or > FpsSettingsManager.UnlockedCap)
            throw new InvalidDataException("The saved FPS cap must be between 1 and 9,999. The profiles file was preserved.");
        if (state.GraphicsQuality is < 0 or > 10)
            throw new InvalidDataException("The saved graphics quality must be Automatic (0) or a level from 1 to 10. The profiles file was preserved.");
    }
}
