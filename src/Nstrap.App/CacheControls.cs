using System.Globalization;
using System.Windows;
using Nstrap.App.Services;
using Nstrap.Core;

namespace Nstrap.App;

public sealed partial class MainViewModel
{
    private string _cacheStatus = "No cleanup has run.";
    public string CacheStatus { get => _cacheStatus; set => Set(ref _cacheStatus, value); }
}

public partial class MainWindow
{
    private async void CleanCacheClick(object sender, RoutedEventArgs e) => await Busy(async () =>
    {
        Model.CacheStatus = "Checking for closed Roblox sessions and old cache files…";
        try
        {
            var result = await Task.Run(() => new RobloxCacheCleaner().Clean(OfficialClient.IsRobloxRunningForCurrentUser));
            string removed = $"{result.DeletedFiles:N0} file{(result.DeletedFiles == 1 ? "" : "s")} removed · {FormatCacheBytes(result.DeletedBytes)} of cached data";
            string counts = $"{result.ExaminedEntries:N0} entries checked · {result.SkippedEntries:N0} skipped";
            if (!string.IsNullOrWhiteSpace(result.FailureMessage))
            {
                Model.CacheStatus = $"{removed}. {counts}. Cleanup stopped. {result.FailureMessage}";
                Model.HasError = true;
                Model.Status = "Cache cleanup needs attention.";
                Model.StatusDetail = Model.CacheStatus;
                return;
            }
            string stop = result.StoppedBecauseRobloxRunning
                ? "Cleanup stopped. Close all of your Player and Studio sessions before trying again."
                : result.Cancelled ? "Cleanup was cancelled."
                : result.LimitReached ? "The cleanup limit was reached. Some cache files may remain."
                : !result.CacheExists ? "No Roblox cache directory was found."
                : "Cleanup finished. The next Roblox load may redownload removed cache files.";
            Model.CacheStatus = $"{removed}. {counts}. {stop}";
            Model.Success(result.StoppedBecauseRobloxRunning ? "Cache cleanup paused." : "Cache cleanup complete.", Model.CacheStatus);
        }
        catch (Exception ex)
        {
            Model.CacheStatus = $"Cache cleanup needs attention. {ex.Message}";
            throw;
        }
    });

    private static string FormatCacheBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
