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
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string StatePath => Path.Combine(DirectoryPath, "profiles.json");
    public AppState Load()
    {
        if (!File.Exists(StatePath)) return AppState.Create();
        var bytes = File.ReadAllBytes(StatePath);
        if (bytes.Length > 10 * 1024 * 1024) throw new InvalidDataException("The profiles file exceeds the 10 MB limit.");
        var state = JsonSerializer.Deserialize<AppState>(bytes, JsonDefaults.Options)
            ?? throw new InvalidDataException("The profiles file is empty.");
        if (state.SchemaVersion != 1) throw new InvalidDataException("This profiles file was created by an incompatible N strap version.");
        if (state.Profiles is null || state.CustomRoots is null || state.CustomRoots.Any(p => p is null)
            || state.Profiles.Any(p => p is null || p.Name is null || p.Flags is null || p.Flags.Any(f => f is null || f.Name is null || f.Value is null)))
            throw new InvalidDataException("The profiles file contains missing records.");
        if (state.Profiles.Count == 0) throw new InvalidDataException("The profiles file contains no profiles. The file has been preserved for recovery.");
        if (state.Profiles.Select(p => p.Id).Distinct().Count() != state.Profiles.Count)
            throw new InvalidDataException("The profiles file contains duplicate identifiers.");
        return state;
    }
    public void Save(AppState state) => AtomicFile.WriteJson(StatePath, state);
}
