using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nstrap.Core;

public sealed record FlagImportDocument(List<FlagEntry> Flags, bool HasIgnoredMetadata)
{
    public string Notice => HasIgnoredMetadata
        ? "Imported static flag values only. Bindings, cycles, and other metadata are not applied."
        : "";
}

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

    public static List<FlagEntry> Import(string json) => ParseImport(json).Flags;

    public static FlagImportDocument ParseImport(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var document = ParseDocument(bytes);
        var flags = new List<FlagEntry>();
        bool ignoredMetadata = false;
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in document.RootElement.EnumerateObject())
                AddImportedFlag(flags, item.Name, item.Value, enabled: true);
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in document.RootElement.EnumerateArray())
            {
                int index = flags.Count + 1;
                if (record.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"Record {index} must be an object with name and value fields.");
                var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in record.EnumerateObject())
                {
                    if (!fieldNames.Add(field.Name))
                        throw new InvalidDataException($"Record {index} contains a duplicate field: {field.Name}.");
                    fields.Add(field.Name, field.Value);
                    if (field.Name is not ("name" or "value" or "type" or "enabled") && HasMetadataValue(field.Value))
                        ignoredMetadata = true;
                }
                if (!fields.TryGetValue("name", out var name) || name.ValueKind != JsonValueKind.String
                    || !fields.TryGetValue("value", out var value))
                    throw new InvalidDataException($"Record {index} needs a string name and a value.");
                bool enabled = true;
                if (fields.TryGetValue("enabled", out var state))
                {
                    if (state.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException($"Record {index}: enabled must be true or false.");
                    enabled = state.GetBoolean();
                }
                string flagName = name.GetString()!;
                if (fields.TryGetValue("type", out var declaredType))
                {
                    var typeHint = declaredType.ValueKind == JsonValueKind.String ? declaredType.GetString()?.ToLowerInvariant() switch
                    {
                        "bool" or "boolean" => "Boolean",
                        "int" or "integer" => "Integer",
                        "string" => "String",
                        _ => null
                    } : null;
                    if (typeHint is null || typeHint != GetTypeHint(flagName))
                        throw new InvalidDataException($"Record {index}: type must be bool, int, or string and match the flag name's prefix.");
                }
                AddImportedFlag(flags, flagName, value, enabled);
            }
        }
        else
            throw new InvalidDataException("Import a JSON flag object or an array of records with name and value fields.");
        Validate(flags);
        return new(flags, ignoredMetadata);
    }

    private static void AddImportedFlag(List<FlagEntry> flags, string name, JsonElement value, bool enabled)
    {
        if (flags.Count >= MaximumFlags) throw new InvalidDataException($"The import exceeds {MaximumFlags} flags.");
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String))
            throw new InvalidDataException($"{name} must have a boolean, integer or string value.");
        string text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        Value(name, text); // Disabled records must be valid too.
        flags.Add(new() { Name = name, Value = text, Enabled = enabled });
    }

    private static bool HasMetadataValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => false,
        JsonValueKind.String => value.GetString()!.Length > 0,
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        _ => true
    };

    private static JsonDocument ParseDocument(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The settings file exceeds the 2 MB limit.");
        var span = bytes.AsSpan();
        if (span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) span = span[3..];
        return JsonDocument.Parse(span.ToArray(), new() { MaxDepth = 32 });
    }

    public static Dictionary<string, JsonElement> ReadObject(byte[] bytes)
    {
        using var document = ParseDocument(bytes);
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
