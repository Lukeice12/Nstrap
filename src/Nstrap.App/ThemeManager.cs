using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Nstrap.App;

internal static class ThemeManager
{
    internal static string CurrentTheme { get; private set; } = "Crimson";
    internal static string DisplayName { get; private set; } = "N strap";
    internal static void SetDisplayName(string name) => DisplayName = name;
    internal static void SetIcon(ImageSource image) => Application.Current.Resources["AppIcon"] = image;

    internal static void Apply(string theme)
    {
        if (theme is not ("Crimson" or "Dark" or "Light"))
            throw new ArgumentException("Choose Crimson, Dark or Light.", nameof(theme));
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{theme}.xaml", UriKind.Absolute)
        };
        // Styles hold DynamicResource expressions, so replacing the one palette
        // updates existing controls and popup content as well as future windows.
        Application.Current.Resources.MergedDictionaries[0] = palette;
        CurrentTheme = theme;
        foreach (Window window in Application.Current.Windows) UpdateTitleBar(window);
    }

    internal static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => UpdateTitleBar(window);
        UpdateTitleBar(window);
    }

    private static void UpdateTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = CurrentTheme == "Light" ? 0 : 1;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        // Caption/text colors are supported on Windows 11. Older Windows keeps
        // its native caption; an unsupported DWM attribute does not affect UI.
        int caption = ColorRef((SolidColorBrush)Application.Current.FindResource("CanvasBrush"));
        int text = ColorRef((SolidColorBrush)Application.Current.FindResource("TextBrush"));
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    private static int ColorRef(SolidColorBrush brush) =>
        brush.Color.R | (brush.Color.G << 8) | (brush.Color.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
