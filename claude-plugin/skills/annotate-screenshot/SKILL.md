---
name: annotate-screenshot
description: Annotate a screenshot or any image on Windows with greenshot-cli, the same way the Greenshot editor does - arrows, rectangles, ellipses, lines, freehand, text, speech bubbles, numbered steps, text marker, spotlight, magnifier, pixelate/blur to hide private data, crop, border, drop shadow, torn edge, resize. Use when the user wants a screenshot marked up, highlighted, explained step by step, redacted, cropped, or prepared for docs, a bug report or a tutorial.
---

# Annotate images with greenshot-cli

`greenshot-cli` (from [this Greenshot fork](https://github.com/santimorenodi/greenshot)) draws with the
real Greenshot editor, headless: every element is created as if dragged with the mouse, so the result
looks exactly like annotating in the Greenshot editor. Find the exe as in the `take-screenshot` skill.

Two ways in:

```bash
greenshot-cli.exe capture --window 0x1234ab [annotations] -o "C:\path\shot.png"   # capture and annotate
greenshot-cli.exe edit "C:\path\in.png" [annotations] -o "C:\path\out.png"          # annotate an existing image
```

`edit` reads png, jpg, bmp, gif, tiff and `.greenshot`; without `-o` it overwrites the input.

## Workflow

1. Capture (or take the given image) and **look at it with the Read tool first**. Coordinates are
   image pixels, top-left is 0,0; the stdout line `size: WxH` tells you the size. If the Read tool shows
   the image scaled down, multiply what you see by the scale factor it reports.
2. Write the annotations. They are applied **in the order given**, so crop first if you crop: after
   `--crop` the coordinates of the following elements are relative to the cropped image.
3. Read the result and fix positions if something is off. Iterate on a copy, not the original.

## Elements

| Argument | Draws |
|---|---|
| `--rect X,Y,W,H` / `--ellipse X,Y,W,H` | rectangle / ellipse |
| `--line X1,Y1,X2,Y2` / `--arrow X1,Y1,X2,Y2` | line / arrow, the head at X2,Y2 |
| `--freehand "X,Y;X,Y;..."` | freehand path through the points |
| `--text X,Y,W,H "text"` / `--text X,Y "text"` | text in a box / box sized to fit |
| `--bubble X,Y,W,H,TX,TY "text"` | speech bubble whose tail points at TX,TY |
| `--step X,Y[,SIZE]` | numbered circle centered at X,Y (SIZE px wide, default the editor click size), numbered 1, 2, 3... in order |
| `--highlight X,Y,W,H` | text marker (yellow) |
| `--spotlight X,Y,W,H` | keeps the area sharp and blurs/darkens everything else |
| `--grayscale-area X,Y,W,H` | keeps the area in color, everything else gray |
| `--magnify X,Y,W,H` | magnifier over the area |
| `--pixelate X,Y,W,H` / `--blur X,Y,W,H` | hide the area (passwords, emails, names...) |
| `--crop X,Y,W,H` | crop the image, elements move along |

## Style

Style arguments are sticky: they apply to every shape and text **after** them, until changed.
Filters (highlight, spotlight, magnify, pixelate, blur) ignore them and keep their borderless look.

- `--color C` line and text color: a name (`red`), `#RRGGBB` or `#AARRGGBB`
- `--fill C` fill color, `transparent` by default (`#CC1E88E5` = semi-transparent blue)
- `--thickness N` line thickness; for text and bubbles it is the border, `0` for none
- `--font NAME`, `--font-size N`, `--bold` / `--no-bold`, `--italic` / `--no-italic`
- `--shadow` / `--no-shadow` element shadow
- `--heads end|start|both|none` arrow heads
- `--pixel-size N`, `--blur-radius N`, `--magnification N` filter strength

Defaults are Greenshot's: red lines 2px with shadow, step labels dark red.

## Effects on the final image

Applied after the elements, in order: `--border`, `--drop-shadow`, `--torn-edge`, `--grayscale`,
`--invert`, `--rotate 90|180|270`, `--resize W,H` (0 in one of them keeps the aspect ratio),
`--scale PERCENT`.

## Keep it editable

Output to `.greenshot` (`-o shot.greenshot`) keeps every element editable: the user can open it in
Greenshot and move or change anything, and `edit shot.greenshot ...` adds more elements later.
Effects can't be stored there; export to png/jpg for those. `--open` opens the result in the editor.

## Example

Explain a two-step flow in a window, hiding an email address:

```bash
greenshot-cli.exe capture --window 0x1234ab -o "C:\tmp\raw.png"
greenshot-cli.exe edit "C:\tmp\raw.png" -o "C:\tmp\howto.png" \
  --crop 0,0,1280,760 \
  --pixelate 860,20,300,32 \
  --color "#E53935" --thickness 4 --rect 40,120,420,64 --step 30,120 \
  --arrow 700,420,560,300 --step 700,430 \
  --thickness 0 --color white --fill "#E53935" --font-size 20 --bold \
  --bubble 760,460,300,70,700,430 "Then press Save" \
  --drop-shadow
```

## Notes

- Keep annotations readable at the size the image will be seen: thickness 3-5 and font size 18-28 on
  full-HD captures, less on small crops.
- Hide private data with `--pixelate` or `--blur` before sharing, and tell the user what you hid.
- Errors are `error: ...` on stderr with exit code 1 and name the argument that was wrong.
