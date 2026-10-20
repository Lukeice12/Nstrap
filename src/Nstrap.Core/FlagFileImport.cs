using System.Text;

namespace Nstrap.Core;

public sealed record FlagImportPlan(int ImportedCount, IReadOnlyList<FlagEntry> MergedFlags);

/// <summary>Builds a fully validated replacement list without changing the live profile.</summary>
public static class FlagFileImport
{
    public static bool IsSupportedPath(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);

    public static FlagImportPlan Read(string path, IEnumerable<FlagEntry> current)
    {
        if (!IsSupportedPath(path)) throw new InvalidDataException("Choose one .txt or .json file containing a JSON flag object.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > FlagValidator.MaximumBytes) throw new InvalidDataException("The import file exceeds 2 MB.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return CreatePlan(current, reader.ReadToEnd());
    }

    public static FlagImportPlan CreatePlan(IEnumerable<FlagEntry> current, string json)
    {
        var imported = FlagValidator.Import(json);
        var merged = current.Select(flag => flag.Copy()).ToList();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < merged.Count; i++)
            if (!indices.TryAdd(merged[i].Name, i))
                throw new InvalidDataException($"The selected profile has a duplicate flag: {merged[i].Name}. Remove the duplicate before importing.");
        foreach (var flag in imported)
        {
            if (indices.TryGetValue(flag.Name, out int index)) merged[index] = flag;
            else { indices.Add(flag.Name, merged.Count); merged.Add(flag); }
        }
        FlagValidator.Validate(merged);
        return new(imported.Count, merged);
    }
}
