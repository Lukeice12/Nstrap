using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Nstrap.Core;

namespace Nstrap.App;

public partial class JsonEditorWindow : Window
{
    public IReadOnlyList<FlagEntry>? ImportedFlags { get; private set; }
    public JsonEditorWindow(FlagProfile profile)
    {
        InitializeComponent();
        try { Editor.Text = FlagValidator.Export(profile.Flags); Feedback.Text = "Enabled flags from your selected profile."; }
        catch (Exception ex) { Editor.Text = "{}"; Error(ex); }
    }
    private void Error(Exception ex) { Feedback.Foreground = new SolidColorBrush(Color.FromRgb(255, 166, 145)); Feedback.Text = ex.Message; }
    private List<FlagEntry> Validate()
    {
        var flags = FlagValidator.Import(Editor.Text);
        Feedback.Foreground = (Brush)FindResource("MutedBrush");
        Feedback.Text = $"Valid JSON · {flags.Count} flags. All entries can be applied.";
        return flags;
    }
    private void ValidateClick(object sender, RoutedEventArgs e) { try { Validate(); } catch (Exception ex) { Error(ex); } }
    private void ReplaceClick(object sender, RoutedEventArgs e)
    {
        try { ImportedFlags = Validate(); DialogResult = true; }
        catch (Exception ex) { Error(ex); }
    }
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog { Title = "Import FastFlags JSON", Filter = "JSON settings (*.json)|*.json|All files (*.*)|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            if (new FileInfo(dialog.FileName).Length > FlagValidator.MaximumBytes) throw new InvalidDataException("The JSON file exceeds 2 MB.");
            Editor.Text = File.ReadAllText(dialog.FileName); Validate();
        }
        catch (Exception ex) { Error(ex); }
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var flags = Validate();
            var dialog = new SaveFileDialog { Title = "Export FastFlags JSON", FileName = "ClientAppSettings.json", Filter = "JSON settings (*.json)|*.json", DefaultExt = ".json" };
            if (dialog.ShowDialog(this) != true) return;
            AtomicFile.Write(dialog.FileName, System.Text.Encoding.UTF8.GetBytes(FlagValidator.Export(flags)));
            Feedback.Text = "JSON exported.";
        }
        catch (Exception ex) { Error(ex); }
    }
}
