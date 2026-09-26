---
name: setup-greenshot
description: Build Greenshot and greenshot-cli from source on Windows, and configure the Greenshot app (hotkeys, output, greenshot.ini). Use when greenshot-cli.exe or Greenshot.exe is missing, needs rebuilding after source changes, or the user wants to change Greenshot settings or shortcuts.
---

# Build and configure Greenshot

## Build from source

Requirements:

- Windows with .NET Framework 4.8 or newer (preinstalled on Windows 10/11)
- .NET SDK 9.0.311 or newer 9.0.3xx, as pinned in `src/global.json` (`winget install Microsoft.DotNet.SDK.9`). Check with `dotnet --list-sdks`. Do not edit `global.json` to use an older SDK.

```bash
git clone https://github.com/santimorenodi/greenshot.git
cd greenshot
dotnet build src/Greenshot.sln -c Release
```

- Success = `0 Errores` / `0 Error(s)`. Warnings are expected.
- Output: `src/Greenshot/bin/Release/net480/` with `Greenshot.exe`, `greenshot-cli.exe` and the plugins.
- Use `dotnet build`, not Visual Studio 2022's MSBuild: older VS MSBuild versions cannot resolve the pinned SDK (`Microsoft.NET.Sdk.WindowsDesktop` not found).
- Cloud plugins (Box, Dropbox, Imgur) build with empty API credentials unless the `*_ClientId` / `*_ClientSecret` environment variables are set. That only matters for uploading.

Optionally add the output folder to `PATH` or set `GREENSHOT_CLI` to the full path of
`greenshot-cli.exe` so the `take-screenshot` skill finds it.

## Greenshot app hotkeys

Defaults in this fork:

| Shortcut | Action |
|---|---|
| PrintScreen | capture region |
| Alt + PrintScreen | capture window |
| Ctrl + Alt + PrintScreen | capture full screen (moved from Ctrl + PrintScreen, which wcap uses) |
| Shift + PrintScreen | capture last region |

Greenshot listens with a low-level keyboard hook, so probing with `RegisterHotKey` does not show
its shortcuts as taken. To test for conflicts, simulate the key presses with both apps running and see
which one reacts.

On Windows 11, PrintScreen may open the Snipping Tool instead. That is controlled by
`HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled` (0 = off), also in Settings >
Accessibility > Keyboard. Ask the user before changing it.

## Configuration file

Greenshot reads `%APPDATA%\Greenshot\Greenshot.ini` (or `greenshot.ini` next to the exe in portable
mode). Values in the file override the built-in defaults, so an old install can keep old hotkeys.
Hotkeys are in section `[Core]`:

```ini
RegionHotkey=PrintScreen
WindowHotkey=Alt + PrintScreen
FullscreenHotkey=Ctrl + Alt + PrintScreen
LastregionHotkey=Shift + PrintScreen
```

Other useful `[Core]` keys: `OutputFilePath`, `OutputFileFilenamePattern`, `OutputFileFormat`,
`Destinations`.

When editing it by hand:

- Close Greenshot first (`Greenshot.exe --exit`), because it rewrites the file while running.
- Back up the file and keep its encoding (UTF-8 with BOM) and CRLF line endings.
- `Greenshot.exe --reload` makes a running instance reload the configuration.
