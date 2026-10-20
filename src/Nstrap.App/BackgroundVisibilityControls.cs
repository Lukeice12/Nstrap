using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Nstrap.App.Services;

namespace Nstrap.App;

public partial class MainWindow
{
    private readonly string _hiddenWindowProperty = "Nstrap.Hidden." + Guid.NewGuid().ToString("N");
    private readonly List<HiddenWindow> _hiddenWindows = [];
    private nint _previousForeground;
    private bool _mainHasBeenShown, _explicitExitRequested, _closeAfterDrain, _closingRequested, _sessionEnding;
    public bool IsHiddenToBackground { get; private set; } = true;
    private bool IsExitPending => _closingRequested || _closeAfterDrain || _sessionEnding || _experimentalClosing
        || ((App)Application.Current).ShutdownRequested;
    private sealed record HiddenWindow(nint Handle, nint Owner, uint Thread);

    internal void RevealFromLaunch()
    {
        if (IsExitPending) return;
        RevealWindows();
    }

    private void ToggleBackgroundVisibility()
    {
        if (IsExitPending) return;
        if (IsHiddenToBackground) RevealWindows();
        else HideWindows();
    }

    private bool HideWindows()
    {
        if (IsHiddenToBackground) return true;
        if (!_showHideRegistered || _globalHotkeys is not { IsRunning: true })
        {
            Model.BackgroundError = "Set a working show/hide shortcut before hiding N strap.";
            return false;
        }
        EndShortcutCapture();
        uint thread = GetCurrentWindowThread();
        var windows = new List<HiddenWindow>();
        EnumThreadWindows(thread, (handle, _) =>
        {
            if (IsWindowVisible(handle)) windows.Add(new(handle, GetWindow(handle, 4), thread));
            return true;
        }, 0);
        // A property belongs to this HWND lifetime. Reused numeric handles do
        // not inherit it and are never shown when the original window closes.
        foreach (var window in windows)
        {
            if (SetProp(window.Handle, _hiddenWindowProperty, 1)) continue;
            foreach (var marked in windows) RemoveProp(marked.Handle, _hiddenWindowProperty);
            Model.BackgroundError = "Windows could not hide every app window. Try again after closing any open dialog.";
            return false;
        }
        _previousForeground = GetForegroundWindow();
        _hiddenWindows.Clear();
        _hiddenWindows.AddRange(windows);
        IsHiddenToBackground = true;
        // Native visibility preserves WPF ShowDialog's nested loop and drafts.
        // It also covers common file dialogs owned by this UI thread.
        foreach (var window in windows) ShowWindow(window.Handle, 0);
        return true;
    }

    private void RevealWindows()
    {
        if (!_mainHasBeenShown)
        {
            _mainHasBeenShown = true;
            IsHiddenToBackground = false;
            Show();
            Activate();
            return;
        }
        var handles = new HashSet<nint>();
        foreach (var window in _hiddenWindows)
        {
            uint thread = GetWindowThreadProcessId(window.Handle, out uint process);
            if (thread != window.Thread || process != (uint)Environment.ProcessId
                || GetWindow(window.Handle, 4) != window.Owner
                || GetProp(window.Handle, _hiddenWindowProperty) != 1) continue;
            RemoveProp(window.Handle, _hiddenWindowProperty);
            ShowWindow(window.Handle, IsIconic(window.Handle) ? 9 : 8);
            handles.Add(window.Handle);
        }
        _hiddenWindows.Clear();
        IsHiddenToBackground = false;
        nint main = new WindowInteropHelper(this).Handle;
        if (main != 0)
        {
            ShowWindow(main, IsIconic(main) ? 9 : 8);
            handles.Add(main);
        }
        nint activate = handles.Contains(_previousForeground) ? _previousForeground : main;
        if (activate != 0) SetForegroundWindow(activate);
    }

    private void RefreshWindowsStartupStatus()
    {
        try
        {
            bool registered = StartupRegistration.IsEnabled;
            Model.WindowsStartupStatus = registered
                ? "Registered for this Windows account. Windows Startup apps can also disable it."
                : Model.StartWithWindows ? "The startup entry is missing. Turn this option off and on to register it again." : "Off.";
        }
        catch (Exception error) { Model.WindowsStartupStatus = "Startup status unavailable: " + error.Message; }
    }

