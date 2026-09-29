---
name: screenshot
description: Take and annotate screenshots on Windows with greenshot-cli (Greenshot capture engine and editor, no UI). Capture the screen, a monitor, a window (also behind other windows, without taking the focus), a part of a window, menus, minimized windows; annotate with arrows, rectangles, text, bubbles, numbered steps, blur/pixelate, crop, effects; combine before/after images and mark the differences. Use when the user asks for a screenshot, wants a UI seen, marked up, redacted, compared or prepared for docs or a bug report.
---

# Screenshots with greenshot-cli

Console tool of [this Greenshot fork](https://github.com/santimorenodi/greenshot). Default settings in memory, never touches
`greenshot.ini`, runs while `Greenshot.exe` is open. Find the exe: `GREENSHOT_CLI` env var, else `greenshot-cli.exe` on
`PATH`, else `src\Greenshot\bin\Release\net480\greenshot-cli.exe` of a checkout; if missing use the `setup-greenshot` skill.
`greenshot-cli.exe help` lists every option with an example. Errors: `error: ...` on stderr, exit code 1.

**All coordinates are physical pixels** (per-monitor DPI aware): no scaling math for 150 % displays, and `list` and `capture` agree.

## 1. Choose the window

`list --json`: `monitors` and `windows` (hwnd, title, pid, exe, rect = visible frame, client = area without frame/title bar,
minimized, visible), rectangles in screen pixels. Plain `list` is the same as text.

| Target | Meaning |
|---|---|
| `--fullscreen` (default) / `--monitor N` / `--active` | all monitors / one monitor / foreground window |
| `--window 0x1a2b` or `"title"` | handle (unambiguous) / title substring (several matches: warning and first one) |
| `--window-pid N`, `--window-exe "D:\Godlike\*"` | by process; combinable with `--window`; several matches are an **error that lists them**, never a silent pick |
| `--region X,Y,W,H` | screen rectangle; **with a window target it is relative to the window** (to its client area with `--client`) |

Two windows with the same title (two Unreal editors): tell them apart with `--window-exe` or `--window-pid`.

## 2. Capture

```bash
greenshot-cli.exe capture --window-pid 49656 --no-activate --mode gdi --client --region 0,0,1280,720 -o "C:\tmp\a.png" --json
```

`-o FILE` (format from extension; always give an absolute path), `--format`, `--quality N`, `--delay SEC`, `--clipboard`, `--open`,
`--client` (client area only), `--mode auto|aero|aerotransparent|gdi|screen`.
`--json`: one line with `path`, `width`, `height`, `hwnd`, `pid`, `rect` (what was captured, screen pixels), `mode`; without it
stdout is `saved: PATH` and `size: WxH`. `--preview FILE` with `--preview-width N` or `--preview-max N` also writes a smaller copy
to look at (never bigger than the original), no ffmpeg needed. Open images with the Read tool.

**Do not disturb the user** (they keep working in other windows):
- `--no-activate` guarantees no activation, no raise, no mouse move; a minimized window is an error instead of being restored.
  `gdi`, `aero`, `aerotransparent` and `auto` work with the window behind others. `screen` copies the screen, so the window must be
  visible (stderr names the windows covering it). Without `--no-activate`, windows are raised as Greenshot always did: use it.
- `aero` (also `auto` on Windows 11) flashes a topmost copy of the window for a moment. For several captures in a row, or while the
  user works, use `--mode gdi` (shows nothing) and take only the captures you need.
- `--include-popups`: also draws menus, tooltips and open combo lists of the same process at their real position (image grows if they
  stick out); implies `--no-activate` because menus close when their window loses focus.
- `--restore-behind [--settle MS]` (experimental): for a minimized window: restores it without activating, at the bottom of the z-order,
  waits `--settle` (500; Unreal 1500+), captures with PrintWindow, minimizes it again. DirectX windows can come out black or old
  (Unreal worked in tests, check the image).

## 3. Annotate

Same arguments on `capture` and on `edit IN [-o OUT]` (png, jpg, bmp, gif, tiff, `.greenshot`; without `-o` it overwrites IN).
Applied **in the order given** (after `--crop`, later coordinates refer to the cropped image). Look at the image first.

Place things without guessing: `edit raw.png --grid 100 -o grid.png` writes a copy with a grid and coordinates (raw untouched);
read positions off it. `--anchor top-left|top|top-right|left|center|right|bottom-left|bottom|bottom-right` (sticky) makes the
X,Y that follow a distance from that edge/corner (from the center for `center`) to the same edge/corner of the element, e.g.
`--anchor bottom-left --text 10,10 "Before"`.

| Element | Draws |
|---|---|
| `--rect` / `--ellipse X,Y,W,H` | shape |
| `--line` / `--arrow X1,Y1,X2,Y2` | line / arrow with head at X2,Y2 |
| `--freehand "X,Y;X,Y;..."` | path |
| `--text X,Y,W,H "t"` / `--text X,Y "t"` | text box / sized to fit |
| `--bubble X,Y,W,H,TX,TY "t"` | speech bubble, tail at TX,TY |
| `--step X,Y[,SIZE]` | numbered circle centered at X,Y (1, 2, 3... in order) |
| `--highlight` / `--spotlight` / `--grayscale-area` / `--magnify X,Y,W,H` | marker / dim the rest / gray the rest / magnifier |
| `--pixelate` / `--blur X,Y,W,H` | hide private data (say what you hid) |
| `--crop X,Y,W,H` | crop, elements move along |
| `--label "t"` | right after a shape: text above its top-left corner filled with the shape's color (`--label-pos above\|below\|inside`) |

Style (sticky, for the shapes and text after it): `--color C` (name, `#RRGGBB`, `#AARRGGBB`), `--fill C`, `--thickness N`
(text/bubble: border, 0 none), `--font NAME`, `--font-size N`, `--bold/--no-bold`, `--italic/--no-italic`, `--shadow/--no-shadow`,
`--heads end|start|both|none`, `--pixel-size N`, `--blur-radius N`, `--magnification N`. Thickness 3-5 and font 18-28 on full-HD.
Effects on the final image: `--border`, `--drop-shadow`, `--torn-edge`, `--grayscale`, `--invert`, `--rotate 90|180|270`,
`--resize W,H` (0 keeps ratio), `--scale PERCENT`. Output `.greenshot` keeps every element editable (no effects there).

```bash
greenshot-cli.exe edit raw.png -o howto.png --crop 0,0,1280,760 --pixelate 860,20,300,32 \
  --color "#E53935" --thickness 4 --rect 40,120,420,64 --label "Broken" --arrow 700,420,560,300 --step 700,430 \
  --thickness 0 --color white --fill "#E53935" --font-size 20 --bold --bubble 760,460,300,70,700,430 "Then press Save" --drop-shadow
```

## 4. Compare

```bash
greenshot-cli.exe combine before.png after.png -o both.png --hstack --width 800 --labels "Before" "After"
greenshot-cli.exe diff before.png after.png -o marked.png --threshold 30 --json
```

`combine`: `--hstack` (default) / `--vstack` / `--grid COLUMNS`, `--gap N` (8), `--gap-color C`, `--width N` per image, one `--labels` text per
image (style args apply); the result can be annotated with `edit`. `diff` (same size required): Greenshot rectangles (red, 3 px) on
`after.png` around what changed; `--threshold N` (0-255, 24), `--min-area N` (64), `--merge N` (12, in 8 px blocks); `--json` lists
`rects` and `changed_percent`. Both accept `--preview`, `--json`, annotation style args.

Screenshots may show private data: describe only what is relevant, do not upload anywhere unless asked. For video use the `wcap`
plugin's `record-screen` skill if installed.
