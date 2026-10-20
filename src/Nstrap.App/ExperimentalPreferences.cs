using System.Windows.Input;
using Nstrap.App.Services;
using Nstrap.Core;

namespace Nstrap.App;

public sealed partial class MainViewModel
{
    private HotkeyBinding? _killSwitchDraft, _nukeDraft;
    private bool _nukeAcknowledged;
    private string _killSwitchStatus = "Off.", _nukeStatus = "Off.";
    private string _killSwitchError = "", _nukeError = "";
    private bool _capturingKillSwitch, _capturingNuke;

    public bool KillSwitchEnabled => State.KillSwitchEnabled;
    public bool NukeEnabled => State.NukeEnabled;
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
        _nukeAcknowledged = State.NukeScopeAcknowledged;
        if (KillSwitchEnabled) _killSwitchStatus = "Waiting to register.";
        if (NukeEnabled) _nukeStatus = "Preparing removal.";
    }

    public HotkeyBinding? ShortcutDraft(EmergencyHotkey action) =>
        action == EmergencyHotkey.KillSwitch ? _killSwitchDraft : _nukeDraft;

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
        else
        {
            _nukeDraft = binding;
            NukeError = "";
            Changed(nameof(NukeShortcut));
            Changed(nameof(CanSaveNukeShortcut));
        }
    }

    public void SetShortcutCapture(EmergencyHotkey? action)
    {
        _capturingKillSwitch = action == EmergencyHotkey.KillSwitch;
        _capturingNuke = action == EmergencyHotkey.Nuke;
        Changed(nameof(KillSwitchCaptureLabel));
        Changed(nameof(NukeCaptureLabel));
    }

    public void SaveEmergencyPreference(GlobalHotkeys? hotkeys, EmergencyHotkey action,
        bool enabled, HotkeyBinding? binding, bool registerKillSwitch, bool registerNuke)
    {
        binding?.Validate();
        if (enabled && binding is null) throw new InvalidOperationException("Record and save a shortcut first.");
        if (action == EmergencyHotkey.Nuke && enabled && !NukeAcknowledged)
            throw new InvalidOperationException("Accept the removal scope before enabling Nuke.");
        var other = action == EmergencyHotkey.KillSwitch ? State.NukeHotkey : State.KillSwitchHotkey;
        if (binding is not null && binding == other)
            throw new InvalidOperationException("Choose different shortcuts for Kill switch and Nuke.");

        var previousKill = State.KillSwitchHotkey;
        var previousNuke = State.NukeHotkey;
        bool previousKillEnabled = State.KillSwitchEnabled, previousNukeEnabled = State.NukeEnabled;
        bool previousAcknowledged = State.NukeScopeAcknowledged;
        if (action == EmergencyHotkey.KillSwitch)
        {
            State.KillSwitchEnabled = enabled;
            State.KillSwitchHotkey = binding;
        }
        else
        {
            State.NukeEnabled = enabled;
            State.NukeHotkey = binding;
            State.NukeScopeAcknowledged = NukeAcknowledged;
        }
        try
        {
            if (hotkeys is null) Save();
            else hotkeys.Apply(registerKillSwitch ? State.KillSwitchHotkey : null,
                registerNuke ? State.NukeHotkey : null, Save);
        }
        catch
        {
            State.KillSwitchHotkey = previousKill;
            State.NukeHotkey = previousNuke;
            State.KillSwitchEnabled = previousKillEnabled;
            State.NukeEnabled = previousNukeEnabled;
            State.NukeScopeAcknowledged = previousAcknowledged;
            throw;
        }
        finally
        {
            foreach (string name in new[] { nameof(KillSwitchEnabled), nameof(NukeEnabled),
                nameof(CanChangeNukeAcknowledgement), nameof(CanSaveKillSwitchShortcut), nameof(CanSaveNukeShortcut) })
                Changed(name);
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
