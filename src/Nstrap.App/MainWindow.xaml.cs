using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Nstrap.App.Services;
using Nstrap.Core;

namespace Nstrap.App;

public partial class MainWindow : Window
{
    public MainViewModel Model { get; }
    public MainWindow(MainViewModel model)
    {
        Model = model;
        InitializeComponent();
        DataContext = model;
        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Closing += OnClosing;
        Loaded += OnLoaded;
    }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Model is not null) Model.Page = int.Parse(tag);
    }
    private void GoFlags(object sender, RoutedEventArgs e) => FlagsNav.IsChecked = true;
    private void RefreshClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        Model.RefreshInstallations();
        Model.Success("Installations refreshed.", Model.InstallationSummary);
    });
    private RobloxInstallation RequireInstallation() =>
        Model.SelectedInstallation ?? throw new InvalidOperationException("Choose an installed Roblox client, or install Player from Overview first.");
    private async Task ApplySelectedAsync()
    {
        var installation = RequireInstallation();
        OfficialClient.EnsureClosed(installation.Kind);
        FlagValidator.Validate(Model.SelectedProfile.Flags);
        Model.Save();
        var result = await Task.Run(() => Model.Applier.Apply(installation, Model.SelectedProfile));
        Model.Success($"Applied {result.ManagedCount} flag{(result.ManagedCount == 1 ? "" : "s")} to Roblox {installation.Kind}.",
            $"Profile: {Model.SelectedProfile.Name}. Restart the client to load these settings.");
    }
    private async void ApplyClick(object sender, RoutedEventArgs e) => await Busy(ApplySelectedAsync);
    private async void LaunchClick(object sender, RoutedEventArgs e) => await Busy(async () =>
    {
        var client = RequireInstallation();
        if (Model.ApplyBeforeLaunch) await ApplySelectedAsync();
        Model.Success($"Verifying Roblox {client.Kind}…", "Windows is checking its signature.");
        await Task.Run(() => OfficialClient.Launch(client));
        Model.Success($"Roblox {client.Kind} started.", "Your session continues in the official Roblox client.");
    });
    private async void InstallClick(object sender, RoutedEventArgs e) => await Busy(async () =>
    {
        var progress = new Progress<string>(message => Model.Success(message, "Installer source: setup.rbxcdn.com"));
        await OfficialClient.InstallPlayerAsync(Model.DataDirectory, progress);
        Model.RefreshInstallations();
        Model.Success("The official installer has finished.", "Refresh installations if Roblox is still completing setup.");
    });
    private async void RestoreClick(object sender, RoutedEventArgs e) => await Busy(async () =>
    {
        var installation = RequireInstallation();
        OfficialClient.EnsureClosed(installation.Kind);
        await Task.Run(() => Model.Applier.Restore(installation));
        Model.Success($"Restored managed settings for Roblox {installation.Kind}.", "Original values returned. Other settings were preserved.");
    });
    private void ChooseFolderClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new OpenFolderDialog { Title = "Choose a Roblox version folder or its Versions folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var selectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dialog.FolderName));
        var found = InstallationDiscovery.Find([dialog.FolderName]).Where(i =>
            string.Equals(i.DirectoryPath, selectedRoot, StringComparison.OrdinalIgnoreCase)
            || i.DirectoryPath.StartsWith(selectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (found.Length == 0) throw new InvalidOperationException("No RobloxPlayerBeta.exe or RobloxStudioBeta.exe was found in this folder or its version folders.");
        if (!Model.State.CustomRoots.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) Model.State.CustomRoots.Add(dialog.FolderName);
        Model.RefreshInstallations();
        Model.SelectedInstallation = Model.Installations.FirstOrDefault(i => i.ExecutablePath == found[0].ExecutablePath);
        Model.Save();
        Model.Success("Installation folder added.", dialog.FolderName);
    });
    private void NewProfileClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        string? name = PromptWindow.Ask(this, "New profile", "Give this profile a name.", "My profile");
        if (name is null) return;
        var profile = new FlagProfile { Name = name };
        Model.Profiles.Add(profile); Model.SelectedProfile = profile; Model.Save();
        Model.Success($"Created “{name}”.", "Add flags or choose a preset to begin.");
    });
    private void DuplicateProfileClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        string? name = PromptWindow.Ask(this, "Duplicate profile", "Name your new copy.", Model.SelectedProfile.Name + " copy");
        if (name is null) return;
        var copy = Model.SelectedProfile.Copy(name);
        Model.Profiles.Add(copy); Model.SelectedProfile = copy; Model.Save();
        Model.Success($"Created “{name}”.");
    });
    private void RenameProfileClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        string? name = PromptWindow.Ask(this, "Rename profile", "Choose a name that is easy to recognize.", Model.SelectedProfile.Name);
        if (name is null) return;
        Model.SelectedProfile.Name = name; Model.Save(); Model.Success("Profile renamed.");
    });
    private void DeleteProfileClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (Model.Profiles.Count <= 1) throw new InvalidOperationException("Keep at least one profile. Create another profile before deleting this one.");
        var previous = Model.SelectedProfile;
        if (MessageBox.Show(this, $"Delete the profile “{previous.Name}”? Applied settings stay in place until you apply another profile or restore them.", "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Model.SelectedProfile = Model.Profiles.First(p => p != previous);
        Model.Profiles.Remove(previous); Model.Save(); Model.Success("Profile deleted.");
    });
    private void AddFlagClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        string? name = PromptWindow.Ask(this, "Add a flag", "Enter a full flag name, such as FFlagExample.", "");
        if (name is null) return;
        if (Model.SelectedProfile.Flags.Any(f => f.Name == name)) throw new InvalidDataException("That flag is already in this profile.");
        string value = FlagValidator.GetTypeHint(name) == "Boolean" ? "true" : FlagValidator.GetTypeHint(name) == "String" ? "" : "0";
        FlagValidator.Value(name, value);
        var flag = new FlagEntry { Name = name, Value = value };
        Model.SelectedProfile.Flags.Add(flag);
        Model.Search = "";
        FlagsGrid.SelectedItem = flag; FlagsGrid.ScrollIntoView(flag);
        Model.Save(); Model.Success("Flag added.", "Edit its value in the table. The checkbox controls whether it is applied.");
    });
    private void RemoveFlagClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (FlagsGrid.SelectedItem is not FlagEntry flag) throw new InvalidOperationException("Select a flag row to remove it.");
        Model.SelectedProfile.Flags.Remove(flag); Model.Save();
        Model.Success("Flag removed from this profile.", "Apply the profile to restore that flag's previous value in the client.");
    });
    private void PresetClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (sender is not Button { Tag: FlagPreset preset }) return;
        foreach (var item in preset.Flags)
        {
            var existing = Model.SelectedProfile.Flags.FirstOrDefault(f => f.Name == item.Name);
            if (existing is null) Model.SelectedProfile.Flags.Add(item.Copy());
            else { existing.Value = item.Value; existing.Enabled = true; }
        }
        Model.Save();
        Model.Success($"Added “{preset.Name}” to {Model.SelectedProfile.Name}.", "Review it in FastFlags, then apply the profile when ready.");
    });
    private void JsonClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var editor = new JsonEditorWindow(Model.SelectedProfile) { Owner = this };
        if (editor.ShowDialog() == true && editor.ImportedFlags is { } flags)
        {
            // Removing individually preserves observable subscriptions.
            while (Model.SelectedProfile.Flags.Count > 0) Model.SelectedProfile.Flags.RemoveAt(Model.SelectedProfile.Flags.Count - 1);
            foreach (var flag in flags) Model.SelectedProfile.Flags.Add(flag);
            Model.Save();
            Model.Success($"Imported {flags.Count} flags.", "All imported entries are enabled. The previous profile entries were replaced.");
        }
    });
    private void BackupsClick(object sender, RoutedEventArgs e) => Run(() => OpenFolder(Model.Applier.BackupDirectory));
    private void DataFolderClick(object sender, RoutedEventArgs e) => Run(() => OpenFolder(Model.DataDirectory));
    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    private void DocsClick(object sender, RoutedEventArgs e) => Run(() => OfficialClient.OpenWeb(FlagCatalog.SourceUrl));
    private void SupportClick(object sender, RoutedEventArgs e) => Run(() => OfficialClient.OpenWeb("https://en.help.roblox.com/hc/en-us/articles/204473560-How-to-Install-and-Play-Roblox"));
    private void GetStudioClick(object sender, RoutedEventArgs e) => Run(() => OfficialClient.OpenWeb("https://create.roblox.com/docs/studio/setup"));
    private void Run(Action action) { try { action(); } catch (Exception ex) { Model.Error(ex); } }
    private async Task Busy(Func<Task> action)
    {
        if (Model.IsBusy) return;
        Model.IsBusy = true;
        try { await action(); }
        catch (Exception ex) { Model.Error(ex); }
        finally { Model.IsBusy = false; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Model.IsBusy) { e.Cancel = true; Model.Success("An operation is still in progress.", "Wait for it to finish before closing N strap."); return; }
        try { Model.Save(); }
        catch (Exception ex)
        {
            e.Cancel = true;
            Model.Error(ex);
            if (MessageBox.Show(this, $"Your latest changes could not be saved.\n\n{ex.Message}\n\nClose anyway?", "Unsaved changes", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes) e.Cancel = false;
        }
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App { SmokeScreenshot: { } path }) return;
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(path);
            if (Model.SelectedProfile.Flags.Count == 0)
                foreach (var entry in FlagCatalog.Presets[0].Flags) Model.SelectedProfile.Flags.Add(entry.Copy());
            Model.Save();
            FlagsNav.IsChecked = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(Path.ChangeExtension(path, ".flags.png"));
            PresetsNav.IsChecked = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(Path.ChangeExtension(path, ".presets.png"));
            RecoveryNav.IsChecked = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(Path.ChangeExtension(path, ".recovery.png"));
            SettingsNav.IsChecked = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(Path.ChangeExtension(path, ".settings.png"));
            var editor = new JsonEditorWindow(Model.SelectedProfile) { Owner = this };
            editor.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            CaptureElement((FrameworkElement)editor.Content, Path.ChangeExtension(path, ".json.png"));
            editor.Close();
            FlagsNav.IsChecked = true;
            Width = MinWidth;
            Height = MinHeight;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(Path.ChangeExtension(path, ".compact.png"));
            var actualInstaller = Path.Combine(Environment.CurrentDirectory, ".tools", "downloads", "RobloxPlayerInstaller.exe");
            if (File.Exists(actualInstaller))
            {
                await Task.Run(() =>
                {
                    OfficialClient.VerifyPublisher(actualInstaller);
                    var tampered = Path.Combine(Model.DataDirectory, "tampered-signature-fixture.exe");
                    try
                    {
                        File.Copy(actualInstaller, tampered, true);
                        using (var stream = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                            stream.Position = 1024;
                            int original = stream.ReadByte();
                            stream.Position = 1024;
                            stream.WriteByte((byte)(original ^ 1));
                        }
                        bool rejected = false;
                        try { OfficialClient.VerifyPublisher(tampered); } catch (InvalidDataException) { rejected = true; }
                        if (!rejected) throw new InvalidOperationException("Tampered executable passed signature verification.");
                    }
                    finally { if (File.Exists(tampered)) File.Delete(tampered); }
                    bool unsignedRejected = false;
                    try { OfficialClient.VerifyPublisher(Environment.ProcessPath!); } catch (InvalidDataException) { unsignedRejected = true; }
                    if (!unsignedRejected) throw new InvalidOperationException("Unsigned N strap executable passed Roblox publisher verification.");
                });
                File.WriteAllText(path + ".signature.txt", "PASS: official installer Windows trust and exact Roblox publisher; tampered installer rejected; unsigned executable rejected. No executable launched.");
            }
            File.WriteAllText(path + ".result.txt", "PASS: WPF startup; overview, flags, presets, recovery, settings, JSON editor, minimum size rendered; isolated profile persisted.");
            Close();
        }
        catch (Exception ex) { File.WriteAllText(path + ".error.txt", ex.ToString()); Application.Current.Shutdown(1); }
    }
    private void Capture(string path)
    {
        UpdateLayout();
        var content = (FrameworkElement)Content;
        CaptureElement(content, path);
    }
    private static void CaptureElement(FrameworkElement content, string path)
    {
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private void EnableDarkTitleBar()
    {
        int enabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
