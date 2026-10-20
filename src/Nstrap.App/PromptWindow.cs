using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

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
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
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
        Content = new ScrollViewer
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        ThemeManager.Attach(this);
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
