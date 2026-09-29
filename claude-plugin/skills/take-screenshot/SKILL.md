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
greenshot-cli.exe list --json
```

`list --json` prints one JSON object with `monitors` (index, x, y, width, height, device, primary) and
`windows` (hwnd, title, pid, exe, rect, client, minimized, visible). Rectangles are **physical screen
pixels** (the tool is per-monitor DPI aware, so nothing has to be scaled by the 150 % display scaling, and
the numbers of `list` are the ones `capture` uses). `rect` is the visible frame, `client` the area
without frame and title bar. Plain `list` prints the same as text.

Several windows can have the same title (two copies of the Unreal editor). Tell them apart by process:

```bash
greenshot-cli.exe list --json      # look at "pid" and "exe" of the windows
greenshot-cli.exe capture --window-exe "D:\Godlike\*" --window "Unreal Editor" -o "C:\tmp\a.png"
greenshot-cli.exe capture --window-pid 49656 -o "C:\tmp\a.png"
```

| Target | Meaning |
|---|---|
| `--fullscreen` *(default)* | all monitors in one image |
| `--monitor N` | one monitor, index from `list` |
| `--active` | current foreground window |
| `--window 0x1234ab` | exact window handle from `list` (preferred, unambiguous) |
| `--window "Chrome"` | first window whose title contains the text (case-insensitive, warns if several match) |
| `--window-pid N` | the window of the process with this id |
| `--window-exe "D:\Godlike\*"` | the window of a process whose exe matches (case-insensitive, `*` `?` wildcards; a pattern with a path matches the full path, otherwise the file name) |
| `--region X,Y,W,H` | rectangle in virtual screen coordinates (physical pixels) |

`--window`, `--window-pid` and `--window-exe` can be combined. When `--window-pid` or `--window-exe` is
used and several windows match, the tool fails and lists them (0x handle, pid, title, exe): it never
picks one silently. Add the pid or a longer title, or use the handle.

**Part of a window:** with a window target, `--region X,Y,W,H` is relative to the window (to its client area
if you add `--client`), and is cut out after capturing the window. No screen coordinate or DPI math needed:

```bash
greenshot-cli.exe capture --window-pid 49656 --client --region 0,0,1280,720 -o "C:\tmp\viewport.png"
```

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
- `--client` only the client area of the window, without frame and title bar
- `--no-activate` never take the focus (see below)
- `--include-popups` also draw menus, tooltips and open combo lists of the window (see below)
- `--restore-behind [--settle MS]` experimental, capture a minimized window (see below)
- `--json` one line of JSON instead of `saved:`/`size:`: `path`, `width`, `height`, `hwnd`, `pid`, `rect` (what was captured, screen pixels), `mode`
- `--preview FILE` with `--preview-width N` or `--preview-max N` also saves a smaller copy to look at; the original keeps its full resolution

To draw on it (arrows, text, steps, blur, crop...) add annotation arguments to `capture` or use
`greenshot-cli edit`, see the `annotate-screenshot` skill.

Output on stdout, exit code 0 on success:

```
saved: C:\path\shot.png
size: 1920x1027
```

For scripts use `--json` (one line, errors stay on stderr with exit code 1):

```bash
greenshot-cli.exe capture --window-pid 49656 --client --json -o "C:\tmp\a.png"
# {"path":"C:\\tmp\\a.png","width":3840,"height":2088,"hwnd":"0x4b0b5e","pid":49656,"rect":{"x":0,"y":0,"width":3840,"height":2088},"mode":"aero"}
```

Errors go to stderr as `error: ...` with exit code 1.

## 4. Look at it

Open the saved file with the Read tool to see the image. Large captures are big: ask for a preview
instead of running ffmpeg, the original stays at full resolution:

```bash
greenshot-cli.exe capture --window-pid 49656 -o "C:\tmp\full.png" --preview "C:\tmp\small.png" --preview-max 1400
```

Prefer `--monitor N`, `--window` or `--region` for what you actually need.

## 5. Do not disturb the user

The user keeps working in other windows while you capture.

- **`--no-activate`** guarantees that the capture does not activate the window, does not bring it to the front
  and does not move the mouse. `aero`, `aerotransparent`, `gdi` and `auto` then work with the window behind other
  windows (`aero` puts a short-lived topmost copy on screen without taking the focus). `--mode screen` copies the
  pixels that are on the screen, so the window has to be visible: the tool warns on stderr which window is in front of
  it (`is fully covered by "Slack"`). A minimized window fails with a clear error instead of being restored, unless
  you also pass `--restore-behind`.
- Without `--no-activate`, `aero`/`aerotransparent`/`gdi` and `screen` do bring the window to the front (that is how
  Greenshot always worked), and minimized windows are restored and activated. As an agent, use `--no-activate`.

```bash
greenshot-cli.exe capture --window-pid 49656 --no-activate --mode aero -o "C:\tmp\a.png"
```

`aero` (and so `auto` on Windows 11) briefly shows a topmost copy of the window on the screen to copy it: no focus
is taken, but it flashes, which is annoying for the user if you take several captures in a row. For series of
captures, or when the user is working, use `--mode gdi` (PrintWindow, shows nothing) and take only the captures you need.

Checked with the Unreal editor (`aero` and `gdi` give the whole window with the viewport, also behind other applications)
and with Edge.

## 6. Menus and dropdowns

Menus, tooltips and combo lists (also Unreal's Slate menus) are separate windows, so a normal window capture does not
have them, and `--mode screen` shows whatever is in front. Open the menu (with the user, or with `--delay`), then:

```bash
greenshot-cli.exe capture --window-pid 49656 --include-popups -o "C:\tmp\menu.png"
```

Windows of the same process that overlap the window, are in front of it and look like popups (no title bar) are drawn on
top at their real position; the image grows if a popup sticks out. It implies `--no-activate`, because menus close when
their window loses the focus.

## 7. Minimized windows (experimental)

A minimized window has no current image. `--restore-behind` restores it without activating it, sends it behind
everything else, waits for it to paint (`--settle`, 500 ms; give Unreal 1500 or more), captures it with
PrintWindow and minimizes it again, without changing the focus:

```bash
greenshot-cli.exe capture --window "Unreal Editor" --restore-behind --settle 1500 -o "C:\tmp\a.png"
```

Verified with a normal window and with Edge. Windows that draw with DirectX (games, Unreal viewports) can come out
black or with an old frame when captured with PrintWindow: not verified on a minimized Unreal window; check the image
and raise `--settle` if it is wrong. The window flashes on screen behind the others for a moment.

## Notes

- Screenshots may contain private information. Save them where the user expects, describe only what is relevant, and do not upload them anywhere unless asked.
- For video instead of a still image, use the `wcap` plugin's `record-screen` skill if it is installed.
