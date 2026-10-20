# N strap

A Windows launcher and FastFlag manager for Roblox, focused on performance settings and a compact desktop interface.

N strap is an original implementation. Its application code uses .NET and Windows APIs, with no third-party runtime NuGet packages, telemetry, Roblox login, or credential collection.

![N strap FastFlags editor](docs/images/fastflags.png)

## Download

Download [Nstrap-win-x64.zip](https://github.com/Lukeice12/Nstrap/releases/download/v0.2.6/Nstrap-win-x64.zip), choose **Extract All**, and open **Nstrap.exe**. The ZIP contains just that executable and includes the .NET runtime; no separate .NET installation is needed. See [Releases](https://github.com/Lukeice12/Nstrap/releases) for version notes. Release binaries are not code signed.

## Features

- Launch Roblox Player or Studio from a clean desktop interface.
- Create local FastFlag profiles, toggle individual flags, and import or export JSON.
- Drop a `.txt` or `.json` flag file onto the window, or click **Import**, to import and apply it.
- Enable **Custom frame rate** and choose a Player cap from 1–9,999 FPS.
- Choose automatic graphics quality or a level from 1–10, with an option to restore the previous setting.
- Tune mesh detail, MSAA, rendering mode, textures, and terrain grass from **Presets**, or add the **Low detail** preset.
- Optionally close N strap after Roblox launches.
- Search settings from the sidebar.
- Choose Crimson, Dark, or Light in **Appearance**, with Crimson as the default.
- Matching title bars across the main window, editor, prompts, and license viewer.
- Customize the app's displayed name, window icon, and executable/process name.
- Configure optional global **Kill switch** and **Nuke** shortcuts in **Experimental**.
- Run silently at Windows sign-in, prepare Player flags in the background, and show or hide the window with a shortcut.
- Optionally keep N strap running in the background when you close its window.
- Manually clear old Roblox cache files to free disk space.
- Enter arbitrary valid FastFlag names and values. **N strap does not enforce a flag allowlist or blocklist.**
- Apply profiles to an installed client with backups and recovery information.
- Detect Roblox installations or choose an installation manually.
- Install or update Player through Roblox's official installer.
- Keep N strap up to date automatically on startup, or check manually from **Updates**.

Roblox decides which flags its clients honor. The app includes informational labels based on [Roblox's published documentation](https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569). Player may ignore unlisted flags; Studio has different behavior. These labels do not restrict editing or applying flags, and they cannot guarantee that a particular flag still works.

## Use N strap

1. In **Overview**, select a detected Player or Studio installation. Use **Install / update Player** if Player is missing.
2. In **FastFlags**, create a profile and add flags individually, click **Import**, or drop a `.txt` or `.json` flag file onto the window. Profiles start empty.
3. File import merges into the selected profile and immediately attempts to apply it to the selected installation. You can also choose **Apply profile** or **Launch Roblox**. Launch closes all running Roblox Player sessions in your current Windows session before applying settings and starting the selected client. Studio stays open. Applying before launch is enabled by default; using **Apply profile** alone still requires restarting a running client to load the saved flags.
4. Use **Recovery → Restore** to undo N strap's managed changes.

Edits save automatically. File import replaces values and enabled states for matching names; other profile entries are retained. The whole file is validated before importing. If automatic application cannot finish, the imported profile stays saved and the app reports what needs attention.

Import accepts both a flat JSON flag map, such as `{"FFlagExample": true}`, and an array of records, such as `[{"name": "FFlagExample", "value": "True", "type": "bool", "enabled": true}]`. Flat maps enable all imported flags. Record arrays preserve each entry's `enabled` state, defaulting to enabled when it is omitted. Key bindings and cycling settings from other applications are not imported; N strap imports the listed flag values.

The JSON editor opens a record array containing every flag, including disabled entries and draft values that need correction. **Replace profile** validates the edited document before replacing the profile's entries. Exported files remain a flat map containing enabled flags. Presets merge into the selected profile when chosen.

In the JSON editor, press **Ctrl+F** to find text. Search ignores letter case and shows the match count. With the search box focused, use **Enter** for the next match and **Shift+Enter** for the previous match. **Escape** closes the search bar.

### Performance settings

On **Overview**, enable **Custom frame rate** and choose a cap from 1–9,999 FPS with **Set limit**. The default is 9,999. This controls Roblox Player's global framerate preference independently of FastFlag profiles; it cannot guarantee a particular framerate or override graphics-driver limits. Turning the toggle off restores the previous cap. If the cap has since been changed elsewhere, that newer value is kept instead.

**Graphics quality** offers Automatic and manual levels 1–10. Lower levels reduce visual detail. Apply a level for Player's next start, or restore the previous setting. N strap preserves unrelated settings and later changes made outside the app.

If Player is running under your Windows account, pending settings wait until it closes, including sessions elsewhere on the same computer. Another Windows account's Player does not block the change. Keep N strap open to apply pending changes, or reopen it before launching Roblox.

Enable **Close N strap after launch** to close the window after Roblox starts successfully. It is off by default. With **Minimise on close** enabled, N strap stays running in the background; otherwise it exits.

### Graphics presets

**Presets** has controls for mesh detail, MSAA, rendering mode, texture quality, terrain grass, DPI scaling, and rendering quality override. Changes save to the selected FastFlag profile. Apply the profile or launch Roblox to use them; restart an already-running client.

**Low detail** combines lower mesh and texture settings, one-sample MSAA, and zero terrain grass distances. It leaves the rendering mode, DPI setting, and rendering quality override alone. The existing quick presets remain available.

**Automatic** removes only that control's overrides from the selected profile. **Custom** means the profile contains values that do not match a preset; those values stay in place until you choose another option. These controls reflect profile switches, imports, and manual flag edits.

Preserving rendering quality with Windows display scaling can increase GPU work. Higher MSAA and rendering quality settings can also cost performance. Renderer preferences vary by graphics hardware and driver; none of these options guarantees a framerate improvement.

See [preset mappings and sources](docs/performance-presets.md) for the exact flags and values.

### Find settings and clean the cache

Use **Search settings** in the sidebar to find a setting and open its page. FastFlags retain their own name filter, and the JSON editor has separate Ctrl+F text search.

The manual cache cleaner removes old files from `%LOCALAPPDATA%\Roblox\rbx-storage`, which is shared by Player and Studio. Both clients must be closed for your Windows account. Cleanup frees disk space; it does not increase FPS, and Roblox may download cached content again. It does not run automatically.

### Appearance

Choose **Crimson**, **Dark**, or **Light** in **Appearance**. Crimson is the default, and theme changes apply immediately to the app and its dialogs. Dark keeps the charcoal palette.

App windows share a title bar with the selected theme, name, and icon. Moving, resizing, and window commands use Windows' normal handling. The custom maximize button does not provide Windows 11's hover snap-layout menu.

The display name changes the window title and sidebar. Choose a custom PNG or ICO for the window and taskbar icon, or reset to the included artwork. N strap saves its own copy of the selected image.

The process name changes the executable's filename and the description shown for background processes in Task Manager. It requires a restart and a writable local app folder. A new filename must not already exist in that folder. The original executable is removed only after the renamed copy opens successfully; shortcuts pointing to the old filename need updating. Automatic updates preserve the chosen name. If an older build left the Task Manager label unchanged, reapply the same process name after upgrading. The Explorer executable icon remains N strap's.

### Silent mode and background startup

In **Experimental**, record and save a **Show / hide** shortcut before enabling a background option. **Silent mode** hides the app and prepares the selected saved FastFlag profile for the newest Roblox Player installation in each configured location. It watches for new Player installations and saved profile changes. No tray icon, startup window, or notification is shown.

Enable **Start with Windows** to run silently when your Windows account signs in. This uses the extracted portable EXE, so keep it in a permanent folder. Opening the EXE yourself reveals the running app; it is also a way back in if another program takes the shortcut. Errors are shown inside N strap when you open it.

**Minimise on close** keeps N strap running when you press the window's close button, with its windows hidden from the taskbar. Use the shortcut to bring them back, or **Exit N strap** to quit fully. The existing **Close after launch** option follows this close behavior too.

Background application waits while Roblox clients or installers are running. If Roblox starts a newly installed version before its flags are prepared, those changes take effect on its next launch. **Restore** pauses background application for the current run until the profile changes or Silent mode is re-enabled. Starting N strap again prepares the saved profile again. Background application preserves managed values changed outside N strap and reports conflicts inside the app.

Turning off **Start with Windows** removes N strap's own startup entry. Process renaming updates that entry, and Nuke removes it during cleanup. Startup settings changed through Windows are respected.

### Experimental shortcuts

**Experimental** also provides two emergency shortcuts. Both start disabled with no key assigned. They work while N strap is running, including when another app has focus.

- **Kill switch** immediately closes N strap without saving pending edits or showing a confirmation. It leaves Roblox running.
- **Nuke** closes N strap silently and starts removal of the running portable executable and this Windows account's N strap data: profiles, settings, custom icons, backups, recovery records, and update cache. It first attempts to restore managed Roblox settings where this can be done safely, preserving changes made outside N strap.

Click **Record shortcut**, press Ctrl or Alt with another key, then **Save shortcut** and enable the action. Nuke also requires accepting its deletion scope. Choose different shortcuts for the two actions. Configuration errors appear in Experimental; pressing an armed shortcut does not open a confirmation or progress window.

Nuke permanently deletes saved profiles. It prepares a background helper while enabled and pauses during app updates or executable renames, then re-arms after restart. Turning it off or closing the app normally cancels removal.

Cleanup is limited to N strap's own files. Copies and downloads elsewhere, Windows records, restore points, and the main app's .NET runtime extraction cache are outside its scope. Locked, protected, or redirected files can remain. Roblox settings may also remain if a client is running, their current values have changed elsewhere, or recovery information cannot be used safely.

## Update N strap

Open **Updates** to see the installed version, change **Update automatically**, or choose **Check for updates**. Automatic updates are enabled by default. When a newer release is available at startup, N strap downloads it and restarts into the updated app. Manual checks let you choose **Install and restart**.

The updater uses this repository's public GitHub releases, including preview releases, and checks the package against GitHub's SHA-256 digest before installing it. Network or download failures leave the current version available to use. Installation requires the published Windows x64 executable, running normally from a writable local folder. A custom process filename is supported. Updates do not request administrator access.

Update downloads and helper files are stored in `%LOCALAPPDATA%\Nstrap\updates`. Replacement keeps a backup of the previous executable beside the running app while startup is confirmed. An incomplete installation pauses automatic retries for that version; the Updates tab lets you retry manually.

Versions before **0.1.7** need a one-time manual upgrade: close N strap, download and extract the current ZIP, and run the new `Nstrap.exe`.

## Run from source

Requires Windows 10/11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The SDK version is recorded in `global.json`.

```powershell
dotnet run --project src/Nstrap.App
```

Build and run the isolated core checks:

```powershell
dotnet build Nstrap.slnx -c Release
dotnet run --project tests/Nstrap.Checks -c Release
```

The checks use temporary directories; they do not change your installed Roblox clients.

## Create a portable app

```powershell
dotnet publish src/Nstrap.App/Nstrap.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None `
  -o artifacts/Nstrap-win-x64
```

Run `artifacts/Nstrap-win-x64/Nstrap.exe`. A self-contained publish includes the .NET runtime.

Publish GitHub releases as regular releases, with the newest version marked **Latest**. Use simple version tags and titles such as `v0.2.6`. Keep release notes to a few short bullets describing what changed.

On the initial development machine, the verified SDK is stored locally at `.tools/dotnet/dotnet.exe`. Replace `dotnet` in the commands above with `.\.tools\dotnet\dotnet.exe` if it is not on your PATH. Downloaded tools and build output are excluded from Git.

## Project layout

| Path | Purpose |
| --- | --- |
| `src/Nstrap.App` | Native WPF interface, verified client launch and official installer |
| `src/Nstrap.Core` | Profiles, validation, installation discovery, apply/recovery |
| `tests/Nstrap.Checks` | Dependency-free executable checks |
| `scripts` | Local build and publish helpers |
| `.github/workflows` | Windows build, checks, and portable build artifacts |

## Trust and scope

You can review the complete application source and build it yourself. Installation downloads come from Roblox's official HTTPS endpoint and must pass Windows signature verification and the Roblox publisher check before execution. The app does not download FastFlag packs or execute imported JSON.

Roblox accounts and game authentication stay with the official Roblox software. This first version uses Roblox's official installation and update process. It does not replace Roblox's binary updater or register itself as the system's Roblox browser protocol handler.

Roblox loads these settings at startup. Applying a profile uses its enabled flag values. If a managed flag was changed elsewhere, its current value is saved as the new restore value before the profile's value is written. Removing a managed flag restores its recorded original value if it still matches what N strap wrote; a newer external value is left alone. Unrelated settings are preserved. Recovery's **Restore previous values** reports conflicts instead of replacing externally changed values. Settings backups and interrupted-write checks remain in place.

App data lives in `%LOCALAPPDATA%\Nstrap`: `profiles.json` stores profiles, `backups` holds previous settings files, and `recovery` holds ownership and interrupted-operation records. Keep the recovery records if you want N strap to restore values it previously changed.

Profile saves that would exceed the existing 10 MB loading limit are rejected before replacing the saved file. Applying flags also checks that the combined settings, including existing entries, stay within the reader's 5,000-entry limit.

FPS and graphics controls change the existing `FramerateCap` and `SavedQualityLevel` properties in `%LOCALAPPDATA%\Roblox\GlobalBasicSettings_13.xml`. Settings backups and ownership records allow restoration without replacing unrelated preferences. N strap does not make Roblox's settings file read-only.

## License

N strap is licensed under the [MIT License](LICENSE). Copyright © 2026 lukeistaken.
You can use, modify, redistribute, or sell it; keep the copyright and license notice
with copies or substantial portions of the software.

The self-contained executable includes the .NET and Windows Desktop runtimes.
Their license texts and third-party notices are embedded in the app. In
**About & settings → Licenses**, N strap's card opens its GitHub license page and
the .NET and WPF cards open their project repositories;
**View notices** opens the bundled texts without an internet connection.
See [notice sources](src/Nstrap.App/Licenses/README.md) for the matching runtime versions.

N strap is an independent project and is not affiliated with Roblox.
