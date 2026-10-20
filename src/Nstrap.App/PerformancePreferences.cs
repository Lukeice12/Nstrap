using System.Globalization;
using Nstrap.Core;

namespace Nstrap.App;

public sealed record GraphicsQualityOption(int Value, string Label);

public sealed partial class MainViewModel
{
    private string _fpsCapText = FpsSettingsManager.UnlockedCap.ToString(CultureInfo.InvariantCulture);
    private string _graphicsQualityStatus = "Changes take effect on Player's next start.";
    private GraphicsQualityOption _selectedGraphicsQuality = null!;
    private static readonly IReadOnlyList<GraphicsQualityOption> QualityOptions =
        new[] { new GraphicsQualityOption(0, "Automatic") }
            .Concat(Enumerable.Range(1, 10).Select(level => new GraphicsQualityOption(level, $"Level {level}"))).ToArray();
    public IReadOnlyList<GraphicsQualityOption> GraphicsQualityOptions => QualityOptions;

    public bool CloseAfterLaunch
    {
        get => State.CloseAfterLaunch;
        set
        {
            if (State.CloseAfterLaunch == value) return;
            State.CloseAfterLaunch = value;
            Changed();
            ScheduleSave();
        }
    }

    public int FpsCap => State.FpsCap;
    public string FpsCapText
    {
        get => _fpsCapText;
        set
        {
            if (!Set(ref _fpsCapText, value ?? "")) return;
            Changed(nameof(FpsCapValidation));
            Changed(nameof(HasFpsCapError));
        }
    }
    public bool HasFpsCapError => !TryReadFpsCap(out _);
    public string FpsCapValidation => HasFpsCapError ? "Enter a whole number from 1 to 9,999." : "";
    public int ReadFpsCapDraft()
    {
        if (!TryReadFpsCap(out int cap)) throw new InvalidDataException(FpsCapValidation);
        return cap;
    }
    private bool TryReadFpsCap(out int cap) => int.TryParse(FpsCapText, NumberStyles.None,
        CultureInfo.InvariantCulture, out cap) && cap is >= 1 and <= FpsSettingsManager.UnlockedCap;
    private static void ValidateFpsCap(int cap)
    {
        if (cap is < 1 or > FpsSettingsManager.UnlockedCap)
            throw new InvalidDataException("Enter a whole FPS cap from 1 to 9,999.");
    }
    public void SaveFpsCapDraft()
    {
        int cap = ReadFpsCapDraft();
        if (UnlockFps)
        {
            QueueFpsChange(true, cap);
        }
        else
        {
            int oldCap = State.FpsCap;
            State.FpsCap = cap;
            try { Save(); }
            catch { State.FpsCap = oldCap; throw; }
            finally { Changed(nameof(FpsCap)); }
        }
        FpsCapText = cap.ToString(CultureInfo.InvariantCulture);
    }

    public GraphicsQualityOption SelectedGraphicsQuality
    {
        get => _selectedGraphicsQuality;
        set
        {
            if (value is null || value.Value is < 0 or > 10) return;
            Set(ref _selectedGraphicsQuality, GraphicsQualityOptions[value.Value]);
        }
    }
    public bool GraphicsQualityChangePending => State.GraphicsQualityChangePending;
    public string GraphicsQualityStatus { get => _graphicsQualityStatus; set => Set(ref _graphicsQualityStatus, value); }
    public void QueueGraphicsQualityChange(bool enabled)
    {
        bool oldManaged = State.GraphicsQualityManaged, oldPending = State.GraphicsQualityChangePending;
        int oldLevel = State.GraphicsQuality;
        State.GraphicsQualityManaged = enabled;
        if (enabled) State.GraphicsQuality = SelectedGraphicsQuality.Value;
        State.GraphicsQualityChangePending = true;
        try { Save(); }
        catch
        {
            State.GraphicsQualityManaged = oldManaged;
            State.GraphicsQualityChangePending = oldPending;
            State.GraphicsQuality = oldLevel;
            throw;
        }
        finally { Changed(nameof(GraphicsQualityChangePending)); }
    }
    public void CompleteGraphicsQualityChange()
    {
        State.GraphicsQualityChangePending = false;
        try { Save(); }
        catch { State.GraphicsQualityChangePending = true; throw; }
        finally { Changed(nameof(GraphicsQualityChangePending)); }
    }
    private void InitializePerformancePreferences()
    {
        _fpsCapText = State.FpsCap.ToString(CultureInfo.InvariantCulture);
        _selectedGraphicsQuality = GraphicsQualityOptions[State.GraphicsQuality];
        if (State.GraphicsQualityChangePending)
            _graphicsQualityStatus = "Graphics quality change pending.";
        else if (State.GraphicsQualityManaged)
            _graphicsQualityStatus = $"{_selectedGraphicsQuality.Label} was applied. Use Apply to reapply or Restore to return the previous choice.";
    }
}
