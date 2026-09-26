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
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Dapplo.Windows.Common.Structs;
using Greenshot.Base.Core;
using Greenshot.Base.Core.Enums;
using Greenshot.Base.Interfaces;
using Greenshot.Base.Interfaces.Plugin;
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
        "  greenshot-cli list                                        list monitors and capturable windows\n" +
        "  greenshot-cli capture [target] [options] [annotations]    take a screenshot, annotate it and save it\n" +
        "  greenshot-cli edit INPUT [options] [annotations]          annotate an image (png, jpg, bmp, gif, tiff, .greenshot)\n" +
        "\n" +
        "target (default is --fullscreen):\n" +
        "  --fullscreen          all monitors\n" +
        "  --monitor N           monitor index from 'list'\n" +
        "  --active              current foreground window\n" +
        "  --window W            window handle (0x...) or case-insensitive title substring\n" +
        "  --region X,Y,W,H      rectangle in virtual screen coordinates\n" +
        "\n" +
        "options:\n" +
        "  -o, --output FILE     output file, format from extension (capture default: greenshot_<timestamp>.png in the\n" +
        "                        current folder, edit default: overwrite INPUT)\n" +
        "  --format F            png, jpg, bmp, gif, tiff, greenshot (overrides the extension)\n" +
        "  --quality N           jpg quality 1-100 (default 80)\n" +
        "  --delay SEC           capture only: wait before capturing (e.g. 1.5)\n" +
        "  --mode M              capture only: window capture mode auto (default), aero, aerotransparent, gdi, screen\n" +
        "  --clipboard           also copy the image to the clipboard\n" +
        "  --open                open the screenshot in the Greenshot editor afterwards\n" +
        "\n" +
        Annotations.Usage +
        "\n" +
        "output (stdout): 'saved: PATH' and 'size: WxH'\n" +
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

            // In-memory configuration with defaults, the capture code reads its settings from it
            IniConfigHelper.EnsureInitialized();
            // The editor elements read their default colors, fonts etc. from here, also in memory with defaults
            IniConfigHelper.EnsureSection<IEditorConfiguration>(() => new EditorConfigurationImpl());
            // ImageIO saves through the registered file format handlers, Greenshot.exe registers them in EditorInitialize
            SimpleServiceProvider.Current.AddService<IFileFormatHandler>(new DefaultFileFormatHandler());
            // .greenshot files keep the elements editable, loading one needs to know how to make a surface
            SimpleServiceProvider.Current.AddService<IFileFormatHandler>(new GreenshotFileFormatHandler());
            SimpleServiceProvider.Current.AddService<Func<ISurface>>(() => new Surface());

            switch (args[0])
            {
                case "list":
                    return List();
                case "capture":
                    return Capture(args.Skip(1).ToArray());
                case "edit":
                    return Edit(args.Skip(1).ToArray());
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

    private static int List()
    {
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var bounds = screens[i].Bounds;
            Console.WriteLine($"monitor {i}: {bounds.Width}x{bounds.Height} at {bounds.X},{bounds.Y} {screens[i].DeviceName}{(screens[i].Primary ? " (primary)" : "")}");
        }

        foreach (var window in WindowDetails.GetTopLevelWindows())
        {
            var rect = window.WindowRectangle;
            Console.WriteLine($"window 0x{window.Handle.ToInt64():x}: {rect.Width}x{rect.Height} pid={window.ProcessId}{(window.Iconic ? " minimized" : "")} \"{window.Text}\"");
        }
        return 0;
    }

    private static int Capture(string[] args)
    {
        string target = "fullscreen";
        string targetValue = null;
        string output = null;
        string format = null;
        int quality = 80;
        double delay = 0;
        var mode = WindowCaptureMode.Auto;
        bool clipboard = false;
        bool open = false;
        int targets = 0;
        var annotations = new Annotations();

        for (int i = 0; i < args.Length; i++)
        {
            if (annotations.TryParse(args, ref i))
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
                case "--fullscreen":
                case "--active":
                    target = arg.Substring(2);
                    targets++;
                    break;
                case "--monitor":
                case "--window":
                case "--region":
                    target = arg.Substring(2);
                    targetValue = NextValue();
                    targets++;
                    break;
                case "-o":
                case "--output":
                    output = NextValue();
                    break;
                case "--format":
                    format = NextValue().ToLowerInvariant();
                    break;
                case "--quality":
                    quality = ParseInt(arg, NextValue());
                    if (quality < 1 || quality > 100)
                    {
                        throw new CliException("--quality must be 1-100");
                    }
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
                case "--clipboard":
                    clipboard = true;
                    break;
                case "--open":
                    open = true;
                    break;
                default:
                    throw new CliException($"unknown argument: {arg}");
            }
        }

        if (targets > 1)
        {
            throw new CliException("use only one of --fullscreen, --monitor, --active, --window or --region");
        }

        string fullPath = ResolveOutputPath(output, ref format);
        OutputFormat outputFormat = ParseOutputFormat(format, annotations);

        // resolve window before the delay, so a wrong title fails fast
        WindowDetails window = target switch
        {
            "window" => FindWindow(targetValue),
            _ => null
        };

        if (delay > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(delay));
        }

        if (target == "active")
        {
            window = WindowDetails.GetActiveWindow() ?? throw new CliException("no active window");
        }

        ICapture capture = target switch
        {
            "fullscreen" => WindowCapture.CaptureScreen(new Base.Core.Capture()),
            "monitor" => WindowCapture.CaptureRectangle(new Base.Core.Capture(), GetMonitorBounds(targetValue)),
            "region" => WindowCapture.CaptureRectangle(new Base.Core.Capture(), ParseRegion(targetValue)),
            _ => CaptureWindow(window, mode)
        };

        if (capture?.Image == null)
        {
            throw new CliException("capture failed");
        }

        // the surface owns the image from here on
        using (var surface = new Surface(capture.Image))
        {
            Save(surface, annotations, fullPath, format, outputFormat, quality, clipboard);
        }

        if (open)
        {
            OpenInEditor(fullPath);
        }
        return 0;
    }

    private static int Edit(string[] args)
    {
        string input = null;
        string output = null;
        string format = null;
        int quality = 80;
        bool clipboard = false;
        bool open = false;
        var annotations = new Annotations();

        for (int i = 0; i < args.Length; i++)
        {
            if (annotations.TryParse(args, ref i))
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
                case "-o":
                case "--output":
                    output = NextValue();
                    break;
                case "--format":
                    format = NextValue().ToLowerInvariant();
                    break;
                case "--quality":
                    quality = ParseInt(arg, NextValue());
                    if (quality < 1 || quality > 100)
                    {
                        throw new CliException("--quality must be 1-100");
                    }
                    break;
                case "--clipboard":
                    clipboard = true;
                    break;
                case "--open":
                    open = true;
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

        if (input == null)
        {
            throw new CliException("edit needs an input image, see 'greenshot-cli help'");
        }
        string inputPath = Path.GetFullPath(input);
        if (!File.Exists(inputPath))
        {
            throw new CliException($"{inputPath} does not exist");
        }

        string fullPath = ResolveOutputPath(output ?? inputPath, ref format);
        OutputFormat outputFormat = ParseOutputFormat(format, annotations);

        using (var surface = LoadSurface(inputPath))
        {
            Save(surface, annotations, fullPath, format, outputFormat, quality, clipboard);
        }

        if (open)
        {
            OpenInEditor(fullPath);
        }
        return 0;
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
    /// Apply the annotations and save: a .greenshot keeps the elements editable, anything else is the rendered image
    /// </summary>
    private static void Save(Surface surface, Annotations annotations, string fullPath, string format, OutputFormat outputFormat, int quality, bool clipboard)
    {
        annotations.ApplyTo(surface);
        var settings = new SurfaceOutputSettings(outputFormat, quality);

        if (outputFormat == OutputFormat.greenshot)
        {
            ImageIO.Save(surface, fullPath, true, settings, false);
            CheckWritten(fullPath, format);
            if (clipboard)
            {
                using var rendered = surface.GetImageForExport();
                Clipboard.SetImage(rendered);
            }
            Console.WriteLine($"saved: {fullPath}");
            Console.WriteLine($"size: {surface.Image.Width}x{surface.Image.Height}");
            return;
        }

        using Image image = annotations.ApplyEffects(surface.GetImageForExport());
        ImageIO.SaveRenderedImage(image, fullPath, true, settings, false);
        CheckWritten(fullPath, format);
        if (clipboard)
        {
            Clipboard.SetImage(image);
        }
        Console.WriteLine($"saved: {fullPath}");
        Console.WriteLine($"size: {image.Width}x{image.Height}");
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

    private static OutputFormat ParseOutputFormat(string format, Annotations annotations)
    {
        OutputFormat outputFormat = format switch
        {
            "png" => OutputFormat.png,
            "jpg" or "jpeg" => OutputFormat.jpg,
            "bmp" => OutputFormat.bmp,
            "gif" => OutputFormat.gif,
            "tif" or "tiff" => OutputFormat.tiff,
            "greenshot" => OutputFormat.greenshot,
            _ => throw new CliException($"unsupported format: {format}")
        };
        if (outputFormat == OutputFormat.greenshot && annotations.HasEffects)
        {
            throw new CliException("image effects can't be kept editable, save to png, jpg, bmp, gif or tiff to use them");
        }
        return outputFormat;
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

    private static ICapture CaptureWindow(WindowDetails window, WindowCaptureMode mode)
    {
        var selected = WindowCaptureHelper.SelectCaptureWindow(window) ?? throw new CliException($"window \"{window.Text}\" has nothing to capture");
        var capture = new Base.Core.Capture();
        capture.CaptureDetails.Title = selected.Text;
        return WindowCaptureHelper.CaptureWindow(selected, capture, mode);
    }

    private static WindowDetails FindWindow(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long handle))
        {
            var window = new WindowDetails(new IntPtr(handle));
            if (!window.Visible && !window.Iconic)
            {
                throw new CliException($"window {value} does not exist or is not visible");
            }
            return window;
        }

        var matches = WindowDetails.GetTopLevelWindows()
            .Where(w => w.Text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        if (matches.Count == 0)
        {
            throw new CliException($"no window title contains \"{value}\", see 'greenshot-cli list'");
        }
        if (matches.Count > 1)
        {
            Console.Error.WriteLine($"warning: {matches.Count} windows match \"{value}\", using \"{matches[0].Text}\"");
        }
        return matches[0];
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
