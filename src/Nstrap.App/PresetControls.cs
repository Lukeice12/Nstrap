using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nstrap.Core;

namespace Nstrap.App;

public partial class MainWindow
{
    private bool _refreshingPresetControls = true, _presetRefreshPending;

    private void InitializePresetControls()
    {
        Model.PresetFlagsChanged += PresetFlagsChanged;
        Closed += (_, _) => Model.PresetFlagsChanged -= PresetFlagsChanged;
        Loaded += (_, _) => RefreshPresetControls();
        RefreshPresetControls();
    }

    private void PresetFlagsChanged(object? sender, EventArgs e)
    {
        if (_presetRefreshPending) return;
        _presetRefreshPending = true;
        // A JSON import can replace thousands of entries in one operation.
        // Read the completed profile once instead of rebuilding controls per row.
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _presetRefreshPending = false;
            RefreshPresetControls();
        }));
    }

    private void RefreshPresetControls()
    {
        _refreshingPresetControls = true;
        try
        {
            foreach (var combo in new[] { MeshDetailCombo, MsaaCombo, RendererCombo, TextureQualityCombo, GrassCombo })
                if (combo.Tag is FlagSetting setting)
                    combo.SelectedItem = setting.ReadChoice(Model.SelectedProfile.Flags);

            var dpi = FlagCatalog.NativeDpi.ReadChoice(Model.SelectedProfile.Flags);
            NativeDpiToggle.IsChecked = dpi.IsCustom ? null : dpi != FlagCatalog.NativeDpi.Automatic;
            CustomDpiNotice.Visibility = dpi.IsCustom ? Visibility.Visible : Visibility.Collapsed;

            var quality = FlagCatalog.RenderingQuality.ReadChoice(Model.SelectedProfile.Flags);
            bool automatic = quality == FlagCatalog.RenderingQuality.Automatic;
            RenderingQualityToggle.IsChecked = quality.IsCustom ? null : !automatic;
            RenderingQualityOptions.Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
            CustomRenderingQualityNotice.Visibility = quality.IsCustom ? Visibility.Visible : Visibility.Collapsed;
            RenderingQualityCombo.SelectedItem = automatic ? FlagCatalog.RenderingQuality.Choices[1] : quality;
        }
        finally { _refreshingPresetControls = false; }
    }

    private void PresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPresetControls || sender is not ComboBox
            { Tag: FlagSetting setting, SelectedItem: FlagChoice { IsCustom: false } choice }) return;
        if (choice == setting.ReadChoice(Model.SelectedProfile.Flags)) return;
        RunPresetChange(setting, choice);
    }

    private void NativeDpiClick(object sender, RoutedEventArgs e) =>
        RunPresetChange(FlagCatalog.NativeDpi,
            NativeDpiToggle.IsChecked == true ? FlagCatalog.NativeDpi.Choices[1] : FlagCatalog.NativeDpi.Automatic);

    private void RenderingQualityClick(object sender, RoutedEventArgs e) =>
        RunPresetChange(FlagCatalog.RenderingQuality,
            RenderingQualityToggle.IsChecked == true ? FlagCatalog.RenderingQuality.Choices[1] : FlagCatalog.RenderingQuality.Automatic);

    private void RunPresetChange(FlagSetting setting, FlagChoice choice)
    {
        try
        {
            Run(() =>
            {
                Model.SetPresetChoice(setting, choice);
                Model.Success($"{setting.Name}: {choice.Label}.",
                    $"Saved to {Model.SelectedProfile.Name}. Apply the profile from FastFlags, or launch with Apply before launch enabled.");
            });
        }
        finally
        {
            // A failed save rolls back the model; immediately restore the control too.
            RefreshPresetControls();
        }
    }
}
