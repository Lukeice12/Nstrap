using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nstrap.App;

internal sealed class MessageDialog : Window
{
    private MessageBoxResult _result;

    private MessageDialog(Window? owner, string message, string title, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult)
    {
        if (buttons is not (MessageBoxButton.OK or MessageBoxButton.YesNo))
            throw new ArgumentOutOfRangeException(nameof(buttons));
        if (owner is not null) Owner = owner;
        Title = title;
        Style = (Style)FindResource(typeof(Window));
        Width = Math.Min(520, SystemParameters.WorkArea.Width - 32);
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 32);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        _result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.No;
        if (defaultResult == MessageBoxResult.None) defaultResult = _result;

        var root = new DockPanel { Margin = new Thickness(24) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        AutomationProperties.SetName(text, message);
        root.Children.Add(new ScrollViewer
        {
            Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = MaxHeight - 150
        });

        Button? defaultButton = null;
        void AddButton(string label, MessageBoxResult result, bool primary)
        {
            var button = new Button
            {
                Content = label, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0),
                IsDefault = result == defaultResult, IsCancel = result is MessageBoxResult.No or MessageBoxResult.OK
            };
            if (primary) button.Style = (Style)FindResource("Primary");
            button.Click += (_, _) => { _result = result; Close(); };
            if (button.IsDefault) defaultButton = button;
            actions.Children.Add(button);
        }
        if (buttons == MessageBoxButton.YesNo)
        {
            AddButton("No", MessageBoxResult.No, false);
            AddButton("Yes", MessageBoxResult.Yes, true);
        }
        else AddButton("OK", MessageBoxResult.OK, true);
        Content = root;
        ThemeManager.Attach(this);
        Loaded += (_, _) => defaultButton?.Focus();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Close();
        };
    }

    internal static MessageBoxResult Show(string message, string title, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(null, message, title, buttons, image, defaultResult);

    internal static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = new MessageDialog(owner, message, title, buttons, image, defaultResult);
        dialog.ShowDialog();
        return dialog._result;
    }
}
