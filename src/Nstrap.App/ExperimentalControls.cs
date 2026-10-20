using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nstrap.App.Services;
using Nstrap.Core;

namespace Nstrap.App;

public partial class MainWindow
{
    private GlobalHotkeys? _globalHotkeys;
    private EmergencyHotkey? _capturingShortcut;
    private bool _killSwitchRegistered, _nukeRegistered, _showHideRegistered, _experimentalClosing;
    private bool CanRegisterEmergencyHotkeys =>
        ((App)Application.Current).UpdateRestart is null || _startupAcknowledged;

    private void RequireAcknowledgedStartup()
    {
        if (!CanRegisterEmergencyHotkeys)
            throw new InvalidOperationException("The update restart has not been acknowledged. Resolve the update error and reopen the app before enabling shortcuts.");
    }

    private void InitializeExperimentalControls()
    {
        PreviewKeyDown += CaptureShortcutKeyDown;
        Deactivated += (_, _) => EndShortcutCapture();
        Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Model.Page) && Model.Page != 7) EndShortcutCapture();
        };
        Closed += (_, _) =>
        {
            _experimentalClosing = true;
            _globalHotkeys?.Dispose();
            _globalHotkeys = null;
            EmergencyActions.DisarmNuke();
        };
    }

    private GlobalHotkeys EnsureGlobalHotkeys() => _globalHotkeys ??= new(action =>
    {
        switch (action)
        {
            case EmergencyHotkey.KillSwitch: EmergencyActions.KillSwitch(); break;
            case EmergencyHotkey.Nuke: EmergencyActions.Nuke(); break;
            case EmergencyHotkey.ShowHide:
                if (!_experimentalClosing && !Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(ToggleBackgroundVisibility));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }, error =>
    {
        if (_experimentalClosing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Model.KillSwitchError = "Global shortcut error: " + error.Message;
            Model.NukeError = "Global shortcut error: " + error.Message;
            Model.ShowHideError = "Global shortcut error: " + error.Message;
        }));
    });

    private void InitializeKillSwitchHotkey()
    {
        if (!Model.KillSwitchEnabled) return;
        try
        {
            RequireAcknowledgedStartup();
            EnsureGlobalHotkeys().Apply(Model.State.KillSwitchHotkey, null,
                _showHideRegistered ? Model.State.ShowHideHotkey : null, () => { });
            _killSwitchRegistered = true;
            Model.KillSwitchStatus = "On. Works while this app is running.";
        }
        catch (Exception error)
        {
            Model.KillSwitchStatus = "Unavailable.";
            Model.KillSwitchError = error.Message + " Turn off and on to retry.";
            ReleaseUnusedHotkeys();
        }
    }

    private async Task InitializeNukeHotkeyAsync()
    {
        if (!Model.NukeEnabled || _nukeRegistered || IsExitPending) return;
        try
        {
            RequireAcknowledgedStartup();
            await EmergencyActions.PrepareNukeAsync();
            if (IsExitPending) { EmergencyActions.DisarmNuke(); return; }
            EnsureGlobalHotkeys().Apply(_killSwitchRegistered ? Model.State.KillSwitchHotkey : null,
                Model.State.NukeHotkey, _showHideRegistered ? Model.State.ShowHideHotkey : null, () => { });
            _nukeRegistered = true;
            Model.NukeStatus = "On. The shortcut runs immediately, with no confirmation.";
            Model.NukeError = "";
        }
        catch (Exception error)
        {
            _nukeRegistered = false;
            EmergencyActions.DisarmNuke();
            Model.NukeStatus = "Unavailable.";
            Model.NukeError = error.Message + " Turn off and on to retry.";
            ReleaseUnusedHotkeys();
        }
    }

    private void InitializeShowHideHotkey()
    {
        if (Model.State.ShowHideHotkey is null || _showHideRegistered) return;
        try
        {
            EnsureGlobalHotkeys().Apply(_killSwitchRegistered ? Model.State.KillSwitchHotkey : null,
                _nukeRegistered ? Model.State.NukeHotkey : null, Model.State.ShowHideHotkey, () => { });
            _showHideRegistered = true;
            Model.ShowHideStatus = "Ready. Opens or hides all N strap windows.";
            Model.ShowHideError = "";
        }
        catch (Exception error)
        {
            Model.ShowHideStatus = "Unavailable. Open the executable again to reveal N strap.";
            Model.ShowHideError = error.Message;
            ReleaseUnusedHotkeys();
        }
    }

    private void RecordShortcutClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } button
            || !Enum.TryParse<EmergencyHotkey>(tag, out var action)) return;
        if (_capturingShortcut == action) { EndShortcutCapture(); return; }
        EndShortcutCapture();
        _capturingShortcut = action;
        Model.SetShortcutCapture(action);
        SetShortcutError(action, "");
        try
        {
            _globalHotkeys?.Capture(binding => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_capturingShortcut == action) AcceptCapturedShortcut(action, binding);
            })));
            button.Focus();
        }
        catch (Exception error) { EndShortcutCapture(); SetShortcutError(action, error.Message); }
    }

    private void CaptureShortcutKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingShortcut is not { } action) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { EndShortcutCapture(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        uint nativeModifiers = ((modifiers & ModifierKeys.Alt) != 0 ? 1u : 0)
            | ((modifiers & ModifierKeys.Control) != 0 ? 2u : 0)
            | ((modifiers & ModifierKeys.Shift) != 0 ? 4u : 0)
            | ((modifiers & ModifierKeys.Windows) != 0 ? 8u : 0);
        AcceptCapturedShortcut(action, new(nativeModifiers, (uint)KeyInterop.VirtualKeyFromKey(key)));
    }

    private void AcceptCapturedShortcut(EmergencyHotkey action, HotkeyBinding binding)
    {
        try
        {
            Model.SetShortcutDraft(action, binding);
            EndShortcutCapture();
        }
        catch (Exception error) { SetShortcutError(action, error.Message); }
    }

    private void EndShortcutCapture()
    {
        if (_capturingShortcut is null) return;
        _capturingShortcut = null;
        Model.SetShortcutCapture(null);
        try { _globalHotkeys?.Capture(null); }
        catch (Exception error) { Model.KillSwitchError = Model.NukeError = Model.ShowHideError = error.Message; }
    }

    private async void SaveShortcutClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<EmergencyHotkey>(tag, out var action)) return;
        bool enabled = action switch
        {
            EmergencyHotkey.KillSwitch => Model.KillSwitchEnabled,
            EmergencyHotkey.Nuke => Model.NukeEnabled,
            EmergencyHotkey.ShowHide => Model.ShortcutDraft(action) is not null,
            _ => false
        };
        await ChangeShortcutAsync(action, enabled, Model.ShortcutDraft(action));
    }

    private async void ClearShortcutClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<EmergencyHotkey>(tag, out var action)) return;
        await ChangeShortcutAsync(action, false, null);
        if (Model.SavedShortcut(action) is null)
            Model.SetShortcutDraft(action, null);
    }

    private async void EmergencyEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string tag } box || !Enum.TryParse<EmergencyHotkey>(tag, out var action)) return;
        var binding = Model.SavedShortcut(action);
        await ChangeShortcutAsync(action, box.IsChecked == true, binding);
        box.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
    }

    private async Task ChangeShortcutAsync(EmergencyHotkey action, bool enabled, HotkeyBinding? binding)
    {
        if (Model.IsBusy) return;
        EndShortcutCapture();
        Model.IsBusy = true;
        bool previouslyPrepared = EmergencyActions.IsNukePrepared;
        try
        {
            if (_globalHotkeys is { IsRunning: false })
            {
                _globalHotkeys.Dispose();
                _globalHotkeys = null;
                _killSwitchRegistered = _nukeRegistered = _showHideRegistered = false;
            }
            binding?.Validate();
            if (enabled && action != EmergencyHotkey.ShowHide) RequireAcknowledgedStartup();
            if (enabled && binding is null) throw new InvalidOperationException("Record and save a shortcut first.");
            Model.ValidateDistinctShortcut(action, binding);
            if (action == EmergencyHotkey.Nuke && enabled)
            {
                if (!Model.NukeAcknowledged) throw new InvalidOperationException("Accept the removal scope before enabling Nuke.");
                Model.NukeStatus = "Preparing removal.";
                await EmergencyActions.PrepareNukeAsync();
            }
            bool kill = action == EmergencyHotkey.KillSwitch ? enabled : _killSwitchRegistered;
            bool nuke = action == EmergencyHotkey.Nuke ? enabled : _nukeRegistered;
            bool showHide = action == EmergencyHotkey.ShowHide ? enabled : _showHideRegistered;
            if (kill || nuke || showHide) EnsureGlobalHotkeys();
            Model.SaveEmergencyPreference(_globalHotkeys, action, enabled, binding, kill, nuke, showHide);
            _killSwitchRegistered = kill;
            _nukeRegistered = nuke;
            _showHideRegistered = showHide;
            if (!Model.NukeEnabled) EmergencyActions.DisarmNuke();
            Model.KillSwitchStatus = kill ? "On. Works while this app is running." : Model.KillSwitchEnabled ? "Unavailable." : "Off.";
            Model.NukeStatus = nuke ? "On. The shortcut runs immediately, with no confirmation." : Model.NukeEnabled ? "Unavailable." : "Off.";
            Model.ShowHideStatus = showHide ? "Ready. Opens or hides all N strap windows." : "Not set.";
            SetShortcutError(action, "");
            ReleaseUnusedHotkeys();
        }
        catch (Exception error)
        {
            if (!previouslyPrepared && !_nukeRegistered) EmergencyActions.DisarmNuke();
            if (action == EmergencyHotkey.Nuke)
                Model.NukeStatus = _nukeRegistered ? "On. The shortcut runs immediately, with no confirmation." : Model.NukeEnabled ? "Unavailable." : "Off.";
            SetShortcutError(action, error.Message);
            ReleaseUnusedHotkeys();
        }
        finally { Model.IsBusy = false; }
    }

    private void SetShortcutError(EmergencyHotkey action, string message)
    {
        if (action == EmergencyHotkey.KillSwitch) Model.KillSwitchError = message;
        else if (action == EmergencyHotkey.Nuke) Model.NukeError = message;
        else if (action == EmergencyHotkey.ShowHide) Model.ShowHideError = message;
    }

    private void ReleaseUnusedHotkeys()
    {
        if (_killSwitchRegistered || _nukeRegistered || _showHideRegistered) return;
        _globalHotkeys?.Dispose();
        _globalHotkeys = null;
    }

    private async Task SuspendNukeForRestartAsync()
    {
        EndShortcutCapture();
        if (_nukeRegistered)
        {
            _globalHotkeys!.Apply(_killSwitchRegistered ? Model.State.KillSwitchHotkey : null, null,
                _showHideRegistered ? Model.State.ShowHideHotkey : null, () => { });
            _nukeRegistered = false;
            Model.NukeStatus = "Paused while the app restarts.";
        }
        await EmergencyActions.DisarmNukeForRestartAsync();
        ReleaseUnusedHotkeys();
    }

    private Task ResumeNukeAfterFailedRestartAsync() => InitializeNukeHotkeyAsync();
}
