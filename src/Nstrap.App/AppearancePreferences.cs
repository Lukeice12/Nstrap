using System.IO;
using Nstrap.App.Services;

namespace Nstrap.App;

public sealed partial class MainViewModel
{
    private string _displayNameDraft = "N strap";
    private string _processNameDraft = "";
    private string _iconStatus = "Original N strap artwork.";
    public IReadOnlyList<string> Themes { get; } = ["Crimson", "Dark", "Light"];
    public string SelectedTheme => State.Theme;
    public string DisplayName => State.DisplayName;
    public string CurrentProcessName => AppIdentity.CurrentProcessName;
    public string CurrentExecutableName => CurrentProcessName + ".exe";
    public string CurrentTaskManagerName => AppIdentity.CurrentTaskManagerName;
    public bool HasCustomIcon => State.CustomIconFile is not null;
    public string IconStatus { get => _iconStatus; private set => Set(ref _iconStatus, value); }

    public string DisplayNameDraft
    {
        get => _displayNameDraft;
        set
        {
            if (!Set(ref _displayNameDraft, value ?? "")) return;
            Changed(nameof(DisplayNameValidation));
            Changed(nameof(CanSaveDisplayName));
        }
    }
    public string DisplayNameValidation => string.IsNullOrWhiteSpace(DisplayNameDraft)
        || DisplayNameDraft.Trim().Length > 48 || DisplayNameDraft.Any(char.IsControl)
        ? "Enter 1–48 characters without line breaks or control characters." : "";
    public bool CanSaveDisplayName => DisplayNameValidation.Length == 0
        && DisplayNameDraft.Trim() != DisplayName;
    public string ProcessNameDraft
    {
        get => _processNameDraft;
        set
        {
            if (!Set(ref _processNameDraft, value ?? "")) return;
            Changed(nameof(ProcessNameValidation));
            Changed(nameof(CanRenameProcess));
        }
    }
    public string ProcessNameValidation
    {
        get
        {
            try { AppIdentity.NormalizeProcessName(ProcessNameDraft); return ""; }
            catch (ArgumentException ex) { return ex.Message; }
            catch (InvalidDataException ex) { return ex.Message; }
        }
    }
    public bool CanRenameProcess => ProcessNameValidation.Length == 0
        && !AppIdentity.IdentityIsCurrent(ProcessNameDraft);

    private void InitializeAppearancePreferences()
    {
        _displayNameDraft = DisplayName;
        _processNameDraft = CurrentProcessName;
        ThemeManager.SetDisplayName(DisplayName);
        try
        {
            ThemeManager.SetIcon(AppearanceIcon.Load(DataDirectory, State.CustomIconFile));
            if (HasCustomIcon) _iconStatus = "Custom icon saved for the sidebar, window and taskbar.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            ThemeManager.SetIcon(AppearanceIcon.Load(DataDirectory, null));
            _iconStatus = "The saved icon could not be loaded. Original artwork is shown; choose an icon again or reset it.";
        }
    }

    public void SetTheme(string theme)
    {
        if (!Themes.Contains(theme) || State.Theme == theme) return;
        string previous = State.Theme;
        State.Theme = theme;
        try { Save(); }
        catch { State.Theme = previous; throw; }
        finally { Changed(nameof(SelectedTheme)); }
        ThemeManager.Apply(theme);
        Success($"{theme} theme saved.");
    }

    public void SaveDisplayName()
    {
        if (DisplayNameValidation.Length != 0) throw new InvalidDataException(DisplayNameValidation);
        string previous = State.DisplayName;
        State.DisplayName = DisplayNameDraft.Trim();
        try { Save(); }
        catch { State.DisplayName = previous; throw; }
        finally { Changed(nameof(DisplayName)); Changed(nameof(CanSaveDisplayName)); }
        DisplayNameDraft = DisplayName;
        ThemeManager.SetDisplayName(DisplayName);
        Success("Program name saved.", "The title and sidebar use your chosen name.");
    }

    public void SetCustomIcon(string? sourcePath)
    {
        string? previous = State.CustomIconFile;
        // Import completes and decodes before the persisted selection changes.
        string? next = sourcePath is null ? null : AppearanceIcon.Import(DataDirectory, sourcePath);
        System.Windows.Media.ImageSource image;
        try
        {
            image = AppearanceIcon.Load(DataDirectory, next);
            State.CustomIconFile = next;
            Save();
        }
        catch (Exception error)
        {
            State.CustomIconFile = previous;
            string cleanupError = RemoveUnusedIcon(next);
            if (cleanupError.Length != 0)
                throw new IOException($"{error.Message} The unused icon copy could not be removed: {cleanupError}", error);
            throw;
        }
        ThemeManager.SetIcon(image);
        Changed(nameof(HasCustomIcon));
        IconStatus = next is null ? "Original N strap artwork." : "Custom icon saved for the sidebar, window and taskbar.";
        string previousCleanup = RemoveUnusedIcon(previous);
        if (previousCleanup.Length != 0) IconStatus += " The previous cached copy could not be removed.";
        Success(next is null ? "Original icon restored." : "Custom icon saved.", previousCleanup);
    }

    private string RemoveUnusedIcon(string? fileName)
    {
        if (fileName is null || fileName == State.CustomIconFile) return "";
        try { AppearanceIcon.RemoveManaged(DataDirectory, fileName); return ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ex.Message;
        }
    }
}
