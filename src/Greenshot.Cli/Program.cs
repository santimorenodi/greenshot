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
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Dapplo.Windows.Common.Structs;
using Greenshot.Base.Core;
using Greenshot.Base.Core.Enums;
using Greenshot.Base.Core.FileFormat;
using Greenshot.Base.Interfaces;
using Greenshot.Base.Interfaces.Plugin;
using Greenshot.Base.Threading;
using Greenshot.Editor.Configuration;
using Greenshot.Editor.Drawing;
using Greenshot.Editor.FileFormatHandlers;

namespace Greenshot.Cli;

/// <summary>
/// Command line interface for taking and annotating screenshots with the Greenshot capture engine and editor, without any UI.
/// It uses an in-memory default configuration, so it never touches greenshot.ini and can run next to Greenshot.exe.
/// </summary>
internal static class Program
{
    private const string Usage =
        "greenshot-cli - take and annotate screenshots from the command line with the Greenshot capture engine and editor\n" +
        "\n" +
        "usage:\n" +
        "  greenshot-cli list [--json]                               list monitors and capturable windows\n" +
        "  greenshot-cli capture [target] [options] [annotations]    take a screenshot, annotate it and save it\n" +
        "  greenshot-cli edit INPUT [options] [annotations]          annotate an image (png, jpg, bmp, gif, tiff, .greenshot)\n" +
        "  greenshot-cli combine IMAGE... -o OUT [options]           put images side by side (before/after)\n" +
        "  greenshot-cli diff BEFORE AFTER -o OUT [options]          mark the areas that changed between two images\n" +
        "\n" +
        "All coordinates and sizes are physical pixels: the process is per-monitor DPI aware (v2), so nothing has to be\n" +
        "scaled by the Windows display scaling (150 % and so on), and 'list --json' gives the same numbers you capture with.\n" +
        "\n" +
        "list:\n" +
        "  --json                one JSON object: \"monitors\" (index, x, y, width, height, device, primary) and \"windows\"\n" +
        "                        (hwnd, title, pid, exe, rect, client, minimized, visible), rectangles in screen pixels\n" +
        "                        example: greenshot-cli list --json\n" +
        "\n" +
        "target (default is --fullscreen):\n" +
        "  --fullscreen          all monitors\n" +
        "  --monitor N           monitor index from 'list'\n" +
        "  --active              current foreground window\n" +
        "  --window W            window handle (0x...) or case-insensitive title substring\n" +
        "  --window-pid N        window of the process with this id\n" +
        "  --window-exe PATTERN  window of a process whose exe matches (case-insensitive, * and ? wildcards; a pattern with a\n" +
        "                        path matches the full path, otherwise the file name)\n" +
        "                        --window, --window-pid and --window-exe can be combined; with --window-pid or --window-exe\n" +
        "                        several matching windows are an error that lists them, they are never picked silently\n" +
        "                        example: greenshot-cli capture --window-exe \"D:\\Godlike\\*\" --window \"Unreal Editor\" -o a.png\n" +
        "  --region X,Y,W,H      rectangle in virtual screen coordinates; together with --window/--active/--window-pid/\n" +
        "                        --window-exe it is relative to the top left of the window (of its client area with\n" +
        "                        --client) and cut out after capturing the window\n" +
        "                        example: greenshot-cli capture --window-pid 4321 --client --region 0,0,800,450 -o top.png\n" +
        "\n" +
        "window options (capture, need a window target):\n" +
        "  --client              only the client area, without frame and title bar\n" +
        "  --mode M              window capture mode auto (default), aero, aerotransparent, gdi, screen\n" +
        "  --no-activate         guarantee that the capture does not activate the window, does not bring it to the front\n" +
        "                        and does not move the mouse; fails with an error if that is not possible (a minimized window\n" +
        "                        without --restore-behind). aero, aerotransparent, gdi and auto do not need the window in\n" +
        "                        front and work with it behind other windows. screen copies the pixels on the screen, so it\n" +
        "                        needs the window to be visible (a warning names the windows in front of it)\n" +
        "                        example: greenshot-cli capture --window \"Unreal Editor\" --no-activate --mode aero -o a.png\n" +
        "  --include-popups      also draw the popup windows of the same process that overlap the window (menus, tooltips,\n" +
        "                        open combo boxes) at their real position; the image grows to fit popups that stick out.\n" +
        "                        Implies --no-activate, because menus close when their window loses the focus\n" +
        "                        example: greenshot-cli capture --window-pid 4321 --include-popups -o menu.png\n" +
        "  --restore-behind      EXPERIMENTAL. A minimized window has no current image, so restore it without activating\n" +
        "                        it, send it to the bottom of the z-order, wait for it to paint (--settle), capture it with\n" +
        "                        PrintWindow and minimize it again. Windows that draw with DirectX (games, Unreal viewports)\n" +
        "                        can come out black or old. Implies --no-activate\n" +
        "  --settle MS           milliseconds to wait for a restored window to paint (default 500)\n" +
        "                        example: greenshot-cli capture --window \"Unreal Editor\" --restore-behind --settle 1500 -o a.png\n" +
        "\n" +
        "options:\n" +
        "  -o, --output FILE     output file, format from extension (capture default: greenshot_<timestamp>.png in the\n" +
        "                        current folder, edit default: overwrite INPUT)\n" +
        "  --format F            png, jpg, bmp, gif, tiff, greenshot (overrides the extension)\n" +
        "  --quality N           jpg quality 1-100 (default 80)\n" +
        "  --delay SEC           capture only: wait before capturing (e.g. 1.5)\n" +
        "  --clipboard           also copy the image to the clipboard\n" +
        "  --open                open the screenshot in the Greenshot editor afterwards\n" +
        "  --preview FILE        also save a smaller copy to look at, the original keeps its full resolution\n" +
        "  --preview-width N     preview width in pixels (height follows)\n" +
        "  --preview-max N       preview size of the longest side (default 1200); a preview is never bigger than the original\n" +
        "                        example: greenshot-cli capture --active -o full.png --preview small.png --preview-max 800\n" +
        "  --json                print one line of JSON instead of 'saved:' and 'size:': path, width, height, preview and,\n" +
        "                        for capture, hwnd, pid, rect (what was captured, screen pixels) and mode\n" +
        "                        example: greenshot-cli capture --window-pid 4321 --json -o a.png\n" +
        "\n" +
        Annotations.Usage +
        "\n" +
        "edit only:\n" +
        "  --grid N              save a copy with a grid every N pixels and the coordinates written on it, to read positions\n" +
        "                        for annotations (default output: INPUT name with .grid before the extension)\n" +
        "                        example: greenshot-cli edit shot.png --grid 100 -o shot.grid.png\n" +
        "\n" +
        "combine (images can be png, jpg, bmp, gif or tiff):\n" +
        "  --hstack | --vstack | --grid COLUMNS   layout, side by side (default), on top of each other, or a grid\n" +
        "  --gap N               pixels between the images (default 8)\n" +
        "  --gap-color C         color between the images (default #202020)\n" +
        "  --width N             scale every image to this width, keeping its proportions\n" +
        "  --labels TEXT...      one text per image (drawn in its top left corner as Greenshot text, with the style\n" +
        "                        arguments of the annotations: --font-size, --color, --fill, --bold ... given anywhere)\n" +
        "                        example: greenshot-cli combine before.png after.png -o both.png --hstack --width 800 \\\n" +
        "                                   --labels \"Before\" \"After\" --json\n" +
        "\n" +
        "diff:\n" +
        "  --threshold N         how much a pixel has to change (0-255, largest channel difference, default 24)\n" +
        "  --min-area N          ignore changed areas whose rectangle is smaller than N square pixels (default 64)\n" +
        "  --merge N             changed areas closer than about N pixels become one rectangle (default 12); changes are\n" +
        "                        collected in blocks of 8 pixels, so anything closer than 8 to 15 pixels is always one\n" +
        "                        rectangle\n" +
        "  the rectangles are drawn as Greenshot rectangles on AFTER (red, 3 px; --color and --thickness change that), the\n" +
        "  images need the same size, --json also lists the rectangles\n" +
        "                        example: greenshot-cli diff before.png after.png -o marked.png --threshold 30 --json\n" +
        "\n" +
        "output (stdout): 'saved: PATH' and 'size: WxH' (or the JSON line); errors go to stderr\n" +
        "exit code: 0 on success, 1 on error\n" +
        "\n" +
        "example:\n" +
        "  greenshot-cli capture --active --color #E53935 --thickness 4 --arrow 900,80,700,210 --step 690,220 \\\n" +
        "    --font-size 18 --bubble 900,40,260,70,880,110 \"Click here\" --pixelate 40,600,300,40 --drop-shadow -o shot.png\n";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                Console.Write(Usage);
                return args.Length == 0 ? 1 : 0;
            }

            // The capture code is async and runs the DWM capture form through the UI dispatcher, this thread is that UI thread
            SimpleServiceProvider.Current.AddService<IUiDispatcher>(WinFormsUiDispatcher.CreateForCurrentThread());

            // In-memory configuration with defaults, the capture code reads its settings from it
            IniConfigHelper.EnsureInitialized();
            // The editor elements read their default colors, fonts etc. from here, also in memory with defaults
            IniConfigHelper.EnsureSection<IEditorConfiguration>(() => new EditorConfigurationImpl());
            // ImageIO saves through the registered file format handlers and the format registry,
            // Greenshot.exe sets them up in MainForm and EditorInitialize
            var fileFormatRegistry = new FileFormatRegistry();
            SimpleServiceProvider.Current.AddService<IFileFormatRegistry>(fileFormatRegistry);
            CoreFileFormats.RegisterCoreFileFormats(fileFormatRegistry);
            var defaultHandler = new DefaultFileFormatHandler();
            // .greenshot files keep the elements editable, loading one needs to know how to make a surface
            var greenshotHandler = new GreenshotFileFormatHandler();
            SimpleServiceProvider.Current.AddService<IFileFormatHandler>(defaultHandler, greenshotHandler);
            defaultHandler.RegisterFileFormats(fileFormatRegistry);
            greenshotHandler.RegisterFileFormats(fileFormatRegistry);
            SimpleServiceProvider.Current.AddService<Func<ISurface>>(() => new Surface());

            switch (args[0])
            {
                case "list":
                    return List(args.Skip(1).ToArray());
                case "capture":
                    return Capture(args.Skip(1).ToArray());
                case "edit":
                    return Edit(args.Skip(1).ToArray());
                case "combine":
                    return Combine(args.Skip(1).ToArray());
                case "diff":
                    return Diff(args.Skip(1).ToArray());
                default:
                    throw new CliException($"unknown command: {args[0]}, see 'greenshot-cli help'");
            }
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// Options every command that saves an image has
    /// </summary>
    private sealed class OutputOptions
    {
        public string Output;
        public string Format;
        public int Quality = 80;
        public bool Clipboard;
        public bool Open;
        public bool Json;
        public string Preview;
        public int? PreviewWidth;
        public int? PreviewMax;

        public bool TryParse(string[] args, ref int i)
        {
            string arg = args[i];
            int index = i;
            string NextValue()
            {
                if (index + 1 >= args.Length)
                {
                    throw new CliException($"{arg} requires a value");
                }
                return args[++index];
            }

            switch (arg)
            {
                case "-o":
                case "--output":
                    Output = NextValue();
                    break;
                case "--format":
                    Format = NextValue().ToLowerInvariant();
                    break;
                case "--quality":
                    Quality = ParseInt(arg, NextValue());
                    if (Quality < 1 || Quality > 100)
                    {
                        throw new CliException("--quality must be 1-100");
                    }
                    break;
                case "--clipboard":
                    Clipboard = true;
                    break;
                case "--open":
                    Open = true;
                    break;
                case "--json":
                    Json = true;
                    break;
                case "--preview":
                    Preview = NextValue();
                    break;
                case "--preview-width":
                    PreviewWidth = ParseInt(arg, NextValue());
                    if (PreviewWidth < 1)
                    {
                        throw new CliException("--preview-width must be at least 1");
                    }
                    break;
                case "--preview-max":
                    PreviewMax = ParseInt(arg, NextValue());
                    if (PreviewMax < 1)
                    {
                        throw new CliException("--preview-max must be at least 1");
                    }
                    break;
                default:
                    return false;
            }
            i = index;
            return true;
        }

        public void Validate()
        {
            if (Preview == null && (PreviewWidth.HasValue || PreviewMax.HasValue))
            {
                throw new CliException("--preview-width and --preview-max need --preview FILE");
            }
            if (PreviewWidth.HasValue && PreviewMax.HasValue)
            {
                throw new CliException("use only one of --preview-width and --preview-max");
            }
            if (Preview != null)
            {
                // fail before anything is written
                string extension = Path.GetExtension(Preview).TrimStart('.').ToLowerInvariant();
                if (!(string.IsNullOrEmpty(extension) || extension is "png" or "jpg" or "jpeg" or "bmp" or "gif" or "tif" or "tiff"))
                {
                    throw new CliException($"a preview is an image, use png, jpg, bmp, gif or tiff, not {extension}");
                }
            }
        }
    }

    private sealed class SaveResult
    {
        public string Path;
        public int Width;
        public int Height;
        public string PreviewPath;
        public Size PreviewSize;
    }

    private static int List(string[] args)
    {
        bool json = false;
        foreach (string arg in args)
        {
            if (arg == "--json")
            {
                json = true;
            }
            else
            {
                // 'list' has always ignored what it does not know
                Console.Error.WriteLine($"warning: list ignores {arg}");
            }
        }

        var screens = Screen.AllScreens;
        var windows = WindowDetails.GetTopLevelWindows().ToList();

        if (json)
        {
            var monitors = screens.Select((screen, i) => Json.Object(
                ("index", Json.Int(i)),
                ("x", Json.Int(screen.Bounds.X)),
                ("y", Json.Int(screen.Bounds.Y)),
                ("width", Json.Int(screen.Bounds.Width)),
                ("height", Json.Int(screen.Bounds.Height)),
                ("device", Json.String(screen.DeviceName)),
                ("primary", Json.Bool(screen.Primary))));
            Console.WriteLine(Json.Object(
                ("monitors", Json.Array(monitors)),
                ("windows", Json.Array(windows.Select(w => WindowTools.Describe(w).ToJson())))));
            return 0;
        }

        for (int i = 0; i < screens.Length; i++)
        {
            var bounds = screens[i].Bounds;
            Console.WriteLine($"monitor {i}: {bounds.Width}x{bounds.Height} at {bounds.X},{bounds.Y} {screens[i].DeviceName}{(screens[i].Primary ? " (primary)" : "")}");
        }

        foreach (var window in windows)
        {
            var rect = window.WindowRectangle;
            Console.WriteLine($"window 0x{window.Handle.ToInt64():x}: {rect.Width}x{rect.Height} pid={window.ProcessId}{(window.Iconic ? " minimized" : "")} \"{window.Text}\"");
        }
        return 0;
    }

    private static int Capture(string[] args)
    {
        string windowTitle = null;
        int? windowPid = null;
        string windowExe = null;
        bool active = false;
        bool fullscreen = false;
        string monitor = null;
        string region = null;
        double delay = 0;
        var mode = WindowCaptureMode.Auto;
        bool client = false;
        bool includePopups = false;
        bool noActivate = false;
        bool restoreBehind = false;
        int settle = 500;
        var output = new OutputOptions();
        var annotations = new Annotations();
        var seenTargets = new HashSet<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (annotations.TryParse(args, ref i) || output.TryParse(args, ref i))
            {
                continue;
            }
            string arg = args[i];
            string NextValue()
            {
                if (i + 1 >= args.Length)
                {
                    throw new CliException($"{arg} requires a value");
                }
                return args[++i];
            }

            // giving the same target twice was always an error
            if (arg is "--fullscreen" or "--active" or "--monitor" or "--window" or "--window-pid" or "--window-exe" or "--region" && !seenTargets.Add(arg))
            {
                throw new CliException("use only one of --fullscreen, --monitor, --active, --window or --region");
            }

            switch (arg)
            {
                case "--fullscreen":
                    fullscreen = true;
                    break;
                case "--active":
                    active = true;
                    break;
                case "--monitor":
                    monitor = NextValue();
                    break;
                case "--window":
                    windowTitle = NextValue();
                    break;
                case "--window-pid":
                    windowPid = ParseInt(arg, NextValue());
                    break;
                case "--window-exe":
                    windowExe = NextValue();
                    break;
                case "--region":
                    region = NextValue();
                    break;
                case "--delay":
                    if (!double.TryParse(NextValue(), NumberStyles.Float, CultureInfo.InvariantCulture, out delay) || delay < 0)
                    {
                        throw new CliException("--delay expects a number of seconds");
                    }
                    break;
                case "--mode":
                    string modeValue = NextValue();
                    if (!Enum.TryParse(modeValue, true, out mode) || !Enum.IsDefined(typeof(WindowCaptureMode), mode))
                    {
                        throw new CliException($"unknown capture mode: {modeValue}");
                    }
                    break;
                case "--client":
                    client = true;
                    break;
                case "--include-popups":
                    includePopups = true;
                    break;
                case "--no-activate":
                    noActivate = true;
                    break;
                case "--restore-behind":
                    restoreBehind = true;
                    break;
                case "--settle":
                    settle = ParseInt(arg, NextValue());
                    if (settle < 0)
                    {
                        throw new CliException("--settle expects milliseconds, 0 or more");
                    }
                    break;
                default:
                    throw new CliException($"unknown argument: {arg}");
            }
        }
        output.Validate();

        bool windowFilters = windowTitle != null || windowPid.HasValue || windowExe != null;
        if (active && windowFilters)
        {
            throw new CliException("use only one of --fullscreen, --monitor, --active, --window or --region");
        }
        bool windowTarget = active || windowFilters;
        // --region only is a screen region, together with a window it is relative to the window
        int targets = (fullscreen ? 1 : 0) + (monitor != null ? 1 : 0) + (windowTarget ? 1 : 0) + (region != null && !windowTarget ? 1 : 0);
        if (targets > 1)
        {
            throw new CliException("use only one of --fullscreen, --monitor, --active, --window or --region");
        }
        if (!windowTarget && (client || includePopups || noActivate || restoreBehind))
        {
            throw new CliException("--client, --include-popups, --no-activate and --restore-behind need a window: --window, --window-pid, --window-exe or --active");
        }

        string fullPath = ResolveOutputPath(output.Output, ref output.Format);
        string outputFormat = ParseOutputFormat(output.Format, annotations);

        Rectangle? relativeRegion = null;
        if (windowTarget && region != null)
        {
            var parsed = ParseRegion(region);
            relativeRegion = new Rectangle(parsed.X, parsed.Y, parsed.Width, parsed.Height);
        }

        // resolve window before the delay, so a wrong title fails fast
        WindowDetails window = windowFilters ? WindowTools.Select(windowTitle, windowPid, windowExe) : null;

        if (delay > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(delay));
        }

        if (active)
        {
            window = WindowDetails.GetActiveWindow() ?? throw new CliException("no active window");
        }

        CaptureInfo info;
        if (window != null)
        {
            info = WindowCapturer.Capture(window, new WindowCaptureRequest
            {
                Mode = mode,
                Client = client,
                Region = relativeRegion,
                IncludePopups = includePopups,
                NoActivate = noActivate,
                RestoreBehind = restoreBehind,
                SettleMs = settle
            });
        }
        else
        {
            NativeRect screenRect;
            ICapture capture;
            if (monitor != null)
            {
                screenRect = GetMonitorBounds(monitor);
                capture = Wait(WindowCapture.CaptureRectangleAsync(new Base.Core.Capture(), screenRect));
            }
            else if (region != null)
            {
                screenRect = ParseRegion(region);
                capture = Wait(WindowCapture.CaptureRectangleAsync(new Base.Core.Capture(), screenRect));
            }
            else
            {
                capture = Wait(WindowCapture.CaptureScreenAsync(new Base.Core.Capture()));
                screenRect = capture?.ScreenBounds ?? default;
            }

            info = new CaptureInfo
            {
                Image = capture?.Image,
                ScreenRect = new Rectangle(screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height),
                Mode = "screen"
            };
        }

        if (info.Image == null)
        {
            throw new CliException("capture failed");
        }

        // the surface owns the image from here on
        SaveResult result;
        using (var surface = new Surface(info.Image))
        {
            result = Save(surface, annotations, output, fullPath, outputFormat, 0);
        }
        Print(output, result, info);

        if (output.Open)
        {
            OpenInEditor(fullPath);
        }
        return 0;
    }

    private static int Edit(string[] args)
    {
        string input = null;
        int grid = 0;
        var output = new OutputOptions();
        var annotations = new Annotations();

        for (int i = 0; i < args.Length; i++)
        {
            if (annotations.TryParse(args, ref i) || output.TryParse(args, ref i))
            {
                continue;
            }
            string arg = args[i];

            switch (arg)
            {
                case "--grid":
                    if (i + 1 >= args.Length)
                    {
                        throw new CliException("--grid requires a value");
                    }
                    grid = ParseInt(arg, args[++i]);
                    if (grid < 2)
                    {
                        throw new CliException("--grid expects a step of at least 2 pixels");
                    }
                    break;
                default:
                    if (arg.StartsWith("-") || input != null)
                    {
                        throw new CliException($"unknown argument: {arg}");
                    }
                    input = arg;
                    break;
            }
        }
        output.Validate();

        if (input == null)
        {
            throw new CliException("edit needs an input image, see 'greenshot-cli help'");
        }
        string inputPath = Path.GetFullPath(input);
        if (!File.Exists(inputPath))
        {
            throw new CliException($"{inputPath} does not exist");
        }

        // a grid is only there to read positions, it never overwrites the image it was made from
        string target = output.Output ?? (grid > 0 ? Path.ChangeExtension(inputPath, ".grid" + Path.GetExtension(inputPath)) : inputPath);
        string fullPath = ResolveOutputPath(target, ref output.Format);
        string outputFormat = ParseOutputFormat(output.Format, annotations);
        if (grid > 0 && outputFormat == WellKnownFileFormats.Greenshot)
        {
            throw new CliException("a grid can't be kept editable, save to png, jpg, bmp, gif or tiff to use --grid");
        }

        SaveResult result;
        using (var surface = LoadSurface(inputPath))
        {
            result = Save(surface, annotations, output, fullPath, outputFormat, grid);
        }
        Print(output, result, null);

        if (output.Open)
        {
            OpenInEditor(fullPath);
        }
        return 0;
    }

    private static int Combine(string[] args)
    {
        var inputs = new List<string>();
        var layout = StackLayout.Horizontal;
        int columns = 0;
        int gap = 8;
        Color gapColor = Annotations.ParseColor("--gap-color", "#202020");
        int tileWidth = 0;
        var labels = new List<string>();
        var output = new OutputOptions();
        var annotations = new Annotations();

        for (int i = 0; i < args.Length; i++)
        {
            if (annotations.TryParse(args, ref i) || output.TryParse(args, ref i))
            {
                continue;
            }
            string arg = args[i];
            string NextValue()
            {
                if (i + 1 >= args.Length)
                {
                    throw new CliException($"{arg} requires a value");
                }
                return args[++i];
            }

            switch (arg)
            {
                case "--hstack":
                    layout = StackLayout.Horizontal;
                    break;
                case "--vstack":
                    layout = StackLayout.Vertical;
                    break;
                case "--grid":
                    layout = StackLayout.Grid;
                    columns = ParseInt(arg, NextValue());
                    if (columns < 1)
                    {
                        throw new CliException("--grid expects the number of columns");
                    }
                    break;
                case "--gap":
                    gap = ParseInt(arg, NextValue());
                    if (gap < 0)
                    {
                        throw new CliException("--gap can't be negative");
                    }
                    break;
                case "--gap-color":
                    gapColor = Annotations.ParseColor(arg, NextValue());
                    break;
                case "--width":
                    tileWidth = ParseInt(arg, NextValue());
                    if (tileWidth < 1)
                    {
                        throw new CliException("--width must be at least 1");
                    }
                    break;
                case "--labels":
                    // every following value up to the next option is a label
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--") && args[i + 1] != "-o")
                    {
                        labels.Add(args[++i]);
                    }
                    if (labels.Count == 0)
                    {
                        throw new CliException("--labels needs one text per image");
                    }
                    break;
                default:
                    if (arg.StartsWith("-"))
                    {
                        throw new CliException($"unknown argument: {arg}");
                    }
                    inputs.Add(arg);
                    break;
            }
        }
        output.Validate();

        if (inputs.Count < 2)
        {
            throw new CliException("combine needs at least two images, see 'greenshot-cli help'");
        }
        if (labels.Count > inputs.Count)
        {
            throw new CliException($"{labels.Count} labels for {inputs.Count} images");
        }

        string fullPath = ResolveOutputPath(output.Output, ref output.Format);
        string outputFormat = ParseOutputFormat(output.Format, annotations);

        var images = new List<Image>();
        try
        {
            foreach (string input in inputs)
            {
                string path = Path.GetFullPath(input);
                if (!File.Exists(path))
                {
                    throw new CliException($"{path} does not exist");
                }
                if (string.Equals(Path.GetExtension(path), ".greenshot", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CliException($"{path}: .greenshot files can't be combined, save it to png first");
                }
                try
                {
                    images.Add(ImageIO.LoadImage(path) ?? throw new CliException($"cannot read {path}"));
                }
                catch (CliException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new CliException($"cannot read {path}: {ex.Message}");
                }
            }

            var combined = ImageTools.Combine(images, layout, columns, gap, gapColor, tileWidth, out var tiles);
            for (int i = 0; i < labels.Count; i++)
            {
                annotations.AddCaptionAt(tiles[i].X + 8, tiles[i].Y + 8, labels[i]);
            }

            SaveResult result;
            using (var surface = new Surface(combined))
            {
                result = Save(surface, annotations, output, fullPath, outputFormat, 0);
            }
            Print(output, result, null);
        }
        finally
        {
            foreach (var image in images)
            {
                image.Dispose();
            }
        }

        if (output.Open)
        {
            OpenInEditor(fullPath);
        }
        return 0;
    }

    private static int Diff(string[] args)
    {
        var inputs = new List<string>();
        int threshold = 24;
        int minArea = 64;
        int merge = 12;
        var output = new OutputOptions();
        var annotations = new Annotations();

        for (int i = 0; i < args.Length; i++)
        {
            if (annotations.TryParse(args, ref i) || output.TryParse(args, ref i))
            {
                continue;
            }
            string arg = args[i];
            string NextValue()
            {
                if (i + 1 >= args.Length)
                {
                    throw new CliException($"{arg} requires a value");
                }
                return args[++i];
            }

            switch (arg)
            {
                case "--threshold":
                    threshold = ParseInt(arg, NextValue());
                    if (threshold < 0 || threshold > 255)
                    {
                        throw new CliException("--threshold must be 0-255");
                    }
                    break;
                case "--min-area":
                    minArea = ParseInt(arg, NextValue());
                    if (minArea < 0)
                    {
                        throw new CliException("--min-area can't be negative");
                    }
                    break;
                case "--merge":
                    merge = ParseInt(arg, NextValue());
                    if (merge < 0)
                    {
                        throw new CliException("--merge can't be negative");
                    }
                    break;
                default:
                    if (arg.StartsWith("-"))
                    {
                        throw new CliException($"unknown argument: {arg}");
                    }
                    inputs.Add(arg);
                    break;
            }
        }
        output.Validate();

        if (inputs.Count != 2)
        {
            throw new CliException("diff needs two images: greenshot-cli diff BEFORE AFTER -o OUT");
        }
        string beforePath = Path.GetFullPath(inputs[0]);
        string afterPath = Path.GetFullPath(inputs[1]);
        foreach (string path in new[] { beforePath, afterPath })
        {
            if (!File.Exists(path))
            {
                throw new CliException($"{path} does not exist");
            }
        }

        string target = output.Output ?? Path.ChangeExtension(afterPath, ".diff" + Path.GetExtension(afterPath));
        string fullPath = ResolveOutputPath(target, ref output.Format);
        string outputFormat = ParseOutputFormat(output.Format, annotations);

        using var beforeImage = LoadBitmap(beforePath);
        using var afterImage = LoadBitmap(afterPath);
        var differences = ImageTools.Differences(beforeImage, afterImage, threshold, minArea, merge, out long changedPixels);

        // a little air around the marked areas
        foreach (var difference in differences)
        {
            var padded = Rectangle.Intersect(Rectangle.Inflate(difference, 2, 2), new Rectangle(0, 0, afterImage.Width, afterImage.Height));
            annotations.AddMarker(padded);
        }

        SaveResult result;
        using (var surface = new Surface(new Bitmap(afterImage)))
        {
            result = Save(surface, annotations, output, fullPath, outputFormat, 0);
        }

        if (output.Json)
        {
            double percent = 100.0 * changedPixels / ((double) afterImage.Width * afterImage.Height);
            Console.WriteLine(Json.Object(
                ("path", Json.String(result.Path)),
                ("width", Json.Int(result.Width)),
                ("height", Json.Int(result.Height)),
                ("changed_pixels", Json.Int(changedPixels)),
                ("changed_percent", percent.ToString("0.####", CultureInfo.InvariantCulture)),
                ("rects", Json.Array(differences.Select(Json.Rect))),
                ("preview", result.PreviewPath != null ? Json.String(result.PreviewPath) : null)));
        }
        else
        {
            Console.WriteLine($"saved: {result.Path}");
            Console.WriteLine($"size: {result.Width}x{result.Height}");
            Console.WriteLine($"differences: {differences.Count}");
            foreach (var difference in differences)
            {
                Console.WriteLine($"rect: {difference.X},{difference.Y},{difference.Width},{difference.Height}");
            }
            if (result.PreviewPath != null)
            {
                Console.WriteLine($"preview: {result.PreviewPath}");
            }
        }

        if (output.Open)
        {
            OpenInEditor(fullPath);
        }
        return 0;
    }

    private static Bitmap LoadBitmap(string path)
    {
        try
        {
            using var image = ImageIO.LoadImage(path) ?? throw new CliException($"cannot read {path}");
            return new Bitmap(image);
        }
        catch (CliException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CliException($"cannot read {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// A surface with the image, or with the image and the elements of a .greenshot file
    /// </summary>
    private static Surface LoadSurface(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".greenshot", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            return (Surface) new GreenshotFileFormatHandler().LoadSurface(stream);
        }

        Image image;
        try
        {
            image = ImageIO.LoadImage(path);
        }
        catch (Exception ex)
        {
            throw new CliException($"cannot read {path}: {ex.Message}");
        }
        return new Surface(image ?? throw new CliException($"cannot read {path}"));
    }

    /// <summary>
    /// Apply the annotations and save: a .greenshot keeps the elements editable, anything else is the rendered image.
    /// Also writes the preview when one was asked for. Does not print anything.
    /// </summary>
    private static SaveResult Save(Surface surface, Annotations annotations, OutputOptions options, string fullPath, string outputFormat, int grid)
    {
        annotations.ApplyTo(surface);
        var settings = new SurfaceOutputSettings(outputFormat, options.Quality);
        var result = new SaveResult { Path = fullPath };

        if (outputFormat == WellKnownFileFormats.Greenshot)
        {
            ImageIO.Save(surface, fullPath, true, settings, false);
            CheckWritten(fullPath, options.Format);
            result.Width = surface.Image.Width;
            result.Height = surface.Image.Height;
            if (options.Clipboard || options.Preview != null)
            {
                using var rendered = surface.GetImageForExport();
                if (options.Clipboard)
                {
                    Clipboard.SetImage(rendered);
                }
                SavePreview(rendered, options, result);
            }
            return result;
        }

        Image image = annotations.ApplyEffects(surface.GetImageForExport());
        if (grid > 0)
        {
            var withGrid = ImageTools.DrawGrid(image, grid);
            image.Dispose();
            image = withGrid;
        }
        using (image)
        {
            ImageIO.SaveRenderedImage(image, fullPath, true, settings, false);
            CheckWritten(fullPath, options.Format);
            if (options.Clipboard)
            {
                Clipboard.SetImage(image);
            }
            result.Width = image.Width;
            result.Height = image.Height;
            SavePreview(image, options, result);
        }
        return result;
    }

    private static void SavePreview(Image image, OutputOptions options, SaveResult result)
    {
        if (options.Preview == null)
        {
            return;
        }

        string previewPath = Path.GetFullPath(options.Preview);
        string extension = Path.GetExtension(previewPath).TrimStart('.').ToLowerInvariant();
        string format = string.IsNullOrEmpty(extension) ? "png" : extension;
        if (format == "greenshot")
        {
            throw new CliException("a preview is an image, use png, jpg, bmp, gif or tiff");
        }
        string previewFormat = ParseOutputFormat(format, null);

        using var preview = ImageTools.Preview(image, options.PreviewWidth, options.PreviewMax);
        string folder = Path.GetDirectoryName(previewPath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }
        ImageIO.SaveRenderedImage(preview, previewPath, true, new SurfaceOutputSettings(previewFormat, options.Quality), false);
        CheckWritten(previewPath, format);
        result.PreviewPath = previewPath;
        result.PreviewSize = preview.Size;
    }

    /// <summary>
    /// stdout: 'saved:' and 'size:' lines, or one line of JSON with --json
    /// </summary>
    private static void Print(OutputOptions options, SaveResult result, CaptureInfo info)
    {
        if (options.Json)
        {
            Console.WriteLine(Json.Object(
                ("path", Json.String(result.Path)),
                ("width", Json.Int(result.Width)),
                ("height", Json.Int(result.Height)),
                ("hwnd", info != null && info.Handle != IntPtr.Zero ? Json.String($"0x{info.Handle.ToInt64():x}") : null),
                ("pid", info != null && info.Handle != IntPtr.Zero ? Json.Int(info.ProcessId) : null),
                ("rect", info != null ? Json.Rect(info.ScreenRect) : null),
                ("mode", info != null ? Json.String(info.Mode) : null),
                ("preview", result.PreviewPath != null ? Json.String(result.PreviewPath) : null),
                ("preview_width", result.PreviewPath != null ? Json.Int(result.PreviewSize.Width) : null),
                ("preview_height", result.PreviewPath != null ? Json.Int(result.PreviewSize.Height) : null)));
            return;
        }

        Console.WriteLine($"saved: {result.Path}");
        Console.WriteLine($"size: {result.Width}x{result.Height}");
        if (result.PreviewPath != null)
        {
            Console.WriteLine($"preview: {result.PreviewPath}");
            Console.WriteLine($"preview-size: {result.PreviewSize.Width}x{result.PreviewSize.Height}");
        }
    }

    private static void CheckWritten(string fullPath, string format)
    {
        if (File.Exists(fullPath) && new FileInfo(fullPath).Length > 0)
        {
            return;
        }
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
        throw new CliException($"no file format handler could write {format}");
    }

    private static string ParseOutputFormat(string format, Annotations annotations)
    {
        string outputFormat = format switch
        {
            "png" => WellKnownFileFormats.Png,
            "jpg" or "jpeg" => WellKnownFileFormats.Jpg,
            "bmp" => WellKnownFileFormats.Bmp,
            "gif" => WellKnownFileFormats.Gif,
            "tif" or "tiff" => WellKnownFileFormats.Tiff,
            "greenshot" => WellKnownFileFormats.Greenshot,
            _ => throw new CliException($"unsupported format: {format}")
        };
        if (outputFormat == WellKnownFileFormats.Greenshot && annotations != null && annotations.HasEffects)
        {
            throw new CliException("image effects can't be kept editable, save to png, jpg, bmp, gif or tiff to use them");
        }
        return outputFormat;
    }

    /// <summary>
    /// Wait for an async capture call while pumping messages, so work it sends to the UI dispatcher (this thread) can run.
    /// </summary>
    internal static T Wait<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            _ = task.ContinueWith(_ => Application.ExitThread(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
            Application.Run();
        }
        // The task is completed here, so this can't deadlock
#pragma warning disable VSTHRD002
        return task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    private static void OpenInEditor(string fullPath)
    {
        string greenshotExe = Path.Combine(AppContext.BaseDirectory, "Greenshot.exe");
        if (!File.Exists(greenshotExe))
        {
            throw new CliException($"cannot open editor, {greenshotExe} not found");
        }
        Process.Start(greenshotExe, $"\"{fullPath}\"")?.Dispose();
    }

    private static string ResolveOutputPath(string output, ref string format)
    {
        if (string.IsNullOrEmpty(output))
        {
            format ??= "png";
            output = $"greenshot_{DateTime.Now:yyyyMMdd_HHmmss}.{(format == "jpeg" ? "jpg" : format)}";
        }
        else if (format == null)
        {
            string extension = Path.GetExtension(output).TrimStart('.').ToLowerInvariant();
            format = string.IsNullOrEmpty(extension) ? "png" : extension;
        }
        return Path.GetFullPath(output);
    }

    private static NativeRect GetMonitorBounds(string value)
    {
        int index = ParseInt("--monitor", value);
        var screens = Screen.AllScreens;
        if (index < 0 || index >= screens.Length)
        {
            throw new CliException($"monitor {index} does not exist, see 'greenshot-cli list'");
        }
        var bounds = screens[index].Bounds;
        return new NativeRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static NativeRect ParseRegion(string value)
    {
        var parts = value.Split(',');
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height)
            || width <= 0 || height <= 0)
        {
            throw new CliException("--region expects X,Y,W,H with positive W and H");
        }
        return new NativeRect(x, y, width, height);
    }

    private static int ParseInt(string option, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            throw new CliException($"invalid value for {option}: {value}");
        }
        return result;
    }
}
