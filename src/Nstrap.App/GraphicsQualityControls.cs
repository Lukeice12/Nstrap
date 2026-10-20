using System.Windows;
using System.Windows.Threading;
using Nstrap.App.Services;

namespace Nstrap.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _qualityTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private void InitializeGraphicsQualityControls()
    {
        _qualityTimer.Tick += QualityTimerTick;
        Closed += (_, _) => _qualityTimer.Stop();
    }
    private void QualityTimerTick(object? sender, EventArgs e)
    {
        if (!Model.GraphicsQualityChangePending) { _qualityTimer.Stop(); return; }
        if (Model.IsBusy) return;
        try
        {
            if (OfficialClient.IsPlayerRunningForCurrentUser()) return;
        }
        catch (Exception ex)
        {
            _qualityTimer.Stop();
            Model.GraphicsQualityStatus = $"Graphics quality needs attention. {ex.Message}";
            Model.Error(ex);
            return;
        }
        _qualityTimer.Stop();
        _ = Busy(async () => { await ReconcileGraphicsQualityAsync(); });
    }

    private async void ApplyGraphicsQualityClick(object sender, RoutedEventArgs e) => await Busy(async () =>
    {
        Model.QueueGraphicsQualityChange(true);
        await ReconcileGraphicsQualityAsync();
    });
    private async void RestoreGraphicsQualityClick(object sender, RoutedEventArgs e) => await Busy(async () =>
    {
        Model.QueueGraphicsQualityChange(false);
        await ReconcileGraphicsQualityAsync();
    });
    private async void RetryGraphicsQualityClick(object sender, RoutedEventArgs e) =>
        await Busy(async () => { await ReconcileGraphicsQualityAsync(); });

    private async Task<string> ReconcileGraphicsQualityAsync()
    {
        _qualityTimer.Stop();
        if (!Model.GraphicsQualityChangePending) return "";
        try
        {
            bool managed = Model.State.GraphicsQualityManaged;
            int level = Model.State.GraphicsQuality;
            var result = await Task.Run(() => Model.FpsSettings.ChangeGraphicsQuality(
                managed, level, OfficialClient.IsPlayerRunningForCurrentUser));
            if (result.Deferred)
            {
                Model.GraphicsQualityStatus = "Pending — close your account's Roblox Player sessions to apply; keep N strap open.";
                _qualityTimer.Start();
                Model.Success("Graphics quality change queued.", Model.GraphicsQualityStatus);
                return Model.GraphicsQualityStatus;
            }
            Model.CompleteGraphicsQualityChange();
            Model.GraphicsQualityStatus = managed
                ? $"{(level == 0 ? "Automatic" : $"Level {level}")} set for Player's next start."
                : result.KeptExternal ? "Kept the graphics quality changed outside N strap."
                : result.Level is not null ? "Previous graphics quality restored for Player's next start."
                : "Player's existing graphics quality is unchanged.";
            Model.Success("Player graphics quality updated.", Model.GraphicsQualityStatus);
        }
        catch (Exception ex)
        {
            // Preserve the requested operation for explicit retry. A malformed or
            // externally changed settings file must never block ordinary launch.
            Model.GraphicsQualityStatus = $"Graphics quality needs attention. {ex.Message}";
            Model.Error(ex);
        }
        return Model.GraphicsQualityStatus;
    }
}