    private async void BackgroundPreferenceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string tag } box || Model.IsBusy) return;
        bool enabled = box.IsChecked == true;
        bool silent = tag == "Silent" ? enabled : Model.SilentModeEnabled;
        bool startup = tag == "Startup" ? enabled : Model.StartWithWindows;
        bool minimise = tag == "Minimise" ? enabled : Model.MinimiseOnClose;
        if (!silent) startup = false;
        Model.IsBusy = true;
        try
        {
            if (tag == "Startup" && enabled && !silent)
                throw new InvalidOperationException("Enable Silent mode before starting with Windows.");
            if (enabled)
            {
                InitializeShowHideHotkey();
                if (!_showHideRegistered || _globalHotkeys is not { IsRunning: true })
                    throw new InvalidOperationException("Record and save a working show/hide shortcut first.");
            }
            using var registration = tag == "Startup" || startup != Model.StartWithWindows
                ? StartupRegistration.SetEnabled(startup) : null;
            Model.SaveBackgroundPreferences(silent, startup, minimise);
            registration?.Commit();
            if (!silent) await StopBackgroundProfilePreparationAsync();
            Model.BackgroundError = "";
            RefreshWindowsStartupStatus();
        }
        catch (Exception error) { Model.BackgroundError = error.Message; }
        finally
        {
            Model.IsBusy = false;
            box.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
        }
        if (Model.SilentModeEnabled) StartBackgroundProfilePreparation();
        if (tag == "Silent" && enabled && Model.SilentModeEnabled) HideWindows();
    }

    private void ExitAppClick(object sender, RoutedEventArgs e)
    {
        _explicitExitRequested = true;
        Close();
    }

    private async Task CloseOrHideAsync(CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (_updateHandoff || _closeAfterDrain || _sessionEnding || app.ShutdownRequested) return;
        e.Cancel = true;
        if (!_explicitExitRequested && Model.MinimiseOnClose) { HideWindows(); return; }
        if (_closingRequested) return;
        if (Model.IsBusy && _startupInitialization is not { IsCompleted: false } && !_backgroundApplying)
        {
            _explicitExitRequested = false;
            Model.Success("An operation is still in progress.", "Wait for it to finish before exiting N strap.");
            return;
        }
        _closingRequested = true;
        try
        {
            // Minimise never reaches this path. A real close cancels GitHub
            // work and an outgoing automatic-update helper before draining.
            _startupCancellation.Cancel();
            await StopBackgroundProfilePreparationAsync();
            if (_startupInitialization is { IsCompleted: false }) await _startupInitialization;
            if (_sessionEnding || app.ShutdownRequested) return;
            if (Model.IsBusy)
            {
                Model.Success("An operation is still in progress.", "Wait for it to finish before exiting N strap.");
                return;
            }
            try { Model.Save(); }
            catch (Exception error)
            {
                Model.Error(error);
                // Background and session-ending paths never open a save dialog.
                if (IsHiddenToBackground || MessageDialog.Show(this,
                    $"Your latest changes could not be saved.\n\n{error.Message}\n\nExit anyway?", "Unsaved changes",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            }
            _closeAfterDrain = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        finally
        {
            _closingRequested = false;
            _explicitExitRequested = false;
            if (!_closeAfterDrain && !_sessionEnding && !app.ShutdownRequested && !_experimentalClosing)
            {
                if (Model.SilentModeEnabled) StartBackgroundProfilePreparation();
                // A save failure can leave the window open after startup was
                // cancelled while Nuke was paused for automatic update.
                if (!Model.IsBusy) await Busy(InitializeNukeHotkeyAsync);
            }
        }
    }

    internal void PrepareForSessionEnd()
    {
        _sessionEnding = true;
        _startupCancellation.Cancel();
        StopBackgroundProfilePreparation();
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetCurrentWindowThread();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint thread, EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)]
    private static extern nint RemoveProp(nint window, string name);
}
