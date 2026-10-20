using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace Nstrap.App;

public partial class LicensesWindow : Window
{
    private sealed record NoticeDocument(string Title, string Text);

    public LicensesWindow()
    {
        InitializeComponent();
        Title = $"License notices · {ThemeManager.DisplayName}";
        ThemeManager.Attach(this);
        NoticeSelector.ItemsSource = new[]
        {
            ReadNotice("N strap · MIT License", "Nstrap.txt"),
            ReadNotice(".NET Runtime · MIT License", "DotNet-LICENSE.txt"),
            ReadNotice(".NET Runtime · Third-party notices", "DotNet-THIRD-PARTY-NOTICES.txt"),
            ReadNotice("WPF / Windows Desktop · MIT License", "WindowsDesktop-LICENSE.txt"),
            ReadNotice("WPF · Third-party notices", "Wpf-THIRD-PARTY-NOTICES.txt")
        };
        NoticeSelector.SelectedIndex = 0;
    }

    private static NoticeDocument ReadNotice(string title, string resourceFile)
    {
        // Assembly resources stay available when N strap is published as a single executable.
        using var stream = typeof(LicensesWindow).Assembly.GetManifestResourceStream($"Nstrap.Licenses.{resourceFile}")
            ?? throw new InvalidOperationException($"The bundled license document “{title}” is missing. Reinstall N strap to restore it.");
        using var reader = new StreamReader(stream);
        return new NoticeDocument(title, reader.ReadToEnd());
    }

    private void NoticeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NoticeSelector.SelectedItem is not NoticeDocument document) return;
        NoticeText.Text = document.Text;
        NoticeText.Select(0, 0);
        NoticeText.ScrollToHome();
        Feedback.Text = "";
    }

    private void CopyAllClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(NoticeText.Text);
            Feedback.Text = "Copied.";
        }
        catch (ExternalException)
        {
            Feedback.Text = "The clipboard is busy. Try copying again.";
        }
    }
}
