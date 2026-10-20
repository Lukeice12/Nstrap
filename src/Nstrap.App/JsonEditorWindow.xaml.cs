using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Nstrap.Core;

namespace Nstrap.App;

public partial class JsonEditorWindow : Window
{
    private readonly List<int> _matches = [];
    private int _activeMatch = -1;

    public IReadOnlyList<FlagEntry>? ImportedFlags { get; private set; }
    public string ImportNotice { get; private set; } = "";
    public JsonEditorWindow(FlagProfile profile)
    {
        InitializeComponent();
        Title = $"JSON editor · {ThemeManager.DisplayName}";
        ThemeManager.Attach(this);
        // Editing must preserve disabled entries and unfinished values verbatim.
        // Validation belongs to Replace/Export, so invalid drafts remain correctable.
        Editor.Text = JsonSerializer.Serialize(profile.Flags.Select(flag => new
        {
            name = flag.Name, value = flag.Value, enabled = flag.Enabled
        }), JsonDefaults.Options);
        Feedback.Text = "All flags, including disabled flags.";
        FindQuery.TextChanged += FindQueryChanged;
        Editor.TextChanged += EditorTextChanged;
        Editor.SelectionChanged += EditorSelectionChanged;
        UpdateMatchStatus();
    }

    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenFind();
        }
        else if (e.Key == Key.Escape && FindBar.IsVisible)
        {
            // Handle before the dialog's IsCancel button can close the editor.
            e.Handled = true;
            CloseFind();
        }
        else if (e.Key == Key.Enter && FindQuery.IsKeyboardFocusWithin)
        {
            // Enter in the JSON document must continue to insert a newline.
            e.Handled = true;
            NavigateMatch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
    }

    private void OpenFind()
    {
        var selection = Editor.IsKeyboardFocusWithin && Editor.SelectionLength is > 0 and <= 256
            ? Editor.SelectedText : null;
        FindBar.Visibility = Visibility.Visible;
        if (selection is not null && !selection.Contains('\r') && !selection.Contains('\n') && selection != FindQuery.Text)
            FindQuery.Text = selection;
        else
            RefreshMatches(selectMatch: true);
        FindQuery.Focus();
        FindQuery.SelectAll();
    }

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        Editor.Focus();
    }

    private void FindQueryChanged(object sender, TextChangedEventArgs e) => RefreshMatches(selectMatch: true);
    private void EditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (FindBar.IsVisible)
            RefreshMatches(selectMatch: false); // Recount without moving the caret while typing.
    }

    private void RefreshMatches(bool selectMatch)
    {
        _matches.Clear();
        _activeMatch = -1;
        var query = FindQuery.Text;
        if (query.Length > 0)
        {
            var text = Editor.Text;
            for (var start = 0; start <= text.Length - query.Length;)
            {
                var match = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (match < 0) break;
                _matches.Add(match);
                start = match + query.Length;
            }
        }

        if (selectMatch && _matches.Count > 0)
            SelectMatch(MatchFromCaret(previous: false));
        else
        {
            if (selectMatch)
                Editor.Select(Editor.SelectionStart, 0);
            TrackSelectedMatch();
        }
    }

    private void EditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (FindBar.IsVisible) TrackSelectedMatch();
    }

    private void TrackSelectedMatch()
    {
        var index = Editor.SelectionLength == FindQuery.Text.Length
            ? _matches.BinarySearch(Editor.SelectionStart) : -1;
        _activeMatch = index >= 0 ? index : -1;
        UpdateMatchStatus();
    }

    private int MatchFromCaret(bool previous)
    {
        var index = _matches.BinarySearch(Editor.SelectionStart);
        index = index >= 0 ? index : ~index;
        if (previous) index--;
        return (index + _matches.Count) % _matches.Count;
    }

    private void NavigateMatch(bool previous)
    {
        if (_matches.Count == 0) return;
        var index = _activeMatch >= 0
            ? (_activeMatch + (previous ? -1 : 1) + _matches.Count) % _matches.Count
            : MatchFromCaret(previous);
        SelectMatch(index);
    }

    private void SelectMatch(int index)
    {
        var start = _matches[index];
        Editor.Select(start, FindQuery.Text.Length);
        _activeMatch = index;
        Editor.UpdateLayout();
        var line = Editor.GetLineIndexFromCharacterIndex(start);
        if (line >= 0)
        {
            Editor.ScrollToLine(line);
            Editor.UpdateLayout();
        }
        // Selection can remain in the document while the query has focus. Explicitly
        // scroll its leading edge into view, including matches on long JSON lines.
        var rect = Editor.GetRectFromCharacterIndex(start);
        if (!rect.IsEmpty)
            Editor.ScrollToHorizontalOffset(Math.Max(0, Editor.HorizontalOffset + rect.Left - Editor.Padding.Left));
        UpdateMatchStatus();
    }

    private void UpdateMatchStatus()
    {
        var hasQuery = FindQuery.Text.Length > 0;
        MatchStatus.Text = !hasQuery ? "" : _matches.Count == 0 ? "No matches"
            : _activeMatch >= 0 ? $"{_activeMatch + 1} / {_matches.Count}"
            : $"{_matches.Count} {(_matches.Count == 1 ? "match" : "matches")}";
        MatchStatus.ToolTip = _activeMatch >= 0
            ? $"Match {_activeMatch + 1} of {_matches.Count}" : MatchStatus.Text;
        PreviousMatchButton.IsEnabled = NextMatchButton.IsEnabled = _matches.Count > 0;
    }

    private void PreviousMatchClick(object sender, RoutedEventArgs e) => NavigateMatch(previous: true);
    private void NextMatchClick(object sender, RoutedEventArgs e) => NavigateMatch(previous: false);
    private void CloseFindClick(object sender, RoutedEventArgs e) => CloseFind();

    private void Error(Exception ex) { Feedback.SetResourceReference(ForegroundProperty, "ErrorBrush"); Feedback.Text = ex.Message; }
    private List<FlagEntry> Validate()
        => ShowValidation(FlagValidator.ParseImport(Editor.Text, ProfileStore.MaximumBytes));

    private List<FlagEntry> ShowValidation(FlagImportDocument document)
    {
        var flags = document.Flags;
        ImportNotice = document.Notice;
        Feedback.SetResourceReference(ForegroundProperty, "MutedBrush");
        Feedback.Text = $"Valid JSON · {flags.Count} flag{(flags.Count == 1 ? "" : "s")} · {flags.Count(flag => flag.Enabled)} enabled. {ImportNotice}".TrimEnd();
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
            var dialog = new OpenFileDialog { Title = "Import FastFlags JSON", Filter = "Flag files (*.txt;*.json)|*.txt;*.json|All files (*.*)|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            var json = FlagFileImport.ReadText(dialog.FileName);
            var document = FlagValidator.ParseImport(json);
            Editor.Text = json;
            ShowValidation(document);
        }
        catch (Exception ex) { Error(ex); }
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var flags = Validate();
            var bytes = Encoding.UTF8.GetBytes(FlagValidator.Export(flags));
            if (bytes.Length > FlagValidator.MaximumBytes)
                throw new InvalidDataException("The exported flags would exceed the 2 MB import limit. Export fewer enabled flags.");
            var dialog = new SaveFileDialog { Title = "Export FastFlags JSON", FileName = "ClientAppSettings.json", Filter = "JSON settings (*.json)|*.json", DefaultExt = ".json" };
            if (dialog.ShowDialog(this) != true) return;
            AtomicFile.Write(dialog.FileName, bytes);
            Feedback.Text = $"Enabled flags exported as a flat JSON object. {ImportNotice}".TrimEnd();
        }
        catch (Exception ex) { Error(ex); }
    }
}
