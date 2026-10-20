using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nstrap.App.Services;
using Nstrap.Core;

namespace Nstrap.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private void InitializeFpsControls()
    {
        _fpsTimer.Tick += FpsTimerTick;
        Closed += (_, _) => _fpsTimer.Stop();
    }

    private void FpsTimerTick(object? sender, EventArgs e)
    {
        if (!Model.FpsChangePending) { _fpsTimer.Stop(); return; }
        if (Model.IsBusy) return;
        try
        {
            // Waiting must not repeatedly disable the UI or replace other feedback.
            if (OfficialClient.IsPlayerRunningForCurrentUser()) return;
        }
        catch (Exception ex)
        {
            _fpsTimer.Stop();
            Model.FpsStatus = $"FPS change needs attention. {ex.Message}";
            Model.Error(ex);
            return;
        }
        // Stop before starting work. Busy catches failures; an attempt only schedules
        // another tick when Player is still running, never after a conflict or IO error.
        _fpsTimer.Stop();
        _ = Busy(async () => { await ReconcileFpsAsync(); });
    }

    private async void UnlockFpsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkbox) return;
        bool enabled = checkbox.IsChecked == true;
        await Busy(async () =>
        {
            Model.QueueFpsChange(enabled);
            await ReconcileFpsAsync();
        });
    }

    private async void RetryFpsClick(object sender, RoutedEventArgs e)
    {
        await Busy(async () => { await ReconcileFpsAsync(); });
    }

    private async Task<string> ReconcileFpsAsync()
    {
        _fpsTimer.Stop();
        try
        {
            if (!Model.FpsChangePending)
            {
                if (!Model.UnlockFps) return "";
                Model.QueueFpsChange(Model.UnlockFps);
            }
            bool enabled = Model.UnlockFps;
            var result = await Task.Run(() => Model.FpsSettings.Change(enabled, OfficialClient.IsPlayerRunningForCurrentUser));
            if (result.Deferred)
            {
                Model.FpsStatus = "Pending — close your account's Roblox Player sessions to apply; keep N strap open.";
                _fpsTimer.Start();
                Model.Success("FPS change queued.", Model.FpsStatus);
                return Model.FpsStatus;
            }
            Model.CompleteFpsChange();
            Model.FpsStatus = enabled ? "Cap set to 9,999 FPS for Player's next start."
                : result.KeptExternal ? "Off. Kept the cap changed outside N strap."
                : result.Cap is not null ? "Previous cap restored for Player's next start."
                : "Off. Player's existing cap is unchanged.";
            Model.Success("Player FPS setting updated.", Model.FpsStatus);
        }
        catch (Exception ex)
        {
            // Keep the durable pending preference and stop automatic retries. This
            // optional setting must not prevent ordinary Roblox launch.
            Model.FpsStatus = $"FPS change needs attention. {ex.Message}";
            Model.Error(ex);
        }
        return Model.FpsStatus;
    }
}
