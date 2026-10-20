using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nstrap.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }
}

public sealed class FlagEntry : Observable
{
    private string _name = "";
    private string _value = "true";
    private bool _enabled = true;
    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) { Changed(nameof(Support)); Changed(nameof(TypeHint)); } }
    }
    public string Value { get => _value; set => Set(ref _value, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    [JsonIgnore] public string Support => FlagCatalog.IsAllowed(Name) ? "Player allowlist" : "Unlisted · support unknown";
    [JsonIgnore] public string TypeHint => FlagValidator.GetTypeHint(Name);
    public FlagEntry Copy() => new() { Name = Name, Value = Value, Enabled = Enabled };
}

public sealed class FlagProfile : Observable
{
    private string _name = "My profile";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public ObservableCollection<FlagEntry> Flags { get; set; } = [];
    public FlagProfile Copy(string name) => new() { Name = name, Flags = new(Flags.Select(f => f.Copy())) };
}

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public Guid SelectedProfileId { get; set; }
    public ObservableCollection<FlagProfile> Profiles { get; set; } = [];
    public List<string> CustomRoots { get; set; } = [];
    public string? SelectedInstallationPath { get; set; }
    public bool ApplyBeforeLaunch { get; set; } = true;
    public bool CloseAfterLaunch { get; set; }
    public bool AutoUpdate { get; set; } = true;
    // Missing preferences in existing profiles adopt the new default palette.
    public string Theme { get; set; } = "Crimson";
    public string DisplayName { get; set; } = "N strap";
    public string? CustomIconFile { get; set; }
    public bool KillSwitchEnabled { get; set; }
    public HotkeyBinding? KillSwitchHotkey { get; set; }
    public bool NukeEnabled { get; set; }
    public HotkeyBinding? NukeHotkey { get; set; }
    public bool NukeScopeAcknowledged { get; set; }
    public bool UnlockFps { get; set; }
    public int FpsCap { get; set; } = FpsSettingsManager.UnlockedCap;
    public bool FpsChangePending { get; set; }
    public int GraphicsQuality { get; set; }
    public bool GraphicsQualityManaged { get; set; }
    public bool GraphicsQualityChangePending { get; set; }
    public static AppState Create()
    {
        var profile = new FlagProfile { Name = "Default" };
        return new() { Profiles = [profile], SelectedProfileId = profile.Id };
    }
}

public sealed record HotkeyBinding(uint Modifiers, uint VirtualKey)
{
    public void Validate()
    {
        // Win32 MOD_ALT / MOD_CONTROL / MOD_SHIFT. Windows combinations and F12
        // are reserved by Windows; require Ctrl or Alt to avoid ordinary typing.
        if ((Modifiers & ~7u) != 0 || (Modifiers & 3u) == 0)
            throw new InvalidDataException("Use Ctrl or Alt with another key. Windows-key shortcuts are reserved.");
        if (!(VirtualKey is 0x08 or 0x09 or 0x0D or 0x20
            or >= 0x21 and <= 0x28 or 0x2D or 0x2E
            or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A
            or >= 0x60 and <= 0x7A or >= 0x7C and <= 0x87
            or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDE))
            throw new InvalidDataException("Choose a letter, number, function key other than F12, or a navigation key.");
    }
}

public enum ClientKind { Player, Studio }
public sealed record RobloxInstallation(string DirectoryPath, string ExecutablePath, ClientKind Kind, DateTime LastModified)
{
    public string Version => Path.GetFileName(DirectoryPath);
    public string DisplayName => $"{Kind}  ·  {Version}";
    public string ConfigPath => Path.Combine(DirectoryPath, "ClientSettings", "ClientAppSettings.json");
}

public sealed record FlagPreset(string Name, string Category, string Description, string Caveat, IReadOnlyList<FlagEntry> Flags);
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = true };
}
