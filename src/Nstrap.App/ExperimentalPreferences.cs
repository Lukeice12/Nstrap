using System.Windows.Input;
using Nstrap.App.Services;
using Nstrap.Core;

namespace Nstrap.App;

public sealed partial class MainViewModel
{
    private HotkeyBinding? _killSwitchDraft, _nukeDraft, _showHideDraft;
    private bool _nukeAcknowledged;
    private string _killSwitchStatus = "Off.", _nukeStatus = "Off.";
    private string _killSwitchError = "", _nukeError = "", _showHideError = "", _backgroundError = "";
    private string _showHideStatus = "Not set.", _windowsStartupStatus = "";
    private bool _capturingKillSwitch, _capturingNuke, _capturingShowHide;

    public bool KillSwitchEnabled => State.KillSwitchEnabled;
    public bool NukeEnabled => State.NukeEnabled;
    public bool SilentModeEnabled => State.SilentModeEnabled;
    public bool StartWithWindows => State.StartWithWindows;
    public bool MinimiseOnClose => State.MinimiseOnClose;
    public string ShowHideShortcut => DescribeHotkey(_showHideDraft);
    public string ShowHideCaptureLabel => _capturingShowHide ? "Press shortcut..." : "Record shortcut";
    public string ShowHideStatus { get => _showHideStatus; set => Set(ref _showHideStatus, value); }
    public string WindowsStartupStatus { get => _windowsStartupStatus; set => Set(ref _windowsStartupStatus, value); }
    public string ShowHideError { get => _showHideError; set { Set(ref _showHideError, value); Changed(nameof(HasShowHideError)); } }
    public string BackgroundError { get => _backgroundError; set { Set(ref _backgroundError, value); Changed(nameof(HasBackgroundError)); } }
    public bool HasShowHideError => ShowHideError.Length != 0;
    public bool HasBackgroundError => BackgroundError.Length != 0;
    public bool CanSaveShowHideShortcut => _showHideDraft != State.ShowHideHotkey;
    public string NukeScopeDescription => EmergencyActions.ScopeDescription;
    public string RemovalExecutablePath => Environment.ProcessPath ?? "Current executable";
    public bool NukeAcknowledged { get => _nukeAcknowledged; set => Set(ref _nukeAcknowledged, value); }
    public bool CanChangeNukeAcknowledgement => !NukeEnabled;
    public string KillSwitchShortcut => DescribeHotkey(_killSwitchDraft);
    public string NukeShortcut => DescribeHotkey(_nukeDraft);
    public string KillSwitchCaptureLabel => _capturingKillSwitch ? "Press shortcut..." : "Record shortcut";
    public string NukeCaptureLabel => _capturingNuke ? "Press shortcut..." : "Record shortcut";
    public string KillSwitchStatus { get => _killSwitchStatus; set => Set(ref _killSwitchStatus, value); }
    public string NukeStatus { get => _nukeStatus; set => Set(ref _nukeStatus, value); }
    public string KillSwitchError { get => _killSwitchError; set { Set(ref _killSwitchError, value); Changed(nameof(HasKillSwitchError)); } }
    public string NukeError { get => _nukeError; set { Set(ref _nukeError, value); Changed(nameof(HasNukeError)); } }
    public bool HasKillSwitchError => KillSwitchError.Length != 0;
    public bool HasNukeError => NukeError.Length != 0;
    public bool CanSaveKillSwitchShortcut => _killSwitchDraft != State.KillSwitchHotkey;
    public bool CanSaveNukeShortcut => _nukeDraft != State.NukeHotkey;

    private void InitializeExperimentalPreferences()
    {
        _killSwitchDraft = State.KillSwitchHotkey;
        _nukeDraft = State.NukeHotkey;
        _showHideDraft = State.ShowHideHotkey;
        _nukeAcknowledged = State.NukeScopeAcknowledged;
        if (KillSwitchEnabled) _killSwitchStatus = "Waiting to register.";
        if (NukeEnabled) _nukeStatus = "Preparing removal.";
        else if (State.NukeHotkey is not null && State.NukeScopeVersion != AppState.CurrentNukeScopeVersion)
            _nukeStatus = "Off. Review the updated removal scope before enabling Nuke.";
    }

