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
`--json` (on `capture`, `edit`, `combine`, `diff`) prints one line of JSON with `path`, `width`, `height`.

## Workflow

1. Capture (or take the given image) and **look at it with the Read tool first**. Coordinates are
   image pixels, top-left is 0,0; the stdout line `size: WxH` tells you the size. If the Read tool shows
   the image scaled down, multiply what you see by the scale factor it reports.
2. Write the annotations. They are applied **in the order given**, so crop first if you crop: after
   `--crop` the coordinates of the following elements are relative to the cropped image.
3. Read the result and fix positions if something is off. Iterate on a copy, not the original.

## Place annotations without guessing

Do not place rectangles by eye on a scaled preview. Make a copy with a grid and the coordinates written on it:

```bash
greenshot-cli.exe edit "C:\tmp\raw.png" --grid 100 -o "C:\tmp\grid.png"
```

Read the coordinates off `grid.png` (lines every 100 px, numbers on the edges and, from a step of 100, on every
crossing), then annotate `raw.png` (the grid is only a copy, the original is never touched; without `-o` it writes
`raw.grid.png`). For a preview of the result use `--preview "C:\tmp\small.png" --preview-max 1400`, the saved image
keeps its full resolution.

For things at the edges of the image, use `--anchor` instead of measuring. It is sticky like the style, applies to
the X,Y of the elements that follow and means "distance from that edge or corner (from the center for `center`) to
the same edge or corner of the element":

```bash
# label at the bottom left, 10 px from the edges, whatever the size of the image or of the text
greenshot-cli.exe edit raw.png -o out.png --font-size 20 --anchor bottom-left --text 10,10 "Before"
# 300x40 box centered at the top, 12 px from the top edge
greenshot-cli.exe edit raw.png -o out.png --anchor top --rect 0,12,300,40
```

`top-left` (default), `top`, `top-right`, `left`, `center`, `right`, `bottom-left`, `bottom`, `bottom-right`.

A label glued to a shape: `--label "text"` right after `--rect`, `--ellipse`, `--line`, `--arrow`, `--text` or `--step`
puts a text above its top left corner, filled with the color of the shape (inside when there is no room above;
`--label-pos above|below|inside`):

```bash
greenshot-cli.exe edit raw.png -o out.png --color "#E53935" --thickness 3 --rect 100,150,200,100 --label "Broken"
```

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

## Before and after side by side

```bash
greenshot-cli.exe combine before.png after.png -o "C:\tmp\both.png" --hstack --width 800 --labels "Before" "After"
```

`--hstack` (default), `--vstack` or `--grid COLUMNS`; `--gap N` (default 8), `--gap-color C`, `--width N` scales every
image to that width keeping its proportions (one `--labels` text per image, drawn in its top left corner as Greenshot text
with the style arguments `--font-size`, `--color`, `--fill`, `--bold`, ... given anywhere; white bold on dark by default).
The result is an ordinary image: annotate it with `edit` afterwards. `--preview`, `--json` and annotations work
on `combine` too. No ffmpeg needed.

## What changed between two images

```bash
greenshot-cli.exe diff before.png after.png -o "C:\tmp\marked.png" --threshold 30 --json
# {"path":"...","width":1280,"height":720,"changed_pixels":5321,"changed_percent":0.5773,"rects":[{"x":100,"y":80,"width":62,"height":42}]}
```

Draws Greenshot rectangles (red, 3 px; `--color`, `--thickness` change that) around the areas that changed on
`after.png`. `--threshold N` (0-255, default 24) how much a pixel has to change, `--min-area N` (default 64) ignores
small areas, `--merge N` (default 12) joins areas closer than N pixels. The two images need the same size, otherwise it
is an error. With `--json` the rectangles are the list to use in the next `edit` (for example `--rect`).

## Notes

- Keep annotations readable at the size the image will be seen: thickness 3-5 and font size 18-28 on
  full-HD captures, less on small crops.
- Hide private data with `--pixelate` or `--blur` before sharing, and tell the user what you hid.
- Errors are `error: ...` on stderr with exit code 1 and name the argument that was wrong.
