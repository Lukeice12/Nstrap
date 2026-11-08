using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nstrap.App;

internal sealed class RuntimeResultsWindow : Window
{
    internal RuntimeResultsWindow(string text)
    {
        Title = "Live flag results"; Width = 900; Height = 620; MinWidth = 640; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        var panel = new DockPanel { Margin = new Thickness(20) };
        var heading = new TextBlock
        {
            Text = "Live read-back and settings-file writes are separate. Stored values are verified; gameplay effects are not measured.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var copy = new Button { Content = "Copy results", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        copy.Click += (_, _) => Clipboard.SetText(text);
        DockPanel.SetDock(copy, Dock.Bottom); panel.Children.Add(copy);
        var body = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(12) };
        body.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
        body.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        panel.Children.Add(body); Content = panel;
    }
}
