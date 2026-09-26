---
name: take-screenshot
description: Take a screenshot of the whole screen, a monitor, the active window, a specific window or a region on Windows with greenshot-cli, save it as png/jpg/bmp/gif/tiff and look at it. Use when the user asks for a screenshot or capture of their screen or an app, or when seeing the current state of a desktop UI would help verify or debug something.
---

# Take screenshots with greenshot-cli

`greenshot-cli` is a console tool from [this Greenshot fork](https://github.com/santimorenodi/greenshot)
that uses Greenshot's capture engine without any UI. It uses default settings in memory (it never
reads or writes `greenshot.ini`), so it can run while `Greenshot.exe` is open.

## 1. Find the executable

Look for it in this order and use the first hit:

1. `GREENSHOT_CLI` environment variable (full path to the exe)
2. `greenshot-cli.exe` on `PATH`
3. `src\Greenshot\bin\Release\net480\greenshot-cli.exe` in a local checkout of the repo (next to `Greenshot.exe`)

```powershell
if ($env:GREENSHOT_CLI) { $env:GREENSHOT_CLI } else { (Get-Command greenshot-cli.exe -ErrorAction SilentlyContinue).Source }
```

If it is not found, ask the user where it is, or build it with the `setup-greenshot` skill.

## 2. Pick the target

```bash
greenshot-cli.exe list
```

Prints monitors (`monitor N: WxH at X,Y \\.\DISPLAYn (primary)`) and top-level windows
(`window 0xHANDLE: WxH pid=N [minimized] "title"`). Choose one target:

| Target | Meaning |
|---|---|
| `--fullscreen` *(default)* | all monitors in one image |
| `--monitor N` | one monitor, index from `list` |
| `--active` | current foreground window |
| `--window 0x1234ab` | exact window handle from `list` (preferred, unambiguous) |
| `--window "Chrome"` | first window whose title contains the text (case-insensitive, warns if several match) |
| `--region X,Y,W,H` | rectangle in virtual screen coordinates (physical pixels) |

## 3. Capture

Always pass `-o` with an absolute path.

```bash
greenshot-cli.exe capture --window 0x1234ab -o "C:\path\shot.png"
```

Options:

- `-o, --output FILE` output file, format taken from the extension (default: `greenshot_<timestamp>.png` in the current folder)
- `--format F` force `png`, `jpg`, `bmp`, `gif` or `tiff`
- `--quality N` jpg quality 1-100 (default 80)
- `--delay SEC` wait before capturing, e.g. `1.5` (useful to let a menu or animation settle)
- `--mode M` window capture mode: `auto` (default), `aero`, `aerotransparent`, `gdi`, `screen`
- `--clipboard` also copy the image to the clipboard
- `--open` open the result in the Greenshot editor so the user can annotate it

To draw on it (arrows, text, steps, blur, crop...) add annotation arguments to `capture` or use
`greenshot-cli edit`, see the `annotate-screenshot` skill.

Output on stdout, exit code 0 on success:

```
saved: C:\path\shot.png
size: 1920x1027
```

Errors go to stderr as `error: ...` with exit code 1.

## 4. Look at it

Open the saved file with the Read tool to see the image. Large multi-monitor captures are big; prefer
`--monitor N`, `--window` or `--region` for what you actually need, or downscale first if ffmpeg is
available:

```bash
ffmpeg -v error -y -i shot.png -vf scale=1280:-1 shot_small.png
```

## Notes

- Window capture may bring the window to the foreground (GDI mode) to capture it correctly. Use `--mode screen` to capture exactly what is visible without touching the window.
- Minimized windows are restored to be captured.
- Screenshots may contain private information. Save them where the user expects, describe only what is relevant, and do not upload them anywhere unless asked.
- For video instead of a still image, use the `wcap` plugin's `record-screen` skill if it is installed.
