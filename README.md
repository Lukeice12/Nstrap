# N strap

A personal Windows launcher and FastFlag manager for Roblox Player and Studio. Built in C# with a native WPF interface.

N strap is an original implementation. Its application code uses .NET and Windows APIs, with no third-party runtime NuGet packages, telemetry, Roblox login, or credential collection.

![N strap FastFlags editor](docs/images/fastflags.png)

## Download

Download [Nstrap-win-x64.zip](https://github.com/Lukeice12/Nstrap/releases/download/v0.1.4/Nstrap-win-x64.zip), choose **Extract All**, and open **Nstrap.exe**. The ZIP contains just that executable and includes the .NET runtime; no separate .NET installation is needed. See [Releases](https://github.com/Lukeice12/Nstrap/releases) for version notes. Preview binaries are not code signed.

## Features

- Launch Roblox Player or Studio from a clean desktop interface.
- Create local FastFlag profiles, toggle individual flags, and import or export JSON.
- Drop a `.txt` or `.json` flag file onto the window, or click **Import**, to import and apply it.
- Toggle **Unlock FPS** to request a 9,999 FPS cap in Roblox Player and restore the previous cap when turned off.
- Enter arbitrary valid FastFlag names and values. **N strap does not enforce a flag allowlist or blocklist.**
- Apply profiles to an installed client with backups and recovery information.
- Detect Roblox installations or choose an installation manually.
- Install or update Player through Roblox's official installer.

Roblox decides which flags its clients honor. The app includes informational labels based on [Roblox's published documentation](https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569). Player may ignore unlisted flags; Studio has different behavior. These labels do not restrict editing or applying flags, and they cannot guarantee that a particular flag still works.

## Use N strap

1. In **Overview**, select a detected Player or Studio installation. Use **Install / update Player** if Player is missing.
2. In **FastFlags**, create a profile and add flags individually, click **Import**, or drop a `.txt` or `.json` flag file onto the window. Profiles start empty.
3. File import merges into the selected profile and immediately attempts to apply it to the selected installation. You can also choose **Apply profile** or **Launch Roblox**. If Roblox is already running, restart it to load the saved flags. Applying before launch is enabled by default.
4. Use **Recovery → Restore previous values** to undo N strap's managed changes.

Edits save automatically. File import enables imported entries and replaces values for matching names; other profile entries are retained. The whole file is validated before importing. If automatic application cannot finish, the imported profile stays saved and the app reports what needs attention.

Exported JSON includes enabled flags. **Replace profile** inside the JSON editor replaces the profile's entries and enables the imported flags. Presets merge into the selected profile when chosen.

In the JSON editor, press **Ctrl+F** to find text. Search ignores letter case and shows the match count. With the search box focused, use **Enter** for the next match and **Shift+Enter** for the previous match. **Escape** closes the search bar.

**Unlock FPS** is on Overview and controls Roblox Player's global framerate setting independently of FastFlag profiles. It sets a high cap of 9,999 FPS; it cannot guarantee a particular framerate or override graphics-driver limits. If Player is running, the change waits until it closes. Keep N strap open to apply the pending change, or reopen N strap before launching Roblox. Turning the toggle off restores the recorded previous cap while preserving other Roblox settings. If the cap has since been changed elsewhere, that newer value is kept instead.

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
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o artifacts/Nstrap-win-x64
```

Run `artifacts/Nstrap-win-x64/Nstrap.exe`. A self-contained publish includes the .NET runtime.

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

Roblox loads these settings at startup. Applying or restoring flags saves the settings file; restart an already-running client to load the changes. Profiles and recovery information are stored locally. Removing a managed flag restores its recorded original value; conflicting external changes are reported rather than silently replaced.

App data lives in `%LOCALAPPDATA%\Nstrap`: `profiles.json` stores profiles, `backups` holds previous settings files, and `recovery` holds ownership and interrupted-operation records. Keep the recovery records if you want N strap to restore values it previously changed.

Unlock FPS changes the existing `FramerateCap` property in `%LOCALAPPDATA%\Roblox\GlobalBasicSettings_13.xml`. Its settings-file backups are stored in `backups\player-fps`, with ownership and interrupted-update records in `recovery`.

For an isolated UI preview, pass `--data-dir <folder> --smoke-screenshot <output.png>`. The preview checks import and apply using a temporary client fixture inside that data folder, writes screenshots, and exits. It does not change installed Roblox clients or launch Roblox. Add `--smoke-import-file <path>` to check a flag file against the same fixture.

N strap is an independent personal project and is not affiliated with Roblox.
