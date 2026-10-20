using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using Nstrap.Core;

namespace Nstrap.App;

public sealed partial class MainViewModel : Observable
{
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private FlagProfile _selectedProfile;
    private RobloxInstallation? _selectedInstallation;
    private string _search = "", _status = "", _statusDetail = "";
    private string _fpsStatus = "Player only. Turn off to restore the previous cap.";
    private bool _isBusy, _hasError;
    private int _page;
    public ProfileStore Store { get; }
    public SettingsApplier Applier { get; }
    public FpsSettingsManager FpsSettings { get; }
    public AppState State { get; }
    public ObservableCollection<FlagProfile> Profiles => State.Profiles;
    public ObservableCollection<RobloxInstallation> Installations { get; } = [];
    public IReadOnlyList<FlagPreset> Presets => FlagCatalog.Presets;
    public ICollectionView FlagsView { get; private set; }
    public string DataDirectory => Store.DirectoryPath;
    public string AppVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.8";
    public bool UnlockFps => State.UnlockFps;
    public bool FpsChangePending => State.FpsChangePending;
    public string FpsStatus { get => _fpsStatus; set => Set(ref _fpsStatus, value); }
    public string CatalogDate => FlagCatalog.CheckedOn;
    public string FlagSummary => $"{SelectedProfile.Flags.Count(f => f.Enabled)} enabled · {SelectedProfile.Flags.Count} total";
    public string FlagCounter => $"{SelectedProfile.Flags.Count} flag{(SelectedProfile.Flags.Count == 1 ? "" : "s")} · {SelectedProfile.Flags.Count(f => f.Enabled)} enabled";
    public string ProfileSummary => $"{Profiles.Count} local profile{(Profiles.Count == 1 ? "" : "s")}";
    public string InstallationSummary => Installations.Count == 0 ? "No clients found" : $"{Installations.Count} client{(Installations.Count == 1 ? "" : "s")} detected";
    public string ClientTitle => SelectedInstallation is null ? "No client selected" : $"Roblox {SelectedInstallation.Kind}";
    public string ClientDetail => SelectedInstallation is null ? "Install Player or choose an installation folder." : SelectedInstallation.Version;
    public string ClientPath => SelectedInstallation?.DirectoryPath ?? "No installation selected";
    public bool HasInstallation => SelectedInstallation is not null;
    public bool EmptyFlags => SelectedProfile.Flags.Count == 0;
    public string SelectedProfileName => SelectedProfile.Name;
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) Changed(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;
    public bool HasError { get => _hasError; set => Set(ref _hasError, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }
    public int Page { get => _page; set => Set(ref _page, value); }
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) FlagsView.Refresh(); }
    }
    public bool ApplyBeforeLaunch
    {
        get => State.ApplyBeforeLaunch;
        set { State.ApplyBeforeLaunch = value; Changed(); ScheduleSave(); }
    }
    public FlagProfile SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value is null || !Set(ref _selectedProfile, value)) return;
            State.SelectedProfileId = value.Id;
            FlagsView = MakeView();
            Changed(nameof(FlagsView));
            UpdateSummaries();
            ScheduleSave();
        }
    }
    public RobloxInstallation? SelectedInstallation
    {
        get => _selectedInstallation;
        set
        {
            if (!Set(ref _selectedInstallation, value)) return;
            State.SelectedInstallationPath = value?.ExecutablePath;
            foreach (var name in new[] { nameof(ClientTitle), nameof(ClientDetail), nameof(ClientPath), nameof(HasInstallation) }) Changed(name);
            ScheduleSave();
        }
    }

    public MainViewModel(string directory, string? fpsSettingsPath = null)
    {
        Store = new(directory);
        Applier = new(directory);
        FpsSettings = new(directory, fpsSettingsPath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "GlobalBasicSettings_13.xml"));
        State = Store.Load();
        if (State.FpsChangePending) _fpsStatus = "FPS change pending.";
        _selectedProfile = State.Profiles.FirstOrDefault(p => p.Id == State.SelectedProfileId) ?? State.Profiles[0];
        FlagsView = MakeView();
        foreach (var profile in Profiles) Observe(profile);
        Profiles.CollectionChanged += ProfilesChanged;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); try { Save(); } catch (Exception ex) { Error(ex); } };
        RefreshInstallations();
    }
    private ICollectionView MakeView()
    {
        var view = CollectionViewSource.GetDefaultView(SelectedProfile.Flags);
        view.Filter = item => item is FlagEntry flag && (string.IsNullOrWhiteSpace(Search) || flag.Name.Contains(Search, StringComparison.OrdinalIgnoreCase));
        return view;
    }
    private void ProfilesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.NewItems is not null) foreach (FlagProfile profile in args.NewItems) Observe(profile);
        if (args.OldItems is not null) foreach (FlagProfile profile in args.OldItems) Unobserve(profile);
        UpdateSummaries(); ScheduleSave();
    }
    private void Observe(FlagProfile profile)
    {
        profile.PropertyChanged += Edited;
        profile.Flags.CollectionChanged += FlagsChanged;
        foreach (var flag in profile.Flags) flag.PropertyChanged += Edited;
    }
    private void Unobserve(FlagProfile profile)
    {
        profile.PropertyChanged -= Edited;
        profile.Flags.CollectionChanged -= FlagsChanged;
        foreach (var flag in profile.Flags) flag.PropertyChanged -= Edited;
    }
    private void FlagsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.NewItems is not null) foreach (FlagEntry flag in args.NewItems) flag.PropertyChanged += Edited;
        if (args.OldItems is not null) foreach (FlagEntry flag in args.OldItems) flag.PropertyChanged -= Edited;
        UpdateSummaries(); ScheduleSave();
    }
    private void Edited(object? sender, PropertyChangedEventArgs args) { UpdateSummaries(); ScheduleSave(); }
    private void UpdateSummaries()
    {
        foreach (var name in new[] { nameof(FlagSummary), nameof(FlagCounter), nameof(ProfileSummary), nameof(EmptyFlags), nameof(SelectedProfileName) }) Changed(name);
    }
    private void ScheduleSave() { _saveTimer.Stop(); _saveTimer.Start(); }
    public void Save() { _saveTimer.Stop(); Store.Save(State); }
    public void QueueFpsChange(bool enabled)
    {
        bool oldEnabled = State.UnlockFps, oldPending = State.FpsChangePending;
        State.UnlockFps = enabled;
        State.FpsChangePending = true;
        try { Save(); }
        catch { State.UnlockFps = oldEnabled; State.FpsChangePending = oldPending; throw; }
        finally { Changed(nameof(UnlockFps)); Changed(nameof(FpsChangePending)); }
    }
    public void CompleteFpsChange()
    {
        State.FpsChangePending = false;
        try { Save(); }
        catch { State.FpsChangePending = true; throw; }
        finally { Changed(nameof(FpsChangePending)); }
    }
    public void CommitImport(FlagImportPlan plan)
    {
        var previous = SelectedProfile.Flags.ToArray();
        ReplaceFlags(plan.MergedFlags);
        try { Save(); }
        catch
        {
            ReplaceFlags(previous);
            _saveTimer.Stop();
            throw;
        }
    }
    private void ReplaceFlags(IEnumerable<FlagEntry> flags)
    {
        while (SelectedProfile.Flags.Count > 0) SelectedProfile.Flags.RemoveAt(SelectedProfile.Flags.Count - 1);
        foreach (var flag in flags) SelectedProfile.Flags.Add(flag);
    }
    public void RefreshInstallations()
    {
        string? prior = State.SelectedInstallationPath;
        var found = InstallationDiscovery.Find(State.CustomRoots);
        Installations.Clear();
        foreach (var client in found) Installations.Add(client);
        SelectedInstallation = Installations.FirstOrDefault(i => string.Equals(i.ExecutablePath, prior, StringComparison.OrdinalIgnoreCase))
            ?? Installations.FirstOrDefault();
        Changed(nameof(InstallationSummary));
    }
    public void Success(string title, string detail = "")
    {
        HasError = false; Status = title; StatusDetail = detail;
    }
    public void Error(Exception exception)
    {
        HasError = true; Status = "That action could not be completed."; StatusDetail = exception.Message;
    }
}