    public HotkeyBinding? ShortcutDraft(EmergencyHotkey action) => action switch
    {
        EmergencyHotkey.KillSwitch => _killSwitchDraft,
        EmergencyHotkey.Nuke => _nukeDraft,
        EmergencyHotkey.ShowHide => _showHideDraft,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    public HotkeyBinding? SavedShortcut(EmergencyHotkey action) => action switch
    {
        EmergencyHotkey.KillSwitch => State.KillSwitchHotkey,
        EmergencyHotkey.Nuke => State.NukeHotkey,
        EmergencyHotkey.ShowHide => State.ShowHideHotkey,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    public void ValidateDistinctShortcut(EmergencyHotkey action, HotkeyBinding? binding)
    {
        binding?.Validate();
        if (binding is not null && Enum.GetValues<EmergencyHotkey>().Any(other => other != action && SavedShortcut(other) == binding))
            throw new InvalidOperationException("Choose a different shortcut for each action.");
    }

    public void SetShortcutDraft(EmergencyHotkey action, HotkeyBinding? binding)
    {
        binding?.Validate();
        if (action == EmergencyHotkey.KillSwitch)
        {
            _killSwitchDraft = binding;
            KillSwitchError = "";
            Changed(nameof(KillSwitchShortcut));
            Changed(nameof(CanSaveKillSwitchShortcut));
        }
        else if (action == EmergencyHotkey.Nuke)
        {
            _nukeDraft = binding;
            NukeError = "";
            Changed(nameof(NukeShortcut));
            Changed(nameof(CanSaveNukeShortcut));
        }
        else if (action == EmergencyHotkey.ShowHide)
        {
            _showHideDraft = binding;
            ShowHideError = "";
            Changed(nameof(ShowHideShortcut));
            Changed(nameof(CanSaveShowHideShortcut));
        }
        else throw new ArgumentOutOfRangeException(nameof(action));
    }

    public void SetShortcutCapture(EmergencyHotkey? action)
    {
        _capturingKillSwitch = action == EmergencyHotkey.KillSwitch;
        _capturingNuke = action == EmergencyHotkey.Nuke;
        _capturingShowHide = action == EmergencyHotkey.ShowHide;
        Changed(nameof(KillSwitchCaptureLabel));
        Changed(nameof(NukeCaptureLabel));
        Changed(nameof(ShowHideCaptureLabel));
    }

    public void SaveEmergencyPreference(GlobalHotkeys? hotkeys, EmergencyHotkey action,
        bool enabled, HotkeyBinding? binding, bool registerKillSwitch, bool registerNuke, bool registerShowHide)
    {
        binding?.Validate();
        if (enabled && binding is null) throw new InvalidOperationException("Record and save a shortcut first.");
        if (action == EmergencyHotkey.Nuke && enabled && !NukeAcknowledged)
            throw new InvalidOperationException("Accept the removal scope before enabling Nuke.");
        ValidateDistinctShortcut(action, binding);
        if (action == EmergencyHotkey.ShowHide && binding is null && (SilentModeEnabled || StartWithWindows || MinimiseOnClose))
            throw new InvalidOperationException("Turn off Silent mode and Minimise on close before clearing the show/hide shortcut.");

        var previousKill = State.KillSwitchHotkey;
        var previousNuke = State.NukeHotkey;
        var previousShowHide = State.ShowHideHotkey;
        bool previousKillEnabled = State.KillSwitchEnabled, previousNukeEnabled = State.NukeEnabled;
        bool previousAcknowledged = State.NukeScopeAcknowledged;
        int previousScopeVersion = State.NukeScopeVersion;
        if (action == EmergencyHotkey.KillSwitch)
        {
            State.KillSwitchEnabled = enabled;
            State.KillSwitchHotkey = binding;
        }
        else if (action == EmergencyHotkey.Nuke)
        {
            State.NukeEnabled = enabled;
            State.NukeHotkey = binding;
            State.NukeScopeAcknowledged = NukeAcknowledged;
            State.NukeScopeVersion = NukeAcknowledged ? AppState.CurrentNukeScopeVersion : 0;
        }
        else if (action == EmergencyHotkey.ShowHide) State.ShowHideHotkey = binding;
        else throw new ArgumentOutOfRangeException(nameof(action));
        try
        {
            if (hotkeys is null) Save();
            else hotkeys.Apply(registerKillSwitch ? State.KillSwitchHotkey : null,
                registerNuke ? State.NukeHotkey : null, registerShowHide ? State.ShowHideHotkey : null, Save);
        }
        catch
        {
            State.KillSwitchHotkey = previousKill;
            State.NukeHotkey = previousNuke;
            State.ShowHideHotkey = previousShowHide;
            State.KillSwitchEnabled = previousKillEnabled;
            State.NukeEnabled = previousNukeEnabled;
            State.NukeScopeAcknowledged = previousAcknowledged;
            State.NukeScopeVersion = previousScopeVersion;
            throw;
        }
        finally
        {
            foreach (string name in new[] { nameof(KillSwitchEnabled), nameof(NukeEnabled),
                nameof(CanChangeNukeAcknowledgement), nameof(CanSaveKillSwitchShortcut), nameof(CanSaveNukeShortcut), nameof(CanSaveShowHideShortcut) })
                Changed(name);
        }
    }

    public void SaveBackgroundPreferences(bool silent, bool startup, bool minimise)
    {
        bool previousSilent = State.SilentModeEnabled, previousStartup = State.StartWithWindows, previousMinimise = State.MinimiseOnClose;
        State.SilentModeEnabled = silent;
        State.StartWithWindows = startup;
        State.MinimiseOnClose = minimise;
        try { Save(); }
        catch
        {
            State.SilentModeEnabled = previousSilent;
            State.StartWithWindows = previousStartup;
            State.MinimiseOnClose = previousMinimise;
            throw;
        }
        finally
        {
            Changed(nameof(SilentModeEnabled));
            Changed(nameof(StartWithWindows));
            Changed(nameof(MinimiseOnClose));
        }
    }

    private static string DescribeHotkey(HotkeyBinding? binding)
    {
        if (binding is null) return "Not set";
        var parts = new List<string>();
        if ((binding.Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((binding.Modifiers & 1) != 0) parts.Add("Alt");
        if ((binding.Modifiers & 4) != 0) parts.Add("Shift");
        var key = KeyInterop.KeyFromVirtualKey((int)binding.VirtualKey);
        string name = binding.VirtualKey is >= 0x30 and <= 0x39 ? ((char)binding.VirtualKey).ToString() : key switch
        {
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Prior => "Page Up",
            Key.Next => "Page Down",
            _ => key.ToString()
        };
        parts.Add(name);
        return string.Join(" + ", parts);
    }
}
