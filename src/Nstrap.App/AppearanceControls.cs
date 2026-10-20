using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Nstrap.App.Services;

namespace Nstrap.App;

public partial class MainWindow
{
    private void ThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box || box.SelectedItem is not string theme) return;
        Run(() => Model.SetTheme(theme));
        box.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateTarget();
    }

    private void SaveDisplayNameClick(object sender, RoutedEventArgs e) => Run(Model.SaveDisplayName);

    private void PickIconClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose a program icon",
            Filter = "PNG or Windows icon (*.png;*.ico)|*.png;*.ico",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) == true) Model.SetCustomIcon(picker.FileName);
    });

    private void ResetIconClick(object sender, RoutedEventArgs e) => Run(() => Model.SetCustomIcon(null));

    private async void RenameProcessClick(object sender, RoutedEventArgs e) => await Busy(async () =>
    {
        string name = AppIdentity.NormalizeProcessName(Model.ProcessNameDraft);
        if (AppIdentity.NameIsCurrent(name)) return;
        try
        {
            Model.Save();
            await SuspendNukeForRestartAsync();
            Model.Success("Preparing the new program name…", "The program will restart when the renamed copy is ready.");
            await Task.Run(() => AppIdentity.StartRenameAsync(name));
            _updateHandoff = true;
            Application.Current.Shutdown();
        }
        catch
        {
            await ResumeNukeAfterFailedRestartAsync();
            throw;
        }
    });
}
