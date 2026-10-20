using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Nstrap.Core;

namespace Nstrap.App.Services;

public enum EmergencyHotkey { KillSwitch, Nuke, ShowHide }

/// <summary>
/// Owns registrations and their message queue independently of WPF's dispatcher.
/// Configuration is staged while the old registrations remain reserved, then
/// committed only after persistence succeeds.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const uint HotkeyMessage = 0x0312, CommandMessage = 0x8001, TimerMessage = 0x0113;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Work> _work = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _configurationLock = new();
    private readonly Action<EmergencyHotkey> _activate;
    private readonly Action<Exception> _failed;
    private Dictionary<int, Registration> _current = [];
    private Dictionary<int, Registration>? _staged;
    private readonly HashSet<int> _releaseKeys = [0x10, 0x11, 0x12];
    private Action<HotkeyBinding>? _capture;
    private uint _threadId;
    private nuint _releaseTimer;
    private int _nextId;
    private bool _releaseGate = true, _stopping;
    private volatile bool _stopped;
    public bool IsRunning => !_stopped;

    private sealed record Registration(EmergencyHotkey Action, HotkeyBinding Binding);
    private sealed class Work(Action action)
    {
        public Action Action { get; } = action;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Cancelled;
    }

    public GlobalHotkeys(Action<EmergencyHotkey> activate, Action<Exception> failed)
    {
        _activate = activate;
        _failed = failed;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "Nstrap global shortcuts" };
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    public void Apply(HotkeyBinding? killSwitch, HotkeyBinding? nuke, HotkeyBinding? showHide, Action persist)
    {
        killSwitch?.Validate();
        nuke?.Validate();
        showHide?.Validate();
        var bindings = new[] { killSwitch, nuke, showHide }.Where(binding => binding is not null).ToArray();
        if (bindings.Distinct().Count() != bindings.Length)
            throw new InvalidOperationException("Choose a different shortcut for each action.");
        lock (_configurationLock)
        {
            Invoke(() => Stage(killSwitch, nuke, showHide));
            try { persist(); }
            catch
            {
                Invoke(Rollback);
                throw;
            }
            Invoke(Commit);
        }
    }

    public void Capture(Action<HotkeyBinding>? captured) => Invoke(() =>
    {
        _capture = captured;
        GateUntilKeysReleased();
    });

    private void Stage(HotkeyBinding? killSwitch, HotkeyBinding? nuke, HotkeyBinding? showHide)
    {
        if (_staged is not null) throw new InvalidOperationException("A shortcut change is already in progress.");
        _staged = [];
        try
        {
            Reserve(EmergencyHotkey.KillSwitch, killSwitch);
            Reserve(EmergencyHotkey.Nuke, nuke);
            Reserve(EmergencyHotkey.ShowHide, showHide);
        }
        catch { Rollback(); throw; }
    }

    private void Reserve(EmergencyHotkey action, HotkeyBinding? binding)
    {
        if (binding is null) return;
        var existing = _current.FirstOrDefault(pair => pair.Value.Binding == binding);
        if (existing.Value is not null)
        {
            _staged!.Add(existing.Key, new(action, binding));
            return;
        }
        int id = NextId();
        if (!RegisterHotKey(0, id, binding.Modifiers | 0x4000, binding.VirtualKey))
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(error == 1409
                ? "That shortcut is already in use by Windows or another app. Choose another."
                : $"Windows could not register that shortcut: {new Win32Exception(error).Message}");
        }
        _staged!.Add(id, new(action, binding));
    }

    private int NextId()
    {
        do { _nextId = _nextId >= 0xBFFF ? 1 : _nextId + 1; }
        while (_current.ContainsKey(_nextId) || (_staged?.ContainsKey(_nextId) ?? false));
        return _nextId;
    }

    private void Commit()
    {
        foreach (var pair in _current)
        {
            _releaseKeys.Add((int)pair.Value.Binding.VirtualKey);
            if (!_staged!.ContainsKey(pair.Key)) UnregisterHotKey(0, pair.Key);
        }
        _current = _staged!;
        _staged = null;
        foreach (var entry in _current.Values) _releaseKeys.Add((int)entry.Binding.VirtualKey);
        GateUntilKeysReleased();
    }

    private void Rollback()
    {
        if (_staged is null) return;
        foreach (int id in _staged.Keys)
            if (!_current.ContainsKey(id)) UnregisterHotKey(0, id);
        _staged = null;
        GateUntilKeysReleased();
    }

    private void GateUntilKeysReleased()
    {
        _releaseGate = true;
        if (_releaseTimer != 0) return;
        _releaseTimer = SetTimer(0, 0, 50, 0);
        if (_releaseTimer == 0)
            ReportFailure(new Win32Exception(Marshal.GetLastWin32Error(), "Shortcuts are paused because Windows could not start the key-release check. Turn them off and on to retry."));
    }

    private void ReportFailure(Exception error)
    {
        try { _failed(error); }
        catch { /* A closing UI must not crash this native message thread. */ }
    }

    private void Invoke(Action action)
    {
        if (_stopped) throw new InvalidOperationException("Global shortcuts are unavailable. Restart the app.");
        var work = new Work(action);
        _work.Enqueue(work);
        if (_stopped)
            work.Completion.TrySetException(new InvalidOperationException("Global shortcuts have stopped."));
        if (!PostThreadMessage(_threadId, CommandMessage, 0, 0))
        {
            Interlocked.Exchange(ref work.Cancelled, 1);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not update global shortcuts.");
        }
        work.Completion.Task.GetAwaiter().GetResult();
    }

    private void MessageLoop()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            // Create the thread's message queue before another thread may post work.
            PeekMessage(out _, 0, 0, 0, 0);
            _ready.TrySetResult();
            while (!_stopping)
            {
                int result = GetMessage(out var message, 0, 0, 0);
                if (result == 0) break;
                if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The global shortcut message loop stopped.");
                if (message.Id == CommandMessage)
                {
                    while (_work.TryDequeue(out var work))
                    {
                        if (Volatile.Read(ref work.Cancelled) != 0) continue;
                        try { work.Action(); work.Completion.TrySetResult(); }
                        catch (Exception error) { work.Completion.TrySetException(error); }
                    }
                }
                else if (message.Id == TimerMessage && _releaseGate
                    && _releaseKeys.All(key => (GetAsyncKeyState(key) & 0x8000) == 0))
                {
                    _releaseGate = false;
                    KillTimer(0, _releaseTimer);
                    _releaseTimer = 0;
                    _releaseKeys.Clear();
                    _releaseKeys.UnionWith([0x10, 0x11, 0x12]);
                    foreach (var entry in _current.Values) _releaseKeys.Add((int)entry.Binding.VirtualKey);
                }
                else if (message.Id == HotkeyMessage && _staged is null
                    && _current.TryGetValue(unchecked((int)message.WParam), out var registration))
                {
                    uint keys = unchecked((uint)message.LParam.ToInt64());
                    if ((keys & 0xFFFF) != registration.Binding.Modifiers
                        || (keys >> 16) != registration.Binding.VirtualKey) continue;
                    try
                    {
                        if (_capture is not null) _capture(registration.Binding);
                        else if (!_releaseGate) _activate(registration.Action);
                    }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
        }
        catch (Exception error)
        {
            if (!_ready.TrySetException(error)) ReportFailure(error);
        }
        finally
        {
            _stopped = true;
            foreach (int id in _current.Keys.Concat(_staged?.Keys ?? Enumerable.Empty<int>()).Distinct())
                UnregisterHotKey(0, id);
            if (_releaseTimer != 0) KillTimer(0, _releaseTimer);
            while (_work.TryDequeue(out var work))
                work.Completion.TrySetException(new InvalidOperationException("Global shortcuts have stopped."));
        }
    }

    public void Dispose()
    {
        lock (_configurationLock)
        {
            if (_stopped) return;
            try { Invoke(() => { _capture = null; _stopping = true; }); }
            catch (Win32Exception) { }
            catch (InvalidOperationException) when (_stopped) { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out Message message, nint window, uint first, uint last);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, nint window, uint first, uint last, uint remove);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint window, nuint id);
}
