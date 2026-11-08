using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Nstrap.App.Services;
using Nstrap.Core;

namespace Nstrap.App;

public sealed partial class MainViewModel
{
    internal ObservableCollection<RuntimeLiveFlagState> LiveFlagRows { get; } = [];
    private string _liveFlagSearch = "", _liveFlagSummary = "Refresh to read the selected Player's live values.";
    public ICollectionView LiveFlagsView => CollectionViewSource.GetDefaultView(LiveFlagRows);
    public string LiveFlagSearch { get => _liveFlagSearch; set { if (Set(ref _liveFlagSearch, value)) LiveFlagsView.Refresh(); } }
    public string LiveFlagSummary { get => _liveFlagSummary; internal set => Set(ref _liveFlagSummary, value); }
}

public partial class MainWindow
{
    private const int LiveFlagsPage = 8;
    private RuntimeFlagSession? _liveFlagSession;
    private void InitializeLiveFlagControls()
    {
        Model.LiveFlagsView.Filter = item => item is RuntimeLiveFlagState flag &&
            (string.IsNullOrWhiteSpace(Model.LiveFlagSearch) || flag.Name.Contains(Model.LiveFlagSearch, StringComparison.OrdinalIgnoreCase)
                || flag.Status.Contains(Model.LiveFlagSearch, StringComparison.OrdinalIgnoreCase));
        Model.PropertyChanged += LiveFlagContextChanged;
        Closed += (_, _) => Model.PropertyChanged -= LiveFlagContextChanged;
    }
    private async void LiveFlagContextChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(Model.Page) or nameof(Model.SelectedProfile) or nameof(Model.SelectedInstallation))) return;
        _liveFlagSession = null;
        if (Model.Page == LiveFlagsPage && !Model.IsBusy && !IsExitPending) await Busy(() => RefreshLiveFlagsAsync());
    }
    private async void RefreshLiveFlagsClick(object sender, RoutedEventArgs e) => await Busy(() => RefreshLiveFlagsAsync());
    private async Task RefreshLiveFlagsAsync()
    {
        _liveFlagSession = null;
        Model.LiveFlagRows.Clear();
        Model.LiveFlagSummary = "Select a Player installation to read live status.";
        var installation = RequireInstallation();
        var profile = Model.SelectedProfile.Copy(Model.SelectedProfile.Name);
        Model.LiveFlagSummary = "Reading live values and committed ownership…";
        var snapshot = await Task.Run(() => _runtimeFlags.ReadLiveFlags(installation, profile));
        foreach (var flag in snapshot.Flags) Model.LiveFlagRows.Add(flag);
        _liveFlagSession = snapshot.Session;
        Model.LiveFlagSummary = snapshot.Summary;
    }
    private async void LiveFlagToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox toggle || toggle.DataContext is not RuntimeLiveFlagState row) return;
        bool turnOn = toggle.IsChecked == true;
        var session = _liveFlagSession;
        try
        {
            if (!row.CanToggle || session is null || Model.IsBusy || IsExitPending) return;
            await Busy(async () =>
            {
                // Flush existing profile edits without changing any saved value or
                // Enabled state. The live operation uses only this selected entry.
                Model.Save();
                var flag = Model.SelectedProfile.Flags.Single(item => item.Name == row.Name).Copy();
                var installation = RequireInstallation();
                _runtimeReport = await Task.Run(() => _runtimeFlags.SetLiveFlag(installation, flag, turnOn, session, _startupCancellation.Token));
                await RefreshLiveFlagsAsync();
                Model.Success(_runtimeReport.Summary, "Current values were refreshed. Some changes need rejoining to reset game state.");
                Model.HasError = _runtimeReport.RequiresAttention;
            });
        }
        finally
        {
            // An unsuccessful/ignored click must not leave an optimistic check mark.
            toggle.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
        }
    }
}
