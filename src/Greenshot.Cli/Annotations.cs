/*
 * Greenshot - a free and open source screenshot tool
 * Copyright (C) 2007-2026 Thomas Braun, Jens Klingen, Robin Krom
 *
 * For more information see: https://getgreenshot.org/
 * The Greenshot project is hosted on GitHub https://github.com/greenshot/greenshot
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 1 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using Dapplo.Windows.Common.Structs;
using Greenshot.Base.Core;
using Greenshot.Base.Effects;
using Greenshot.Base.Interfaces;
using Greenshot.Base.Interfaces.Drawing;
using Greenshot.Editor.Drawing;
using Greenshot.Editor.Drawing.Fields;
using static Greenshot.Editor.Drawing.ArrowContainer;
using static Greenshot.Editor.Drawing.FilterContainer;

namespace Greenshot.Cli;

/// <summary>
/// Editor operations (shapes, text, obfuscation, crop, effects) given on the command line. They are applied in the order
/// they are given, on a headless Greenshot editor surface: every element is drawn the same way the editor draws it when
/// dragging the mouse, so the result looks exactly like annotating in the editor.
/// </summary>
internal sealed class Annotations
{
    public const string Usage =
        "annotations (capture and edit, applied in the order given, coordinates in image pixels):\n" +
        "  --rect X,Y,W,H              rectangle            --ellipse X,Y,W,H       ellipse\n" +
        "  --line X1,Y1,X2,Y2          line                 --arrow X1,Y1,X2,Y2     arrow (head at X2,Y2)\n" +
        "  --freehand X,Y;X,Y;...      freehand path\n" +
        "  --text X,Y,W,H TEXT         text box             --text X,Y TEXT         text box sized to fit\n" +
        "  --bubble X,Y,W,H,TX,TY TEXT speech bubble pointing at TX,TY\n" +
        "  --step X,Y[,SIZE]           numbered step label centered at X,Y (1, 2, 3... in order)\n" +
        "  --highlight X,Y,W,H         text marker          --spotlight X,Y,W,H     darken everything else\n" +
        "  --grayscale-area X,Y,W,H    grayscale the rest   --magnify X,Y,W,H       magnifier\n" +
        "  --pixelate X,Y,W,H          pixelate area        --blur X,Y,W,H          blur area\n" +
        "  --crop X,Y,W,H              crop the image (elements move along)\n" +
        "  --label TEXT                label glued to the shape right before it (above its top left corner, filled with the\n" +
        "                              color of the shape), e.g. --color red --rect 40,60,200,80 --label \"Broken\"\n" +
        "\n" +
        "style (applies to the shapes and text that follow it, not to highlight/magnify/pixelate/blur):\n" +
        "  --color C                   line/text color: name (red), #RRGGBB or #AARRGGBB\n" +
        "  --fill C                    fill color (transparent by default)\n" +
        "  --thickness N               line thickness (for text and bubbles: the border, 0 for none)\n" +
        "  --font NAME, --font-size N, --bold, --italic, --no-bold, --no-italic\n" +
        "  --shadow, --no-shadow       element shadow\n" +
        "  --heads end|start|both|none arrow heads\n" +
        "  --pixel-size N, --blur-radius N, --magnification N\n" +
        "  --label-pos above|below|inside   where --label goes (default above, inside when there is no room)\n" +
        "  --anchor A                  what the X,Y of the following elements are relative to: top-left (default), top,\n" +
        "                              top-right, left, center, right, bottom-left, bottom, bottom-right. X,Y is the distance\n" +
        "                              from that edge/corner (from the center for center) to the same edge/corner of the\n" +
        "                              element, e.g. --anchor bottom-left --text 10,10 \"Before\" puts the text 10 pixels from\n" +
        "                              the left and bottom edge of the image, without measuring anything\n" +
        "\n" +
        "effects on the final image (in the order given, not with a .greenshot output):\n" +
        "  --border, --drop-shadow, --torn-edge, --grayscale, --invert\n" +
        "  --rotate 90|180|270, --resize W,H (keeps aspect ratio if W or H is 0), --scale PERCENT\n" +
        "\n" +
        "output .greenshot keeps every element editable in the Greenshot editor.\n";

    private enum Anchor
    {
        TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight
    }

    private enum LabelPosition
    {
        Above, Below, Inside
    }

    /// <summary>
    /// The last shape that was added, filled in when the shape is drawn, so --label can stick to it
    /// </summary>
    private sealed class ShapeRef
    {
        public Rectangle Rect;
        public Color Color = Color.Red;
    }

    private readonly List<Action<Surface>> _operations = new();
    private readonly List<Func<Image, IEffect>> _effects = new();

    // Current style, null means "the editor default for that element"
    private Color? _color;
    private Color? _fill;
    private int? _thickness;
    private string _font;
    private float? _fontSize;
    private bool? _bold;
    private bool? _italic;
    private bool? _shadow;
    private ArrowHeadCombination? _heads;
    private int? _pixelSize;
    private int? _blurRadius;
    private int? _magnification;
    private Anchor _anchor = Anchor.TopLeft;
    private LabelPosition _labelPosition = LabelPosition.Above;
    private ShapeRef _lastShape;

    public bool HasOperations => _operations.Count > 0 || _effects.Count > 0;
    public bool HasEffects => _effects.Count > 0;

    /// <summary>
    /// Try to consume the argument at index i (and its values). Returns false if it is not an annotation argument.
    /// </summary>
    public bool TryParse(string[] args, ref int i)
    {
        string arg = args[i];
        int index = i;
        string Next(string what)
        {
            if (index + 1 >= args.Length)
            {
                throw new CliException($"{arg} requires {what}");
            }
            return args[++index];
        }

        switch (arg)
        {
            // shapes
            case "--rect":
                AddDrag(Rect(Next("X,Y,W,H")), s => new RectangleContainer(s));
                break;
            case "--ellipse":
                AddDrag(Rect(Next("X,Y,W,H")), s => new EllipseContainer(s));
                break;
            case "--line":
                AddLine(Next("X1,Y1,X2,Y2"), s => new LineContainer(s));
                break;
            case "--arrow":
                AddLine(Next("X1,Y1,X2,Y2"), s => new ArrowContainer(s));
                break;
            case "--freehand":
                AddFreehand(Next("X,Y;X,Y;..."));
                break;
            case "--text":
                AddText(Next("X,Y[,W,H]"), Next("the text"));
                break;
            case "--bubble":
                AddBubble(Next("X,Y,W,H,TX,TY"), Next("the text"));
                break;
            case "--step":
                AddStep(Next("X,Y"));
                break;

            // filters
            case "--highlight":
                AddFilter(Next("X,Y,W,H"), s => new HighlightContainer(s), FieldType.PREPARED_FILTER_HIGHLIGHT, PreparedFilter.TEXT_HIGHTLIGHT);
                break;
            case "--spotlight":
                AddFilter(Next("X,Y,W,H"), s => new HighlightContainer(s), FieldType.PREPARED_FILTER_HIGHLIGHT, PreparedFilter.AREA_HIGHLIGHT);
                break;
            case "--grayscale-area":
                AddFilter(Next("X,Y,W,H"), s => new HighlightContainer(s), FieldType.PREPARED_FILTER_HIGHLIGHT, PreparedFilter.GRAYSCALE);
                break;
            case "--magnify":
                AddFilter(Next("X,Y,W,H"), s => new HighlightContainer(s), FieldType.PREPARED_FILTER_HIGHLIGHT, PreparedFilter.MAGNIFICATION);
                break;
            case "--pixelate":
                AddFilter(Next("X,Y,W,H"), s => new ObfuscateContainer(s), FieldType.PREPARED_FILTER_OBFUSCATE, PreparedFilter.PIXELIZE);
                break;
            case "--blur":
                AddFilter(Next("X,Y,W,H"), s => new ObfuscateContainer(s), FieldType.PREPARED_FILTER_OBFUSCATE, PreparedFilter.BLUR);
                break;
            case "--crop":
                var crop = Rect(Next("X,Y,W,H"));
                _operations.Add(s =>
                {
                    if (!s.ApplyCrop(crop))
                    {
                        throw new CliException($"--crop {crop.X},{crop.Y},{crop.Width},{crop.Height} is outside the {s.Image.Width}x{s.Image.Height} image");
                    }
                });
                break;

            // style
            case "--color":
                _color = ParseColor(arg, Next("a color"));
                break;
            case "--fill":
                _fill = ParseColor(arg, Next("a color"));
                break;
            case "--thickness":
                _thickness = ParseInt(arg, Next("a number"), 0);
                break;
            case "--font":
                _font = Next("a font name");
                if (!FontFamily.Families.Any(f => string.Equals(f.Name, _font, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new CliException($"font \"{_font}\" is not installed");
                }
                break;
            case "--font-size":
                _fontSize = ParseInt(arg, Next("a number"), 1);
                break;
            case "--bold":
            case "--no-bold":
                _bold = arg == "--bold";
                break;
            case "--italic":
            case "--no-italic":
                _italic = arg == "--italic";
                break;
            case "--shadow":
            case "--no-shadow":
                _shadow = arg == "--shadow";
                break;
            case "--heads":
                string heads = Next("end, start, both or none");
                _heads = heads switch
                {
                    "end" => ArrowHeadCombination.END_POINT,
                    "start" => ArrowHeadCombination.START_POINT,
                    "both" => ArrowHeadCombination.BOTH,
                    "none" => ArrowHeadCombination.NONE,
                    _ => throw new CliException($"--heads expects end, start, both or none, not {heads}")
                };
                break;
            case "--pixel-size":
                _pixelSize = ParseInt(arg, Next("a number"), 1);
                break;
            case "--blur-radius":
                _blurRadius = ParseInt(arg, Next("a number"), 1);
                break;
            case "--magnification":
                _magnification = ParseInt(arg, Next("a number"), 1);
                break;
            case "--anchor":
                string anchor = Next("top-left, top, top-right, left, center, right, bottom-left, bottom or bottom-right");
                _anchor = anchor.ToLowerInvariant() switch
                {
                    "top-left" => Anchor.TopLeft,
                    "top" => Anchor.Top,
                    "top-right" => Anchor.TopRight,
                    "left" => Anchor.Left,
                    "center" => Anchor.Center,
                    "right" => Anchor.Right,
                    "bottom-left" => Anchor.BottomLeft,
                    "bottom" => Anchor.Bottom,
                    "bottom-right" => Anchor.BottomRight,
                    _ => throw new CliException($"--anchor expects top-left, top, top-right, left, center, right, bottom-left, bottom or bottom-right, not {anchor}")
                };
                break;
            case "--label-pos":
                string labelPosition = Next("above, below or inside");
                _labelPosition = labelPosition.ToLowerInvariant() switch
                {
                    "above" => LabelPosition.Above,
                    "below" => LabelPosition.Below,
                    "inside" => LabelPosition.Inside,
                    _ => throw new CliException($"--label-pos expects above, below or inside, not {labelPosition}")
                };
                break;
            case "--label":
                AddLabel(Next("the text"));
                break;

            // effects
            case "--border":
                _effects.Add(_ => new BorderEffect());
                break;
            case "--drop-shadow":
                _effects.Add(_ => new DropShadowEffect());
                break;
            case "--torn-edge":
                _effects.Add(_ => new TornEdgeEffect());
                break;
            case "--grayscale":
                _effects.Add(_ => new GrayscaleEffect());
                break;
            case "--invert":
                _effects.Add(_ => new InvertEffect());
                break;
            case "--rotate":
                int angle = ParseInt(arg, Next("90, 180 or 270"), 0);
                if (angle is not (90 or 180 or 270))
                {
                    throw new CliException("--rotate expects 90, 180 or 270");
                }
                _effects.Add(_ => new RotateEffect(angle));
                break;
            case "--resize":
                var size = Ints(arg, Next("W,H"), 2);
                if (size[0] < 0 || size[1] < 0 || size[0] + size[1] == 0)
                {
                    throw new CliException("--resize expects W,H, use 0 for one of them to keep the aspect ratio");
                }
                _effects.Add(image => size[0] == 0 || size[1] == 0
                    ? new ResizeEffect(size[0] == 0 ? image.Width * size[1] / image.Height : size[0],
                                       size[1] == 0 ? image.Height * size[0] / image.Width : size[1], true)
                    : new ResizeEffect(size[0], size[1], false));
                break;
            case "--scale":
                int percent = ParseInt(arg, Next("a percentage"), 1);
                _effects.Add(image => new ResizeEffect(Math.Max(1, image.Width * percent / 100), Math.Max(1, image.Height * percent / 100), true));
                break;

            default:
                return false;
        }
        i = index;
        return true;
    }

    /// <summary>
    /// Apply the element operations to the surface, in order
    /// </summary>
    public void ApplyTo(Surface surface)
    {
        foreach (var operation in _operations)
        {
            operation(surface);
        }
        surface.DeselectAllElements();
    }

    /// <summary>
    /// Apply the image effects to the rendered image, returns the new image (the old one is disposed if it changed)
    /// </summary>
    public Image ApplyEffects(Image image)
    {
        foreach (var createEffect in _effects)
        {
            Image result = ImageHelper.ApplyEffect(image, createEffect(image), new Matrix());
            if (result != null && !ReferenceEquals(result, image))
            {
                image.Dispose();
                image = result;
            }
        }
        return image;
    }

    // --- elements ------------------------------------------------------------

    /// <summary>
    /// Top left corner of an element of the given size whose X,Y is relative to the current anchor
    /// </summary>
    private static Point Place(Anchor anchor, Size image, int x, int y, int width, int height)
    {
        int left = anchor switch
        {
            Anchor.TopLeft or Anchor.Left or Anchor.BottomLeft => x,
            Anchor.Top or Anchor.Center or Anchor.Bottom => (image.Width - width) / 2 + x,
            _ => image.Width - width - x
        };
        int top = anchor switch
        {
            Anchor.TopLeft or Anchor.Top or Anchor.TopRight => y,
            Anchor.Left or Anchor.Center or Anchor.Right => (image.Height - height) / 2 + y,
            _ => image.Height - height - y
        };
        return new Point(left, top);
    }

    private void AddDrag(NativeRect rect, Func<Surface, DrawableContainer> create) =>
        AddBox(rect.Left, rect.Top, rect.Width, rect.Height, create);

    private void AddLine(string value, Func<Surface, DrawableContainer> create)
    {
        var p = Ints("--line/--arrow", value, 4);
        var style = Snapshot();
        var anchor = _anchor;
        var shape = new ShapeRef();
        _lastShape = shape;
        _operations.Add(surface =>
        {
            var element = create(surface);
            style(element);
            var from = Place(anchor, surface.Image.Size, p[0], p[1], 0, 0);
            var to = Place(anchor, surface.Image.Size, p[2], p[3], 0, 0);
            Draw(surface, element, from.X, from.Y, to.X, to.Y);
            RememberShape(shape, element, Rectangle.FromLTRB(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Max(from.X, to.X), Math.Max(from.Y, to.Y)));
        });
    }

    /// <summary>
    /// Draw an element like the editor does while dragging over the box, the box is placed relative to the current anchor
    /// </summary>
    private void AddBox(int x, int y, int width, int height, Func<Surface, DrawableContainer> create, Action<DrawableContainer, Surface, Point> after = null)
    {
        var style = Snapshot();
        var anchor = _anchor;
        var shape = new ShapeRef();
        _lastShape = shape;
        _operations.Add(surface =>
        {
            var element = create(surface);
            style(element);
            var origin = Place(anchor, surface.Image.Size, x, y, width, height);
            Draw(surface, element, origin.X, origin.Y, origin.X + width, origin.Y + height);
            after?.Invoke(element, surface, origin);
            RememberShape(shape, element, new Rectangle(origin.X, origin.Y, width, height));
        });
    }

    private static void RememberShape(ShapeRef shape, DrawableContainer element, Rectangle rect)
    {
        shape.Rect = rect;
        if (element.HasField(FieldType.LINE_COLOR))
        {
            shape.Color = element.GetFieldValueAsColor(FieldType.LINE_COLOR);
        }
    }

    private static void Draw(Surface surface, DrawableContainer element, int x1, int y1, int x2, int y2)
    {
        element.Status = element.DefaultEditMode;
        if (!element.HandleMouseDown(x1, y1))
        {
            element.Left = x1;
            element.Top = y1;
        }
        surface.AddElement(element, false, false);
        element.HandleMouseMove(x2, y2);
        element.HandleMouseUp(x2, y2);
        element.Status = EditStatus.IDLE;
    }

    private void AddFreehand(string value)
    {
        var points = value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Ints("--freehand", p, 2))
            .ToList();
        if (points.Count < 2)
        {
            throw new CliException("--freehand needs at least two points: X,Y;X,Y;...");
        }
        var style = Snapshot();
        var anchor = _anchor;
        _lastShape = null;
        _operations.Add(surface =>
        {
            var placed = points.Select(point => Place(anchor, surface.Image.Size, point[0], point[1], 0, 0)).ToList();
            var element = new FreehandContainer(surface);
            style(element);
            element.Status = element.DefaultEditMode;
            element.HandleMouseDown(placed[0].X, placed[0].Y);
            surface.AddElement(element, false, false);
            foreach (var point in placed.Skip(1))
            {
                element.HandleMouseMove(point.X, point.Y);
            }
            var last = placed[placed.Count - 1];
            element.HandleMouseUp(last.X, last.Y);
            element.Status = EditStatus.IDLE;
        });
    }

    private void AddText(string position, string text)
    {
        var p = Ints("--text", position, 2, 4);
        if (p.Length == 4)
        {
            AddBox(p[0], p[1], p[2], p[3], s => new TextContainer(s), (e, _, __) => ((TextContainer) e).Text = text);
            return;
        }

        var style = Snapshot();
        var anchor = _anchor;
        var shape = new ShapeRef();
        _lastShape = shape;
        _operations.Add(surface =>
        {
            var element = new TextContainer(surface) { Left = p[0], Top = p[1] };
            style(element);
            element.Text = text;
            element.FitToText();
            // the size is only known now, so this is where the anchor can be applied
            var origin = Place(anchor, surface.Image.Size, p[0], p[1], element.Width, element.Height);
            element.Left = origin.X;
            element.Top = origin.Y;
            surface.AddElement(element, false, false);
            RememberShape(shape, element, new Rectangle(origin.X, origin.Y, element.Width, element.Height));
        });
    }

    /// <summary>
    /// A caption at an image position (anchor top-left), used for the labels of 'combine': white bold text on a dark
    /// background unless the style arguments say otherwise
    /// </summary>
    internal void AddCaptionAt(int x, int y, string text)
    {
        var (anchor, color, fill, thickness, fontSize, bold) = (_anchor, _color, _fill, _thickness, _fontSize, _bold);
        _anchor = Anchor.TopLeft;
        _color ??= Color.White;
        _fill ??= Color.FromArgb(200, 0, 0, 0);
        _thickness ??= 0;
        _fontSize ??= 20;
        _bold ??= true;
        try
        {
            AddText($"{x},{y}", text);
        }
        finally
        {
            (_anchor, _color, _fill, _thickness, _fontSize, _bold) = (anchor, color, fill, thickness, fontSize, bold);
        }
    }

    /// <summary>
    /// A rectangle around an area (image coordinates), used by 'diff': red with a 3 pixel line unless the style arguments say otherwise
    /// </summary>
    internal void AddMarker(Rectangle rect)
    {
        var (anchor, color, thickness) = (_anchor, _color, _thickness);
        _anchor = Anchor.TopLeft;
        _color ??= Color.Red;
        _thickness ??= 3;
        try
        {
            AddBox(rect.X, rect.Y, rect.Width, rect.Height, s => new RectangleContainer(s));
        }
        finally
        {
            (_anchor, _color, _thickness) = (anchor, color, thickness);
        }
    }

    private void AddBubble(string position, string text)
    {
        var p = Ints("--bubble", position, 6);
        var anchor = _anchor;
        AddBox(p[0], p[1], p[2], p[3], s => new SpeechbubbleContainer(s), (e, surface, origin) =>
        {
            var bubble = (SpeechbubbleContainer) e;
            bubble.Text = text;
            // the tail is a point, relative to the same anchor as the box
            var tail = Place(anchor, surface.Image.Size, p[4], p[5], 0, 0);
            bubble.SetTailLocation(new NativePoint(tail.X, tail.Y));
        });
    }

    private void AddStep(string position)
    {
        var p = Ints("--step", position, 2, 3);
        if (p.Length == 3 && p[2] <= 0)
        {
            throw new CliException($"--step size must be positive, got {p[2]}");
        }
        var style = Snapshot();
        var anchor = _anchor;
        var shape = new ShapeRef();
        _lastShape = shape;
        _operations.Add(surface =>
        {
            var element = new StepLabelContainer(surface);
            style(element);
            // the label is centered at X,Y (relative to the anchor): placed with a click like in the editor, then resized as with its grippers
            var center = Place(anchor, surface.Image.Size, p[0], p[1], 0, 0);
            Draw(surface, element, center.X, center.Y, center.X, center.Y);
            if (p.Length == 3)
            {
                element.Width = p[2];
                element.Height = p[2];
            }
            element.Left = center.X - element.Width / 2;
            element.Top = center.Y - element.Height / 2;
            RememberShape(shape, element, new Rectangle(element.Left, element.Top, element.Width, element.Height));
        });
    }

    private void AddFilter(string value, Func<Surface, DrawableContainer> create, IFieldType presetField, PreparedFilter preset)
    {
        var rect = Rect(value);
        // Filters keep their editor defaults (no border), only the filter settings apply
        var style = Snapshot(filterOnly: true);
        var anchor = _anchor;
        _lastShape = null;
        _operations.Add(surface =>
        {
            var element = create(surface);
            element.SetFieldValue(presetField, preset);
            style(element);
            var origin = Place(anchor, surface.Image.Size, rect.Left, rect.Top, rect.Width, rect.Height);
            Draw(surface, element, origin.X, origin.Y, origin.X + rect.Width, origin.Y + rect.Height);
        });
    }

    /// <summary>
    /// A text stuck to the last shape: filled with the color of the shape, above its top left corner
    /// (inside when there is no room, or where --label-pos says)
    /// </summary>
    private void AddLabel(string text)
    {
        var shape = _lastShape ?? throw new CliException("--label goes right after a shape (--rect, --ellipse, --line, --arrow, --text, --step)");
        var style = Snapshot();
        var position = _labelPosition;
        bool fontSizeGiven = _fontSize.HasValue;
        bool boldGiven = _bold.HasValue;
        _operations.Add(surface =>
        {
            var label = new TextContainer(surface);
            style(label);
            if (!fontSizeGiven) label.SetFieldValue(FieldType.FONT_SIZE, 14f);
            if (!boldGiven) label.SetFieldValue(FieldType.FONT_BOLD, true);
            label.SetFieldValue(FieldType.FILL_COLOR, shape.Color);
            label.SetFieldValue(FieldType.LINE_COLOR, ContrastColor(shape.Color));
            label.SetFieldValue(FieldType.LINE_THICKNESS, 0);
            label.SetFieldValue(FieldType.SHADOW, false);
            label.Text = text;
            label.FitToText();

            int left = Math.Max(0, Math.Min(shape.Rect.Left, surface.Image.Width - label.Width));
            int top = position switch
            {
                LabelPosition.Below => shape.Rect.Bottom,
                LabelPosition.Inside => shape.Rect.Top,
                _ => shape.Rect.Top - label.Height
            };
            if (position != LabelPosition.Inside && (top < 0 || top + label.Height > surface.Image.Height))
            {
                top = shape.Rect.Top;
            }
            label.Left = left;
            label.Top = Math.Max(0, top);
            surface.AddElement(label, false, false);
        });
    }

    private static Color ContrastColor(Color background) =>
        (background.R * 299 + background.G * 587 + background.B * 114) / 1000 > 150 ? Color.Black : Color.White;

    /// <summary>
    /// Freeze the current style, so later style arguments only affect later elements
    /// </summary>
    private Action<DrawableContainer> Snapshot(bool filterOnly = false)
    {
        var values = new List<(IFieldType, object)>();
        if (!filterOnly)
        {
            if (_color.HasValue) values.Add((FieldType.LINE_COLOR, _color.Value));
            if (_fill.HasValue) values.Add((FieldType.FILL_COLOR, _fill.Value));
            if (_thickness.HasValue) values.Add((FieldType.LINE_THICKNESS, _thickness.Value));
            if (_font != null) values.Add((FieldType.FONT_FAMILY, _font));
            if (_fontSize.HasValue) values.Add((FieldType.FONT_SIZE, _fontSize.Value));
            if (_bold.HasValue) values.Add((FieldType.FONT_BOLD, _bold.Value));
            if (_italic.HasValue) values.Add((FieldType.FONT_ITALIC, _italic.Value));
            if (_shadow.HasValue) values.Add((FieldType.SHADOW, _shadow.Value));
            if (_heads.HasValue) values.Add((FieldType.ARROWHEADS, _heads.Value));
        }
        if (_pixelSize.HasValue) values.Add((FieldType.PIXEL_SIZE, _pixelSize.Value));
        if (_blurRadius.HasValue) values.Add((FieldType.BLUR_RADIUS, _blurRadius.Value));
        if (_magnification.HasValue) values.Add((FieldType.MAGNIFICATION_FACTOR, _magnification.Value));

        return element =>
        {
            foreach (var (fieldType, value) in values)
            {
                SetField(element, fieldType, value);
            }
        };
    }

    /// <summary>
    /// Set a field on the element or on its filters (pixel size, blur radius and magnification live on the filters)
    /// </summary>
    private static void SetField(DrawableContainer element, IFieldType fieldType, object value)
    {
        if (element.HasField(fieldType))
        {
            element.SetFieldValue(fieldType, value);
        }
        foreach (var filter in element.Filters.OfType<AbstractFieldHolder>())
        {
            if (filter.HasField(fieldType))
            {
                filter.SetFieldValue(fieldType, value);
            }
        }
    }

    // --- parsing -------------------------------------------------------------

    private static NativeRect Rect(string value)
    {
        var p = Ints("rectangle", value, 4);
        if (p[2] <= 0 || p[3] <= 0)
        {
            throw new CliException($"expected X,Y,W,H with positive W and H, got {value}");
        }
        return new NativeRect(p[0], p[1], p[2], p[3]);
    }

    private static int[] Ints(string option, string value, params int[] counts)
    {
        var parts = value.Split(',');
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]))
            {
                throw new CliException($"invalid number \"{parts[i]}\" in {option} {value}");
            }
        }
        if (!counts.Contains(result.Length))
        {
            throw new CliException($"{option} expects {string.Join(" or ", counts)} comma separated numbers, got {value}");
        }
        return result;
    }

    private static int ParseInt(string option, string value, int minimum)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) || result < minimum)
        {
            throw new CliException($"invalid value for {option}: {value}");
        }
        return result;
    }

    internal static Color ParseColor(string option, string value)
    {
        string hex = value.TrimStart('#');
        if (value.StartsWith("#") && hex.Length == 8
            && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
        {
            return Color.FromArgb(unchecked((int) argb));
        }
        if (string.Equals(value, "transparent", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return Color.Transparent;
        }
        try
        {
            var color = ColorTranslator.FromHtml(value);
            if (color.IsEmpty || (color.IsNamedColor && !color.IsKnownColor))
            {
                throw new FormatException();
            }
            return color;
        }
        catch (Exception)
        {
            throw new CliException($"invalid color for {option}: {value}, use a name, #RRGGBB or #AARRGGBB");
        }
    }
}

internal sealed class CliException(string message) : Exception(message);
