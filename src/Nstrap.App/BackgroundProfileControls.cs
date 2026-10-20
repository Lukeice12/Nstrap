using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Nstrap.App.Services;
using Nstrap.Core;

namespace Nstrap.App;

public sealed partial class MainViewModel
{
    private string _backgroundProfileStatus = "Off.";
    public string BackgroundProfileStatus { get => _backgroundProfileStatus; set => Set(ref _backgroundProfileStatus, value); }
}

public partial class MainWindow
{
    private readonly DispatcherTimer _backgroundDebounce = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private BackgroundProfileService? _backgroundProfiles;
    private BackgroundProfileSnapshot? _backgroundSnapshot;
    private Task _backgroundWork = Task.CompletedTask;
    private string? _backgroundRestorePause;
    private bool _backgroundRequested, _backgroundApplying, _backgroundControlsReady;

    private void InitializeBackgroundProfileControls()
    {
        if (_backgroundControlsReady) return;
        _backgroundControlsReady = true;
        _backgroundDebounce.Tick += BackgroundDebounceTick;
        Model.PreferencesSaved += BackgroundPreferencesSaved;
        Model.PropertyChanged += BackgroundModelChanged;
        Closed += (_, _) =>
        {
            StopBackgroundProfilePreparation();
            Model.PreferencesSaved -= BackgroundPreferencesSaved;
            Model.PropertyChanged -= BackgroundModelChanged;
        };
    }

    private void StartBackgroundProfilePreparation()
    {
        if (_backgroundProfiles is not null || !Model.SilentModeEnabled) return;
        try
        {
            // Startup can await other services while the visible UI has drafts.
            // Read saved state once instead of accidentally applying those drafts.
            _backgroundSnapshot = CaptureBackgroundSnapshot(Model.Store.Load());
            _backgroundRestorePause = null;
            _backgroundProfiles = new(Model.DataDirectory, BackgroundFilesystemChanged);
            Model.BackgroundProfileStatus = "Preparing the saved Player profile.";
            RequestBackgroundPreparation();
        }
        catch (Exception error) { Model.BackgroundProfileStatus = "Background preparation needs attention. " + error.Message; }
    }

    private async Task PrepareBackgroundProfileAtStartupAsync()
    {
        StartBackgroundProfilePreparation();
        _backgroundDebounce.Stop();
        await BeginBackgroundPassAsync();
    }

    private void StopBackgroundProfilePreparation()
    {
        _backgroundDebounce.Stop();
        _backgroundRequested = false;
        var service = _backgroundProfiles;
        _backgroundProfiles = null;
        service?.Dispose();
        Model.BackgroundProfileStatus = "Off.";
    }

    private async Task StopBackgroundProfilePreparationAsync()
    {
        StopBackgroundProfilePreparation();
        // Cancellation prevents subsequent targets; an already-entered atomic
        // settings transaction completes before a rename/update/exit proceeds.
        await _backgroundWork;
    }

    private void SuspendBackgroundProfileAfterRestore()
    {
        if (_backgroundProfiles is null) return;
        _backgroundRestorePause = _backgroundSnapshot?.ProfileFingerprint;
        _backgroundRequested = false;
        _backgroundDebounce.Stop();
        _backgroundProfiles.PausePendingWait();
        Model.BackgroundProfileStatus = "Paused after Restore for this run. Save a changed profile or re-enable Silent mode to prepare again. The next app startup prepares the saved profile again.";
    }

    private void BackgroundPreferencesSaved(object? sender, EventArgs args)
    {
        // This observer is part of Save's call stack. It must never turn a
        // successful persistence operation into a rollback or a save error.
        try
        {
            if (_backgroundProfiles is null) return;
            Dispatcher.VerifyAccess();
            var snapshot = CaptureBackgroundSnapshot(Model.State);
            if (snapshot.Fingerprint == _backgroundSnapshot?.Fingerprint) return;
            _backgroundSnapshot = snapshot;
            if (_backgroundRestorePause != snapshot.ProfileFingerprint) _backgroundRestorePause = null;
            RequestBackgroundPreparation();
        }
        catch (Exception error)
        {
            try { Model.BackgroundProfileStatus = "Saved profile needs attention. " + error.Message; }
            catch { /* A persistence observer must never propagate to Save. */ }
        }
    }

    private static BackgroundProfileSnapshot CaptureBackgroundSnapshot(AppState state)
    {
        var profile = state.Profiles.FirstOrDefault(profile => profile.Id == state.SelectedProfileId) ?? state.Profiles.First();
        var flags = profile.Flags.Select(flag => new BackgroundFlag(flag.Name, flag.Value, flag.Enabled)).ToArray();
        return new(profile.Id, profile.Name, flags, state.CustomRoots.ToArray());
    }

    private void BackgroundModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Model.IsBusy) && !Model.IsBusy && !_backgroundApplying && _backgroundRequested)
            RequestBackgroundPreparation();
    }

    private void BackgroundFilesystemChanged()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try { Dispatcher.BeginInvoke(new Action(RequestBackgroundPreparation), DispatcherPriority.Background); }
        catch (InvalidOperationException) { }
    }

    private void RequestBackgroundPreparation()
    {
        if (_backgroundProfiles is null || _backgroundSnapshot is null || _backgroundRestorePause is not null) return;
        _backgroundRequested = true;
        _backgroundDebounce.Stop();
        if (!Model.IsBusy) _backgroundDebounce.Start();
    }

    private async void BackgroundDebounceTick(object? sender, EventArgs args)
    {
        _backgroundDebounce.Stop();
        await BeginBackgroundPassAsync();
    }

    private Task BeginBackgroundPassAsync()
    {
        if (!_backgroundRequested || _backgroundProfiles is null || _backgroundSnapshot is null || _backgroundRestorePause is not null)
            return Task.CompletedTask;
        if (Model.IsBusy) return _backgroundApplying ? _backgroundWork : Task.CompletedTask;
        var service = _backgroundProfiles;
        var snapshot = _backgroundSnapshot;
        _backgroundRequested = false;
        _backgroundApplying = true;
        // Reservation and snapshot capture occur on the same dispatcher as Busy,
        // import, Apply and Restore. Workers never inspect ObservableCollections.
        Model.IsBusy = true;
        return _backgroundWork = RunBackgroundPassAsync(service, snapshot);
    }

    private async Task RunBackgroundPassAsync(BackgroundProfileService service, BackgroundProfileSnapshot snapshot)
    {
        try
        {
            string status = await Task.Run(() => service.Prepare(snapshot));
            if (ReferenceEquals(service, _backgroundProfiles)) Model.BackgroundProfileStatus = status;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (ReferenceEquals(service, _backgroundProfiles))
                Model.BackgroundProfileStatus = "Background preparation needs attention. " + error.Message;
        }
        finally
        {
            _backgroundApplying = false;
            Model.IsBusy = false;
            if (_backgroundRequested) RequestBackgroundPreparation();
        }
    }
}
