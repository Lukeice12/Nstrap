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
    // Values reviewed against Fishstrap 988db40e3e259489f95e4c7ec40f8dbf5685f309.
    // These are configuration choices, not measured performance guarantees.
    public static FlagSetting MeshDetail { get; } = new("Mesh detail",
    [
        MeshChoice("Low", 250, 250, 250, 250),
        MeshChoice("Medium", 500, 250, 250, 250),
        MeshChoice("High", 750, 500, 250, 250),
        MeshChoice("Highest", 1000, 750, 500, 250)
    ]);
    public static FlagSetting Msaa { get; } = new("Anti-aliasing",
    [
        new("Off (1×)", [Flag("FIntDebugForceMSAASamples", "1")]),
        new("2×", [Flag("FIntDebugForceMSAASamples", "2")]),
        new("4×", [Flag("FIntDebugForceMSAASamples", "4")])
    ]);
    public static FlagSetting Renderer { get; } = new("Rendering mode",
    [
        new("Direct3D 11", [Flag("FFlagDebugGraphicsPreferD3D11", "true"),
            Flag("FFlagDebugGraphicsPreferVulkan", "false"), Flag("FFlagDebugGraphicsPreferOpenGL", "false")]),
        new("Vulkan", [Flag("FFlagDebugGraphicsPreferD3D11", "false"),
            Flag("FFlagDebugGraphicsPreferVulkan", "true"), Flag("FFlagDebugGraphicsPreferOpenGL", "false")])
    ], allowAbsentFalse: true);
    public static FlagSetting TextureQuality { get; } = new("Texture quality",
    [
        new("Lower (level 1)", [Flag("DFFlagTextureQualityOverrideEnabled", "true"), Flag("DFIntTextureQualityOverride", "1")]),
        new("Higher (level 3)", [Flag("DFFlagTextureQualityOverrideEnabled", "true"), Flag("DFIntTextureQualityOverride", "3")])
    ]);
    public static FlagSetting Grass { get; } = new("Terrain grass",
    [
        new("Off", [Flag("FIntFRMMinGrassDistance", "0"), Flag("FIntFRMMaxGrassDistance", "0")])
    ]);
    public static FlagSetting NativeDpi { get; } = new("Preserve quality on high DPI",
    [
        new("On", [Flag("DFFlagDisableDPIScale", "true")])
    ]);
    public static FlagSetting RenderingQuality { get; } = new("Rendering quality override",
        Enumerable.Range(1, 21).Select(level => new FlagChoice($"Level {level}",
            [Flag("DFIntDebugFRMQualityLevelOverride", level.ToString(System.Globalization.CultureInfo.InvariantCulture))])).ToArray());
    public static FlagPreset LowDetail { get; } = new("Low detail", "PERFORMANCE",
        "Use low mesh detail, lighter textures, no terrain grass and no MSAA.",
        "Trades visual detail for potentially less rendering work. Results depend on the experience and hardware.",
        [.. MeshDetail.Choices[1].Flags, .. TextureQuality.Choices[1].Flags, .. Grass.Choices[1].Flags, .. Msaa.Choices[1].Flags]);

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
    private static FlagChoice MeshChoice(string label, int first, int second, int third, int fourth) => new(label,
    [
        Flag("DFIntCSGLevelOfDetailSwitchingDistance", first.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Flag("DFIntCSGLevelOfDetailSwitchingDistanceL12", second.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Flag("DFIntCSGLevelOfDetailSwitchingDistanceL23", third.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Flag("DFIntCSGLevelOfDetailSwitchingDistanceL34", fourth.ToString(System.Globalization.CultureInfo.InvariantCulture))
    ]);
    private static FlagEntry Flag(string name, string value) => new() { Name = name, Value = value };
}
