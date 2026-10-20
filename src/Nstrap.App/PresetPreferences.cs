using Nstrap.Core;

namespace Nstrap.App;

public sealed partial class MainViewModel
{
    public FlagSetting MeshDetail => FlagCatalog.MeshDetail;
    public FlagSetting Msaa => FlagCatalog.Msaa;
    public FlagSetting Renderer => FlagCatalog.Renderer;
    public FlagSetting TextureQuality => FlagCatalog.TextureQuality;
    public FlagSetting Grass => FlagCatalog.Grass;
    public FlagSetting RenderingQuality => FlagCatalog.RenderingQuality;
    public FlagPreset LowDetailPreset => FlagCatalog.LowDetail;
    public IReadOnlyList<FlagChoice> RenderingQualityLevels { get; } =
        FlagCatalog.RenderingQuality.Choices.Where(choice => choice != FlagCatalog.RenderingQuality.Automatic).ToArray();

    public event EventHandler? PresetFlagsChanged;
    private void NotifyPresetFlagsChanged() => PresetFlagsChanged?.Invoke(this, EventArgs.Empty);

    public void SetPresetChoice(FlagSetting setting, FlagChoice choice) =>
        CommitPresetFlags(setting.WithChoice(SelectedProfile.Flags, choice));

    public void AddPreset(FlagPreset preset)
    {
        var names = preset.Flags.Select(flag => flag.Name).ToHashSet(StringComparer.Ordinal);
        CommitPresetFlags(SelectedProfile.Flags.Where(flag => !names.Contains(flag.Name))
            .Concat(preset.Flags.Select(flag => flag.Copy())));
    }

    private void CommitPresetFlags(IEnumerable<FlagEntry> entries)
    {
        var flags = entries.ToArray();
        if (flags.Length > FlagValidator.MaximumFlags && flags.Length > SelectedProfile.Flags.Count)
            throw new InvalidDataException($"This choice would exceed {FlagValidator.MaximumFlags:N0} flags. Remove some entries from this profile first.");
        CommitFlags(flags);
    }
}
