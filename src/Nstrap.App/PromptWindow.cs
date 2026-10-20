using System.Windows;
using System.Windows.Controls;

namespace Nstrap.App;

public sealed class PromptWindow : Window
{
    private readonly TextBox _input;
    private PromptWindow(Window owner, string title, string description, string initial)
    {
        Owner = owner; Title = title; Width = 470; Height = 260;
        Style = (Style)FindResource(typeof(Window));
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = description, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 10, 0, 18) });
        _input = new TextBox { Text = initial, MaxLength = 120 };
        panel.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Save", IsDefault = true, Style = (Style)FindResource("Primary") };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(_input.Text)) DialogResult = true; else _input.Focus(); };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }
    public static string? Ask(Window owner, string title, string description, string initial)
    {
        var prompt = new PromptWindow(owner, title, description, initial);
        return prompt.ShowDialog() == true ? prompt._input.Text.Trim() : null;
    }
}
