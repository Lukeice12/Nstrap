using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Nstrap.App.Services;

namespace Nstrap.App;

public sealed partial class MainViewModel
{
    private string _updateStatus = "Check for a newer version of N strap.";
    private string _availableUpdate = "Not checked";
    private bool _hasAppUpdate;
    public bool AutoUpdate => State.AutoUpdate;
    public string UpdateStatus { get => _updateStatus; set => Set(ref _updateStatus, value); }
    public string AvailableUpdate { get => _availableUpdate; set => Set(ref _availableUpdate, value); }
    public bool HasAppUpdate { get => _hasAppUpdate; set => Set(ref _hasAppUpdate, value); }
    public void SetAutoUpdate(bool enabled)
    {
        bool previous = State.AutoUpdate;
        State.AutoUpdate = enabled;
        try { Save(); }
        catch { State.AutoUpdate = previous; throw; }
        finally { Changed(nameof(AutoUpdate)); }
    }
}

public partial class MainWindow
{
    private AppRelease? _availableRelease;
    private bool _updateHandoff;
    private bool _startupAcknowledged;

    private async Task<bool> StartupUpdatesAsync(CancellationToken cancellationToken)
    {
        var app = (App)Application.Current;
        // A completed restart never immediately checks again. Acknowledgement is
        // written only after the data and window were constructed successfully.
        if (app.UpdateRestart is { } restart)
        {
            try
            {
                UpdateHandoff.AcknowledgeStartup(restart, app.UpdateRolledBack);
                _startupAcknowledged = true;
                if (UpdateHandoff.IsIdentityRepair(restart))
                {
                    AppearanceNav.IsChecked = true;
                    if (app.UpdateRolledBack)
                        Model.Error(new IOException("The process-name repair did not complete. The original executable was restored; apply the name again to retry."));
                    else Model.Success("Task Manager name updated.", $"Running as {Model.CurrentExecutableName}.");
                }
                else
                {
                    Model.UpdateStatus = UpdateHandoff.ReadStatus().Message;
                    if (app.UpdateRolledBack) UpdatesNav.IsChecked = true;
                }
            }
            catch (Exception ex) { UpdateFailure(ex); }
            return false;
        }
        Model.IsBusy = true;
        try
        {
            await Task.Run(UpdateHandoff.CleanCompletedJobs);
            if (IsExitPending) return false;
            var status = UpdateHandoff.ReadStatus();
            if (status.Message.Length != 0) Model.UpdateStatus = status.Message;
            if (!Model.AutoUpdate) return false;
            await CheckForUpdateAsync(cancellationToken);
            if (IsExitPending) return false;
            if (_availableRelease is not null)
            {
                if (status.AttemptedVersion == _availableRelease.Version.ToString(3))
                {
                    Model.UpdateStatus = status.Message + " Automatic retry is paused for this version. Choose Install and restart to retry.";
                }
                else
                {
                    UpdatesNav.IsChecked = true;
                    await InstallUpdateAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { UpdateFailure(ex); }
        finally { Model.IsBusy = false; }
        return _updateHandoff;
    }

    private async Task CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        Model.HasAppUpdate = false;
        _availableRelease = null;
        Model.UpdateStatus = "Checking GitHub releases...";
        try
        {
            _availableRelease = await GitHubUpdates.CheckAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Model.HasAppUpdate = _availableRelease is not null;
            Model.AvailableUpdate = _availableRelease is null ? "Up to date" :
                $"{_availableRelease.Version}{(_availableRelease.Preview ? " (preview)" : "")}";
            Model.UpdateStatus = _availableRelease is null ? "You have the newest supported release." :
                $"N strap {_availableRelease.Version} is available. Install and restart when you are ready.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Model.AvailableUpdate = "Check unavailable";
            UpdateFailure(ex);
        }
    }

    private async Task InstallUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_availableRelease is null) return;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Model.Save();
            await StopBackgroundProfilePreparationAsync();
            cancellationToken.ThrowIfCancellationRequested();
            await SuspendNukeForRestartAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var progress = new Progress<string>(message => { if (!IsExitPending) Model.UpdateStatus = message; });
            var ticket = await Task.Run(() => UpdateHandoff.PrepareAsync(_availableRelease, progress, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExitPending) return;
            Model.Save();
            Model.UpdateStatus = "Preparing to restart...";
            bool startHidden = IsHiddenToBackground;
            await Task.Run(() => UpdateHandoff.StartHelperAsync(ticket, startHidden, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _updateHandoff = true;
            ((App)Application.Current).RequestShutdown();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            UpdateFailure(ex);
            if (!IsExitPending)
            {
                await ResumeNukeAfterFailedRestartAsync();
                if (Model.SilentModeEnabled) StartBackgroundProfilePreparation();
            }
        }
    }

    private void UpdateFailure(Exception ex)
    {
        Model.UpdateStatus = "Update not completed. " + UpdateHandoff.FriendlyError(ex);
        Model.Success("Update unavailable.", "See Updates for details.");
    }

    private void AutoUpdateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;
        Run(() =>
        {
            Model.SetAutoUpdate(box.IsChecked == true);
            Model.UpdateStatus = Model.AutoUpdate
                ? "Automatic updates are enabled for the next time you open N strap."
                : "Automatic updates are off. You can still check and install manually.";
        });
    }
    private async void CheckUpdateClick(object sender, RoutedEventArgs e) => await Busy(() => CheckForUpdateAsync());
    private async void InstallUpdateClick(object sender, RoutedEventArgs e) => await Busy(() => InstallUpdateAsync());
    private void OpenReleasesClick(object sender, RoutedEventArgs e) => Run(() =>
        Process.Start(new ProcessStartInfo(GitHubUpdates.ReleasesPage) { UseShellExecute = true }));
}
