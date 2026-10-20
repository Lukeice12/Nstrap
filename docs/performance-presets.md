# Graphics preset mappings

These controls edit the selected FastFlag profile. They do not modify Roblox
until the profile is applied. Automatic removes the listed overrides from the
profile; unrelated flags and draft values remain unchanged.

## Geometry

Mesh detail controls these four flags in order:

1. `DFIntCSGLevelOfDetailSwitchingDistance`
2. `DFIntCSGLevelOfDetailSwitchingDistanceL12`
3. `DFIntCSGLevelOfDetailSwitchingDistanceL23`
4. `DFIntCSGLevelOfDetailSwitchingDistanceL34`

| Choice | Values in that order |
| --- | --- |
| Low | 250, 250, 250, 250 |
| Medium | 500, 250, 250, 250 |
| High | 750, 500, 250, 250 |
| Highest | 1000, 750, 500, 250 |

These are detail-switching preferences. Their visible effect depends on the
geometry and client.

## Rendering and graphics

| Control | Flags and values |
| --- | --- |
| MSAA | `FIntDebugForceMSAASamples`: 1, 2, or 4 samples. One sample disables multisample anti-aliasing. |
| Rendering mode | `FFlagDebugGraphicsPreferD3D11` or `FFlagDebugGraphicsPreferVulkan`; competing renderer preferences are cleared or disabled. |
| Texture quality | `DFFlagTextureQualityOverrideEnabled = true` with `DFIntTextureQualityOverride = 1` for lower detail or `3` for higher detail. |
| Terrain grass off | `FIntFRMMinGrassDistance = 0` and `FIntFRMMaxGrassDistance = 0`. |
| Preserve quality on high DPI | `DFFlagDisableDPIScale = true`. This can increase GPU work on scaled displays. |
| Rendering quality override | `DFIntDebugFRMQualityLevelOverride`, levels 1–21. This is separate from the Overview page's saved graphics quality setting. |

Automatic rendering mode also removes the legacy OpenGL preference. A manually
configured OpenGL preference remains editable in FastFlags and appears as Custom
in the rendering control.

The **Low detail** quick preset combines Low mesh detail, MSAA 1, texture level 1,
and terrain grass off. It does not select a renderer or change DPI/FRM preferences.

## Sources

The control values were compared with Fishstrap's source at commit
[`988db40e3e259489f95e4c7ec40f8dbf5685f309`](https://github.com/fishstrap/fishstrap/tree/988db40e3e259489f95e4c7ec40f8dbf5685f309):

- [FastFlagManager.cs](https://github.com/fishstrap/fishstrap/blob/988db40e3e259489f95e4c7ec40f8dbf5685f309/Bloxstrap/FastFlagManager.cs)
- [FastFlagsViewModel.cs](https://github.com/fishstrap/fishstrap/blob/988db40e3e259489f95e4c7ec40f8dbf5685f309/Bloxstrap/UI/ViewModels/Settings/FastFlagsViewModel.cs)
- [FastFlagsPage.xaml](https://github.com/fishstrap/fishstrap/blob/988db40e3e259489f95e4c7ec40f8dbf5685f309/Bloxstrap/UI/Elements/Settings/Pages/FastFlagsPage.xaml)

N strap implements its own controls and profile handling. Existing texture and
grass preset mappings are retained.

Roblox's [local FastFlag announcement](https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569)
lists these flag families and explains that client support can change. A listed
flag is not a guarantee of a visible effect or a performance improvement. N strap
continues to allow arbitrary valid flag names in its editor.
