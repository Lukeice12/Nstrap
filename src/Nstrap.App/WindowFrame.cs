using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Nstrap.App;

/// <summary>Shared caption commands and work-area layout for app-owned windows.</summary>
public static class WindowFrame
{
    public const double CaptionHeight = 36;

    private static readonly DependencyPropertyKey MaximizedPaddingPropertyKey =
        DependencyProperty.RegisterAttachedReadOnly("MaximizedPadding", typeof(Thickness), typeof(WindowFrame),
            new PropertyMetadata(new Thickness()));
    public static readonly DependencyProperty MaximizedPaddingProperty = MaximizedPaddingPropertyKey.DependencyProperty;
    public static Thickness GetMaximizedPadding(DependencyObject element) => (Thickness)element.GetValue(MaximizedPaddingProperty);

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(FrameState), typeof(WindowFrame));

    internal static void Attach(Window window)
    {
        if (window.GetValue(StateProperty) is null)
            window.SetValue(StateProperty, new FrameState(window));
    }

    private sealed class FrameState
    {
        private readonly Window _window;
        private readonly WindowChrome _chrome = new()
        {
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        };
        private readonly List<CommandBinding> _commands = [];
        private HwndSource? _source;
        private DispatcherOperation? _pendingUpdate;
        private bool _closed;

        internal FrameState(Window window)
        {
            _window = window;
            AddCommand(SystemCommands.CloseWindowCommand, SystemCommands.CloseWindow, () => true);
            AddCommand(SystemCommands.MinimizeWindowCommand, SystemCommands.MinimizeWindow,
                () => window.ResizeMode != ResizeMode.NoResize);
            AddCommand(SystemCommands.MaximizeWindowCommand, SystemCommands.MaximizeWindow,
                () => CanResize && window.WindowState != WindowState.Maximized);
            AddCommand(SystemCommands.RestoreWindowCommand, SystemCommands.RestoreWindow,
                () => CanResize && window.WindowState != WindowState.Normal);

            UpdateFrame();
            WindowChrome.SetWindowChrome(window, _chrome);
            window.SourceInitialized += SourceInitialized;
            window.StateChanged += StateChanged;
            window.Closed += Closed;
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
                SourceInitialized(window, EventArgs.Empty);
        }

        private bool CanResize => _window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

        private void AddCommand(RoutedCommand command, Action<Window> action, Func<bool> canExecute)
        {
            var binding = new CommandBinding(command,
                (_, args) =>
                {
                    // The native system command reaches the usual Window.Closing
                    // path, including save failure and busy-operation cancellation.
                    if (canExecute()) action(_window);
                    args.Handled = true;
                },
                (_, args) => { args.CanExecute = canExecute(); args.Handled = true; });
            _commands.Add(binding);
            _window.CommandBindings.Add(binding);
        }

        private void SourceInitialized(object? sender, EventArgs args)
        {
            if (_source is not null) return;
            _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
            _source?.AddHook(WindowMessage);
            UpdateFrame();
        }

        private void StateChanged(object? sender, EventArgs args)
        {
            UpdateFrame();
            CommandManager.InvalidateRequerySuggested();
        }

        private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Observe final geometry; leave native sizing, DPI and system commands
            // to WPF/Windows. Queue after WPF has processed this message.
            const int wmWindowPosChanged = 0x0047, wmStyleChanged = 0x007D, wmDpiChanged = 0x02E0;
            if ((message is wmWindowPosChanged or wmStyleChanged or wmDpiChanged) &&
                !_closed && _pendingUpdate?.Status != DispatcherOperationStatus.Pending)
                _pendingUpdate = _window.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(UpdateFrame));
            return IntPtr.Zero;
        }

        private void UpdateFrame()
        {
            if (_closed) return;
            bool maximized = _window.WindowState == WindowState.Maximized;
            var padding = new Thickness();
            if (maximized && _source?.CompositionTarget is { } target)
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                var monitor = MonitorFromWindow(_source.Handle, 2 /* MONITOR_DEFAULTTONEAREST */);
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) && GetWindowRect(_source.Handle, out var rect))
                {
                    // Glassless WindowChrome clips the HWND to rcWork when maximized,
                    // but leaves its layout at the full window size. Inset only the
                    // content, using this monitor's actual physical overhang and DPI.
                    var transform = target.TransformFromDevice;
                    var leading = transform.Transform(new Vector(
                        Math.Max(0, info.Work.Left - rect.Left), Math.Max(0, info.Work.Top - rect.Top)));
                    var trailing = transform.Transform(new Vector(
                        Math.Max(0, rect.Right - info.Work.Right), Math.Max(0, rect.Bottom - info.Work.Bottom)));
                    padding = new Thickness(leading.X, leading.Y, trailing.X, trailing.Y);
                }
            }
            _window.SetValue(MaximizedPaddingPropertyKey, padding);
            _chrome.ResizeBorderThickness = new Thickness(CanResize && !maximized ? 6 : 0);
            // CaptionHeight begins BELOW the resize strip. Match the template's
            // 36-DIP caption plus its 1-DIP normal border, including maximize insets.
            _chrome.CaptionHeight = CaptionHeight + (maximized ? padding.Top : 1) - _chrome.ResizeBorderThickness.Top;
        }

        private void Closed(object? sender, EventArgs args)
        {
            _closed = true;
            _pendingUpdate?.Abort();
            if (_source is { IsDisposed: false }) _source.RemoveHook(WindowMessage);
            _window.SourceInitialized -= SourceInitialized;
            _window.StateChanged -= StateChanged;
            _window.Closed -= Closed;
            foreach (var binding in _commands) _window.CommandBindings.Remove(binding);
            _source = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
}
