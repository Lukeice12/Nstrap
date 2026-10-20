namespace Nstrap.Core;

public static class FlagCatalog
{
    public const string SourceUrl = "https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569";
    public const string CheckedOn = "20 October 2026";
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "DFIntCSGLevelOfDetailSwitchingDistance", "DFIntCSGLevelOfDetailSwitchingDistanceL12",
        "DFIntCSGLevelOfDetailSwitchingDistanceL23", "DFIntCSGLevelOfDetailSwitchingDistanceL34",
        "FFlagHandleAltEnterFullscreenManually", "DFFlagTextureQualityOverrideEnabled",
        "DFIntTextureQualityOverride", "FIntDebugForceMSAASamples", "DFFlagDisableDPIScale",
        "FFlagDebugGraphicsPreferD3D11", "FFlagDebugSkyGray", "DFFlagDebugPauseVoxelizer",
        "DFIntDebugFRMQualityLevelOverride", "FIntFRMMaxGrassDistance", "FIntFRMMinGrassDistance",
        "FFlagDebugGraphicsPreferVulkan", "FFlagDebugGraphicsPreferOpenGL",
        "FIntGrassMovementReducedMotionFactor"
    };
    public static bool IsAllowed(string name) => Allowed.Contains(name);
    public static IReadOnlyList<FlagPreset> Presets { get; } =
    [
        new("Direct3D 11", "RENDERER", "Request the Direct3D 11 renderer on Windows.",
            "A preference, not a performance guarantee. Conflicting renderer flags are disabled.",
            [Flag("FFlagDebugGraphicsPreferD3D11", "true"), Flag("FFlagDebugGraphicsPreferVulkan", "false"), Flag("FFlagDebugGraphicsPreferOpenGL", "false")]),
        new("Sharper textures", "VISUALS", "Enable the texture quality override at level 3.",
            "Can increase graphics memory use. Appearance depends on the experience and client.",
            [Flag("DFFlagTextureQualityOverrideEnabled", "true"), Flag("DFIntTextureQualityOverride", "3")]),
        new("Lighter textures", "VISUALS", "Enable the texture quality override at level 1.",
            "May reduce texture detail and memory use. No FPS improvement is promised.",
            [Flag("DFFlagTextureQualityOverrideEnabled", "true"), Flag("DFIntTextureQualityOverride", "1")]),
        new("Less grass", "ENVIRONMENT", "Set the minimum and maximum grass drawing distances to zero.",
            "Affects terrain grass where the client honors these flags. Experience visuals can vary.",
            [Flag("FIntFRMMinGrassDistance", "0"), Flag("FIntFRMMaxGrassDistance", "0")]),
        new("Native DPI", "DISPLAY", "Request rendering without Roblox's DPI scaling.",
            "High resolution displays may use more GPU resources. Restart Roblox after applying.",
            [Flag("DFFlagDisableDPIScale", "true")]),
        new("Manual fullscreen", "WINDOW", "Request Roblox's manual handling of Alt + Enter.",
            "Fullscreen behavior depends on the current client and Windows configuration.",
            [Flag("FFlagHandleAltEnterFullscreenManually", "true")])
    ];
    private static FlagEntry Flag(string name, string value) => new() { Name = name, Value = value };
}
