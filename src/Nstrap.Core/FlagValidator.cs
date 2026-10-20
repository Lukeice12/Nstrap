using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nstrap.Core;

public static partial class FlagValidator
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const int MaximumFlags = 5000;
    [GeneratedRegex(@"^(?:DF|SF|F)(?:Flag|Int|String|Log)[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex FlagNamePattern();
    public static string GetTypeHint(string name) =>
        name.StartsWith("FFlag", StringComparison.Ordinal) || name.StartsWith("DFFlag", StringComparison.Ordinal) || name.StartsWith("SFFlag", StringComparison.Ordinal) ? "Boolean" :
        name.StartsWith("FString", StringComparison.Ordinal) || name.StartsWith("DFString", StringComparison.Ordinal) || name.StartsWith("SFString", StringComparison.Ordinal) ? "String" : "Integer";

    public static JsonElement Value(string name, string value)
    {
        if (!FlagNamePattern().IsMatch(name))
            throw new InvalidDataException($"“{name}” is not a flag name. Use a prefix such as FFlag, DFFlag, FInt, DFInt or FString.");
        if (value.Length > 32768) throw new InvalidDataException($"The value for {name} is too long.");
        switch (GetTypeHint(name))
        {
            case "Boolean":
                if (!bool.TryParse(value, out var boolean))
                    throw new InvalidDataException($"{name} needs true or false.");
                return JsonSerializer.SerializeToElement(boolean);
            case "Integer":
                if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                    throw new InvalidDataException($"{name} needs a 32-bit whole number.");
                return JsonSerializer.SerializeToElement(number);
            default:
                return JsonSerializer.SerializeToElement(value);
        }
    }

    public static Dictionary<string, JsonElement> Validate(IEnumerable<FlagEntry> entries, bool includeDisabled = false)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        foreach (var entry in entries)
        {
            if (++count > MaximumFlags) throw new InvalidDataException($"A profile can contain at most {MaximumFlags} flags.");
            if (!names.Add(entry.Name)) throw new InvalidDataException($"Duplicate flag: {entry.Name}.");
            var value = Value(entry.Name, entry.Value);
            if (entry.Enabled || includeDisabled) result.Add(entry.Name, value);
        }
        return result;
    }

    public static List<FlagEntry> Import(string json)
    {
        var values = ReadObject(Encoding.UTF8.GetBytes(json));
        var flags = new List<FlagEntry>();
        foreach (var (name, value) in values)
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String))
                throw new InvalidDataException($"{name} must have a boolean, integer or string value.");
            string text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            Value(name, text);
            flags.Add(new() { Name = name, Value = text });
        }
        Validate(flags);
        return flags;
    }

    public static Dictionary<string, JsonElement> ReadObject(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The settings file exceeds the 2 MB limit.");
        var span = bytes.AsSpan();
        if (span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) span = span[3..];
        using var document = JsonDocument.Parse(span.ToArray(), new() { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Settings must be a JSON object: { \"FlagName\": value }.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateObject())
        {
            if (!result.TryAdd(item.Name, item.Value.Clone()))
                throw new InvalidDataException($"Duplicate JSON key: {item.Name}.");
            if (result.Count > MaximumFlags) throw new InvalidDataException($"The settings file exceeds {MaximumFlags} entries.");
        }
        return result;
    }

    public static string Export(IEnumerable<FlagEntry> entries) =>
        JsonSerializer.Serialize(Validate(entries), JsonDefaults.Options);
}
