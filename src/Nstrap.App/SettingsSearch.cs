using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace Nstrap.App;

public sealed record SettingsSearchResult(string Title, string Description, int Page, string Keywords);

public sealed partial class MainViewModel
{
    private static readonly SettingsSearchResult[] SearchEntries =
    [
        new("Roblox installation", "Select, refresh, add or install a Roblox client.", 0, "overview player studio folder version download"),
        new("FPS limit", "Set a custom frame rate or turn it on or off.", 0, "performance framerate cap unlock maximum refresh retry"),
        new("Graphics quality", "Choose Automatic or levels 1–10; apply or restore the previous quality.", 0, "performance rendering visuals graphics manual automatic slider"),
        new("Close after launch", "Close N strap after Roblox has started.", 0, "launch close exit automatic app startup"),
        new("Apply before launch", "Apply the selected flag profile before launching Roblox.", 4, "launch apply automatic profile startup settings"),
        new("FastFlags", "Edit flags, profiles and values, or import a JSON or text file.", 1, "flags fflag dfflag fint dfint fstring unrestricted raw json editor import export add enable disable"),
        new("Profiles", "Create, rename, duplicate or delete a flag profile.", 1, "profile preset configuration save"),
        new("Low detail preset", "Use low mesh detail, lighter textures, no terrain grass and no MSAA.", 2, "presets rendering graphics performance quick low detail"),
        new("Mesh detail", "Choose Automatic or a geometry detail preset.", 2, "presets geometry mesh lod level distance low quality"),
        new("Terrain grass", "Use Automatic grass drawing or turn it off.", 2, "presets geometry terrain grass foliage drawing distance"),
        new("Anti-aliasing", "Choose Automatic, Off, 2× or 4× MSAA.", 2, "presets graphics rendering antialiasing msaa samples edges"),
        new("Texture quality", "Choose Automatic, lower or higher texture quality.", 2, "presets graphics textures memory detail"),
        new("Preserve quality on high DPI", "Request native resolution on scaled displays.", 2, "presets graphics rendering dpi scaling display resolution sharp native"),
        new("Rendering quality override", "Choose an FRM rendering quality override from levels 1–21.", 2, "presets graphics rendering quality frm level override"),
        new("Rendering mode", "Choose Automatic, Direct3D 11 or Vulkan.", 2, "presets graphics renderer directx d3d11 vulkan backend"),
        new("Quick presets", "Add Direct3D 11, texture, grass, native DPI or manual fullscreen presets.", 2, "presets rendering textures graphics performance fullscreen alt enter"),
        new("Restore and backups", "Restore managed FastFlags or open your backups.", 3, "recovery restore backup undo reset settings files"),
        new("Cache cleanup", "Manually remove old Roblox cache files.", 4, "cache clean cleanup disk storage rbx-storage space temporary"),
        new("About N strap", "Version, data folder, documentation and support.", 4, "about settings contact discord help data location directory"),
        new("Licenses", "Open component repositories and bundled license notices.", 4, "license licensing MIT notices copyright credits"),
        new("N strap updates", "Check for updates, install a release or change automatic updates.", 5, "updates update upgrade github release automatic download restart version"),
        new("Color theme", "Choose Crimson, Dark or Light. Changes apply immediately.", 6, "appearance color theme crimson dark light palette"),
        new("Program names", "Customize the display name or rename the executable and process.", 6, "appearance title sidebar task manager exe filename identity rename"),
        new("Custom icon", "Choose your own PNG or ICO for the sidebar, window and taskbar.", 6, "appearance image logo icon artwork reset original"),
        new("Kill switch", "Set a global shortcut to close N strap immediately.", 7, "experimental hotkey keyboard shortcut kill switch exit silent emergency"),
        new("Nuke", "Set an opt-in shortcut to remove N strap and its saved data.", 7, "experimental hotkey keyboard shortcut uninstall remove delete nuke silent")
    ];
    private string _settingsSearch = "";
    public ObservableCollection<SettingsSearchResult> SettingsSearchResults { get; } = [];
    public bool HasSettingsSearch => !string.IsNullOrWhiteSpace(SettingsSearch);
    public bool NoSettingsSearchResults => HasSettingsSearch && SettingsSearchResults.Count == 0;
    public string SettingsSearch
    {
        get => _settingsSearch;
        set
        {
            if (!Set(ref _settingsSearch, value ?? "")) return;
            SettingsSearchResults.Clear();
            if (HasSettingsSearch)
            {
                var terms = _settingsSearch[..Math.Min(_settingsSearch.Length, 256)]
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(16).ToArray();
                foreach (var entry in SearchEntries)
                {
                    string text = $"{entry.Title} {entry.Description} {entry.Keywords}";
                    if (terms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
                        SettingsSearchResults.Add(entry);
                }
            }
            Changed(nameof(HasSettingsSearch));
            Changed(nameof(NoSettingsSearchResults));
        }
    }
}

public partial class MainWindow
{
    private void SettingsSearchResultClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SettingsSearchResult result }) return;
        var navigation = result.Page switch
        {
            0 => HomeNav,
            1 => FlagsNav,
            2 => PresetsNav,
            3 => RecoveryNav,
            4 => SettingsNav,
            5 => UpdatesNav,
            6 => AppearanceNav,
            7 => ExperimentalNav,
            _ => null
        };
        if (navigation is null) return;
        Model.SettingsSearch = "";
        navigation.IsChecked = true;
        Model.Page = result.Page;
        navigation.Focus();
    }
}
