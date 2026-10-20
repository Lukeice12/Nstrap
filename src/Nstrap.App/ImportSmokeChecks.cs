using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nstrap.Core;

namespace Nstrap.App;

public partial class MainWindow
{
    private async Task RunImportSmokeChecksAsync(string capturePath)
    {
        var originalProfile = Model.SelectedProfile;
        var originalInstallation = Model.SelectedInstallation;
        var originalPicker = _pickImportFile;
        var profile = new FlagProfile { Name = "Import check" };
        profile.Flags.Add(new() { Name = "FFlagSmokeToggle", Value = "true", Enabled = false });
        profile.Flags.Add(new() { Name = "FStringSmokeKeep", Value = "retained" });
        var directory = Path.Combine(Model.DataDirectory, "import-smoke-client");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "RobloxPlayerBeta.exe");
        File.WriteAllBytes(executable, []);
        var installation = new RobloxInstallation(directory, executable, ClientKind.Player, DateTime.UtcNow);
        Model.Profiles.Add(profile);
        Model.SelectedProfile = profile;
        Model.Installations.Add(installation);
        Model.SelectedInstallation = installation;
        var fixture = Path.Combine(Model.DataDirectory, "import-smoke.txt");
        File.WriteAllText(fixture, """{"FFlagSmokeToggle":"False","FLogSmokeLevel":"7","FStringSmokeLiteral":"null"}""");
        int sampleCount = 0;
        try
        {
            FlagsNav.IsChecked = true;
            _pickImportFile = _ => fixture;
            ImportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await _activeImport;
            RequireSmoke(!Model.HasError && Model.Status.Contains("applied 4", StringComparison.Ordinal), "Import button did not apply the merged fixture.");
            RequireSmoke(Model.FlagCounter == "4 flags · 4 enabled", "Footer did not reflect the merged flags.");
            RequireSmoke(profile.Flags.Single(f => f.Name == "FStringSmokeKeep").Value == "retained", "Import removed an unrelated flag.");
            var settings = FlagValidator.ReadObject(File.ReadAllBytes(installation.ConfigPath));
            RequireSmoke(settings["FStringSmokeLiteral"].GetString() == "null", "String null was converted to JSON null.");
            RequireSmoke(settings["FLogSmokeLevel"].GetInt32() == 7 && !settings["FFlagSmokeToggle"].GetBoolean(), "Quoted primitives were not applied correctly.");

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(Path.ChangeExtension(capturePath, ".import-result.png"));
            var textBox = FindVisual<TextBox>(FlagsGrid) ?? throw new InvalidOperationException("No flag text editor was rendered.");
            var data = new DataObject(DataFormats.FileDrop, new[] { fixture });
            var drag = MakeDrag(data, textBox, DragDrop.PreviewDragOverEvent);
            textBox.RaiseEvent(drag);
            RequireSmoke(drag.Handled && drag.Effects == DragDropEffects.Copy && DropCue.Visibility == Visibility.Visible, "Window did not accept file drag over a TextBox.");
            Capture(Path.ChangeExtension(capturePath, ".drop.png"));
            File.WriteAllText(fixture, """{"FFlagSmokeToggle":"True","DFIntSmokeAdded":"9"}""");
            var drop = MakeDrag(data, textBox, DragDrop.PreviewDropEvent);
            textBox.RaiseEvent(drop);
            await _activeImport;
            RequireSmoke(drop.Handled && !Model.HasError && profile.Flags.Count == 5, "Routed file drop failed.");
            RequireSmoke(FlagValidator.ReadObject(File.ReadAllBytes(installation.ConfigPath))["DFIntSmokeAdded"].GetInt32() == 9, "Dropped file was not applied.");
            RequireSmoke(DropCue.Visibility == Visibility.Collapsed, "Drop cue remained after import.");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            // The successful merge replaced the rows. Send the next event through a current
            // editor, because the previous TextBox is now detached from the routed event tree.
            textBox = FindVisual<TextBox>(FlagsGrid) ?? throw new InvalidOperationException("Merged flag editors were not rendered.");

            // The complete file must validate before profile or settings mutation.
            var savedProfile = File.ReadAllBytes(Model.Store.StatePath);
            var savedSettings = File.ReadAllBytes(installation.ConfigPath);
            var profileJson = JsonSerializer.Serialize(profile);
            File.WriteAllText(fixture, """{"FFlagSmokeToggle":"False","DFIntBroken":"invalid"}""");
            textBox.RaiseEvent(MakeDrag(data, textBox, DragDrop.PreviewDropEvent));
            await _activeImport;
            RequireSmoke(Model.HasError, "Malformed drop did not report its validation error.");
            RequireSmoke(profileJson == JsonSerializer.Serialize(profile), "Malformed drop changed profile state.");
            RequireSmoke(savedProfile.SequenceEqual(File.ReadAllBytes(Model.Store.StatePath)), "Malformed drop changed saved profile.");
            RequireSmoke(savedSettings.SequenceEqual(File.ReadAllBytes(installation.ConfigPath)), "Malformed drop changed client settings.");

            Model.IsBusy = true;
            var busyDrag = MakeDrag(data, textBox, DragDrop.PreviewDragOverEvent);
            textBox.RaiseEvent(busyDrag);
            RequireSmoke(busyDrag.Handled && busyDrag.Effects == DragDropEffects.None, "Busy window accepted a file.");
            Model.IsBusy = false;
            var multiple = new DataObject(DataFormats.FileDrop, new[] { fixture, fixture });
            var multipleDrag = MakeDrag(multiple, textBox, DragDrop.PreviewDragOverEvent);
            textBox.RaiseEvent(multipleDrag);
            RequireSmoke(multipleDrag.Effects == DragDropEffects.None, "Multiple files were accepted.");

            File.WriteAllText(fixture, """{"FIntSmokeNoClient":"12"}""");
            Model.SelectedInstallation = null;
            ImportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await _activeImport;
            RequireSmoke(Model.HasError && Model.Status.Contains("Not applied.", StringComparison.Ordinal), "Missing client reported a false apply success.");
            RequireSmoke(Model.Store.Load().Profiles.Single(p => p.Id == profile.Id).Flags.Any(f => f.Name == "FIntSmokeNoClient"), "Valid import was not saved without a client.");
            RequireSmoke(savedSettings.SequenceEqual(File.ReadAllBytes(installation.ConfigPath)), "Missing-client import changed settings.");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(Path.ChangeExtension(capturePath, ".import-error.png"));
            Model.SelectedInstallation = installation;

            // Losing the selected installation must not recreate its version directory.
            File.Delete(executable);
            File.WriteAllText(fixture, """{"FIntSmokeStaleClient":"13"}""");
            ImportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await _activeImport;
            RequireSmoke(Model.HasError && Model.StatusDetail.Contains("Refresh installations", StringComparison.Ordinal), "Stale installation did not report a refresh instruction.");
            RequireSmoke(savedSettings.SequenceEqual(File.ReadAllBytes(installation.ConfigPath)), "Stale-client import changed settings.");
            File.WriteAllBytes(executable, []);

            // Explicit import/apply uses profile values and records external edits for restore.
            var edited = FlagValidator.ReadObject(savedSettings);
            edited["FLogSmokeLevel"] = JsonSerializer.SerializeToElement(99);
            AtomicFile.WriteJson(installation.ConfigPath, edited);
            File.WriteAllText(fixture, """{"FIntSmokeConflict":"14"}""");
            ImportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await _activeImport;
            RequireSmoke(!Model.HasError && Model.Status.Contains("applied", StringComparison.Ordinal), "Import failed to reconcile an external managed edit.");
            RequireSmoke(FlagValidator.ReadObject(File.ReadAllBytes(installation.ConfigPath))["FLogSmokeLevel"].GetInt32() == 7, "Import did not use the selected profile value.");
            Model.Applier.Restore(installation);
            RequireSmoke(FlagValidator.ReadObject(File.ReadAllBytes(installation.ConfigPath))["FLogSmokeLevel"].GetInt32() == 99, "Restore lost the external baseline adopted by import.");

            if (Application.Current is App { SmokeImportFile: { } samplePath })
            {
                while (profile.Flags.Count > 0) profile.Flags.RemoveAt(profile.Flags.Count - 1);
                _pickImportFile = _ => samplePath;
                ImportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await _activeImport;
                RequireSmoke(!Model.HasError, "Supplied sample did not auto-apply to the fixture.");
                var expected = FlagFileImport.Read(samplePath, []);
                sampleCount = expected.ImportedCount;
                RequireSmoke(profile.Flags.Count == sampleCount, "Supplied sample count changed.");
                var applied = FlagValidator.ReadObject(File.ReadAllBytes(installation.ConfigPath));
                foreach (var (name, value) in FlagValidator.Validate(expected.MergedFlags))
                    RequireSmoke(JsonElement.DeepEquals(value, applied[name]), "Supplied sample value changed.");
                Model.Applier.Restore(installation);
            }

            profile.Flags[0].Enabled = false;
            RequireSmoke(Model.FlagCounter.EndsWith($"{profile.Flags.Count - 1} enabled", StringComparison.Ordinal), "Footer did not update after a toggle.");
            File.WriteAllText(capturePath + ".import.txt",
                $"PASS: Import button event; routed drag/drop over a flag TextBox; cue; busy/multiple rejection; validated merge; auto-apply; malformed file no mutation; missing/stale client retain import without false success; external edits reconciled and restored as the new baseline; counter updates. Supplied sample: {sampleCount} flags. All writes targeted isolated fixture files.");
        }
        finally
        {
            Model.IsBusy = false;
            DropCue.Visibility = Visibility.Collapsed;
            _pickImportFile = originalPicker;
            Model.SelectedProfile = originalProfile;
            Model.Profiles.Remove(profile);
            Model.SelectedInstallation = originalInstallation;
            Model.Installations.Remove(installation);
            Model.Search = "";
            Model.Success("");
            Model.Save();
        }
    }

    private async Task CapturePromptSmokeAsync(string path)
    {
        var prompt = new PromptWindow(this, "Add a flag", "Enter a full flag name, such as FFlagExample.", "FFlagExample", "Add");
        prompt.Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        RequireVisibleSubmit(prompt);
        CaptureElement((FrameworkElement)prompt.Content, Path.ChangeExtension(path, ".prompt.png"));
        prompt.Close();

        var scaled = new PromptWindow(this, "Add a flag", "Enter a full flag name, such as FFlagExample.", "", "Add");
        ((FrameworkElement)scaled.Content).LayoutTransform = new ScaleTransform(1.5, 1.5);
        scaled.Width = 720;
        scaled.Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        RequireVisibleSubmit(scaled);
        CaptureElement((FrameworkElement)scaled.Content, Path.ChangeExtension(path, ".prompt-scaled.png"));
        scaled.Close();

        var modal = new PromptWindow(this, "New profile", "Give this profile a name.", "Prompt check");
        modal.Loaded += (_, _) => Dispatcher.BeginInvoke(() => modal.SubmitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        RequireSmoke(modal.ShowDialog() == true, "Prompt default action did not accept the value.");
        RequireSmoke(modal.SubmitButton.IsDefault, "Prompt submit is not the Enter/default action.");
        var cancel = new PromptWindow(this, "Rename profile", "Enter a profile name.", "Prompt check");
        cancel.Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
            cancel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(cancel), Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent }));
        RequireSmoke(cancel.ShowDialog() != true, "Escape did not cancel the prompt.");
        File.WriteAllText(path + ".prompt.txt", "PASS: custom dark caption; submit visible at normal and 150% layout scale; modal submit accepted; Enter/default action configured; routed Escape cancels.");
    }

    private static void RequireVisibleSubmit(PromptWindow prompt)
    {
        var bounds = prompt.SubmitButton.TransformToAncestor(prompt).TransformBounds(new Rect(prompt.SubmitButton.RenderSize));
        RequireSmoke(bounds.Bottom <= prompt.ActualHeight + 1 && bounds.Right <= prompt.ActualWidth + 1, "Prompt buttons are clipped.");
        RequireSmoke(prompt.SubmitButton.ActualHeight > 20, "Prompt submit button has no usable height.");
    }
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private static DragEventArgs MakeDrag(IDataObject data, UIElement target, RoutedEvent routedEvent)
    {
        // WPF's DragEventArgs constructor is internal. This smoke-only factory exercises
        // the actual tunnel route, including interception before TextBox class handlers.
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, target, new Point(5, 5)], culture: null)!;
        args.RoutedEvent = routedEvent;
        return args;
    }
    private static void RequireSmoke(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
