using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace Nstrap.App;

public sealed class PromptWindow : Window
{
    private readonly TextBox _input;
    internal PromptWindow(Window owner, string title, string description, string initial, string actionLabel = "Save")
    {
        Owner = owner;
        Title = title;
        Width = Math.Min(480, SystemParameters.WorkArea.Width - 32);
        MaxWidth = Math.Max(280, SystemParameters.WorkArea.Width - 32);
        MaxHeight = Math.Max(200, SystemParameters.WorkArea.Height - 32);
        SizeToContent = SizeToContent.Height;
        Style = (Style)FindResource(typeof(Window));
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 44, ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
            UseAeroCaptionButtons = false
        });
        var layout = new DockPanel();
        var caption = new Grid { Height = 44, Background = (Brush)FindResource("SurfaceBrush") };
        caption.ColumnDefinitions.Add(new ColumnDefinition());
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        caption.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 12, 0)
        });
        var close = new Button
        {
            Content = "\uE8BB", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12, Style = (Style)FindResource("Quiet"), Padding = new Thickness(0),
            Width = 38, Height = 34, ToolTip = "Close", Focusable = true
        };
        AutomationProperties.SetName(close, "Close dialog");
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        caption.Children.Add(close);
        DockPanel.SetDock(caption, Dock.Top);
        layout.Children.Add(caption);
        var panel = new StackPanel { Margin = new Thickness(20, 20, 20, 20) };
        panel.Children.Add(new TextBlock
        {
            Text = description, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 16)
        });
        _input = new TextBox { Text = initial, MaxLength = 120 };
        AutomationProperties.SetName(_input, title);
        panel.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var submitButton = new Button { Content = actionLabel, IsDefault = true, MinWidth = 80, Style = (Style)FindResource("Primary") };
        submitButton.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(_input.Text)) DialogResult = true; else _input.Focus(); };
        buttons.Children.Add(cancel); buttons.Children.Add(submitButton);
        panel.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        Content = new Border
        {
            Background = (Brush)FindResource("CanvasBrush"), BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = layout
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Close(); }
        };
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }
    public static string? Ask(Window owner, string title, string description, string initial, string actionLabel = "Save")
    {
        var prompt = new PromptWindow(owner, title, description, initial, actionLabel);
        return prompt.ShowDialog() == true ? prompt._input.Text.Trim() : null;
    }
}
