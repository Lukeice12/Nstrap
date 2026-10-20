using System.Globalization;

namespace Nstrap.Core;

public sealed record FlagChoice(string Label, IReadOnlyList<FlagEntry> Flags, bool IsCustom = false)
{
    public bool IsSelectable => !IsCustom;
}

/// <summary>A profile view over a fixed set of flags, with no separately saved state.</summary>
public sealed class FlagSetting
{
    private readonly HashSet<string> _names;
    private readonly bool _allowAbsentFalse;
    public string Name { get; }
    public FlagChoice Automatic { get; } = new("Automatic", []);
    public FlagChoice Custom { get; } = new("Custom", [], IsCustom: true);
    public IReadOnlyList<FlagChoice> Choices { get; }

    public FlagSetting(string name, IReadOnlyList<FlagChoice> choices, bool allowAbsentFalse = false)
    {
        Name = name;
        _allowAbsentFalse = allowAbsentFalse;
        _names = new(choices.SelectMany(choice => choice.Flags).Select(flag => flag.Name), StringComparer.Ordinal);
        Choices = [Automatic, .. choices, Custom];
    }

    public FlagChoice ReadChoice(IEnumerable<FlagEntry> flags)
    {
        var relevant = flags.Where(flag => _names.Contains(flag.Name)).ToArray();
        if (relevant.Length == 0) return Automatic;
        // Disabled entries, partial groups, and duplicate drafts must remain visible
        // as custom rather than silently becoming an apparently active preset.
        if (relevant.Any(flag => !flag.Enabled)
            || relevant.Select(flag => flag.Name).Distinct(StringComparer.Ordinal).Count() != relevant.Length)
            return Custom;

        foreach (var choice in Choices.Where(choice => choice != Automatic && !choice.IsCustom))
        {
            if (relevant.Any(flag => !choice.Flags.Any(expected => expected.Name == flag.Name))) continue;
            bool matches = choice.Flags.All(expected =>
            {
                var actual = relevant.FirstOrDefault(flag => flag.Name == expected.Name);
                // A missing competing renderer preference and an explicit false
                // both leave that renderer unrequested.
                return actual is null
                    ? _allowAbsentFalse && expected.Value == "false"
                    : SameValue(expected.Name, actual.Value, expected.Value);
            });
            if (matches) return choice;
        }
        return Custom;
    }

    public IReadOnlyList<FlagEntry> WithChoice(IEnumerable<FlagEntry> flags, FlagChoice choice)
    {
        if (!Choices.Contains(choice) || choice.IsCustom)
            throw new ArgumentException("Choose a defined setting or Automatic.", nameof(choice));
        // Preserve unrelated entries, including disabled flags and unfinished drafts.
        return [.. flags.Where(flag => !_names.Contains(flag.Name)), .. choice.Flags.Select(flag => flag.Copy())];
    }

    private static bool SameValue(string name, string value, string expected) =>
        FlagValidator.GetTypeHint(name) == "Boolean"
            ? bool.TryParse(value, out bool actualBool) && bool.TryParse(expected, out bool expectedBool) && actualBool == expectedBool
            : int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int actualInt)
                && int.TryParse(expected, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int expectedInt)
                && actualInt == expectedInt;
}
