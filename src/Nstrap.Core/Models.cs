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
    public bool UnlockFps { get; set; }
    public bool FpsChangePending { get; set; }
    public static AppState Create()
    {
        var profile = new FlagProfile { Name = "Default" };
        return new() { Profiles = [profile], SelectedProfileId = profile.Id };
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
