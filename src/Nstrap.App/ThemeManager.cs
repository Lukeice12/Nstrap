using System.Windows;
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
    }

    internal static void Attach(Window window) => WindowFrame.Attach(window);
}
