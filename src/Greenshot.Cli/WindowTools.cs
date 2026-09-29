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
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Dapplo.Windows.DesktopWindowsManager;
using Greenshot.Base.Core;
using Greenshot.Base.Core.Enums;

namespace Greenshot.Cli;

/// <summary>
/// A window as reported by 'list --json'. All rectangles are in physical screen pixels.
/// </summary>
internal sealed class WindowInfo
{
    public IntPtr Handle;
    public string Title;
    public int ProcessId;
    public string Exe;
    /// <summary>Visible frame of the window, for a minimized window the place where it comes back to</summary>
    public Rectangle Rect;
    /// <summary>Client area (without frame and title bar), empty for a minimized window</summary>
    public Rectangle Client;
    public bool Minimized;
    public bool Visible;

    public string ToJson() => Json.Object(
        ("hwnd", Json.String($"0x{Handle.ToInt64():x}")),
        ("title", Json.String(Title)),
        ("pid", Json.Int(ProcessId)),
        ("exe", Json.String(Exe)),
        ("rect", Json.Rect(Rect)),
        ("client", Json.Rect(Client)),
        ("minimized", Json.Bool(Minimized)),
        ("visible", Json.Bool(Visible)));

    public override string ToString() => $"0x{Handle.ToInt64():x} pid={ProcessId} \"{Title}\" {Exe}";
}

/// <summary>
/// Window listing and selection: by handle, title, process id or executable
/// </summary>
internal static class WindowTools
{
    public static WindowInfo Describe(WindowDetails window)
    {
        IntPtr handle = window.Handle;
        bool minimized = Native.IsIconic(handle) || window.Iconic;
        return new WindowInfo
        {
            Handle = handle,
            Title = window.Text,
            ProcessId = window.ProcessId,
            Exe = window.ProcessPath,
            Rect = minimized ? Native.GetRestoredRectangle(handle) : Native.GetFrameRectangle(handle),
            Client = minimized ? Rectangle.Empty : Native.GetClientRectangleOnScreen(handle),
            Minimized = minimized,
            Visible = Native.IsWindowVisible(handle)
        };
    }

    /// <summary>
    /// Find the window to capture. Without process filters several matching titles only give a warning and the first one is
    /// used (as it always was), with --window-pid or --window-exe the match has to be unique.
    /// </summary>
    public static WindowDetails Select(string value, int? processId, string exePattern)
    {
        bool strict = processId.HasValue || exePattern != null;
        Regex exe = exePattern != null ? WildcardToRegex(exePattern) : null;
        bool exeHasPath = exePattern != null && exePattern.IndexOfAny(new[] { '\\', '/' }) >= 0;

        bool Matches(WindowDetails window)
        {
            if (processId.HasValue && window.ProcessId != processId.Value)
            {
                return false;
            }
            if (exe != null)
            {
                string path = window.ProcessPath ?? string.Empty;
                string candidate = exeHasPath ? path.Replace('/', '\\') : Path.GetFileName(path);
                if (!exe.IsMatch(candidate))
                {
                    return false;
                }
            }
            return true;
        }

        string description = string.Join(" and ", new[]
        {
            value != null ? $"--window \"{value}\"" : null,
            processId.HasValue ? $"--window-pid {processId}" : null,
            exePattern != null ? $"--window-exe \"{exePattern}\"" : null
        }.Where(d => d != null));

        if (value != null && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long handle))
        {
            var window = new WindowDetails(new IntPtr(handle));
            if (!window.Visible && !window.Iconic)
            {
                throw new CliException($"window {value} does not exist or is not visible");
            }
            if (!Matches(window))
            {
                throw new CliException($"window {value} does not match {description}");
            }
            return window;
        }

        var matches = WindowDetails.GetTopLevelWindows()
            .Where(w => (value == null || w.Text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0) && Matches(w))
            .ToList();
        if (matches.Count == 0)
        {
            throw new CliException(strict
                ? $"no window matches {description}, see 'greenshot-cli list'"
                : $"no window title contains \"{value}\", see 'greenshot-cli list'");
        }
        if (matches.Count > 1)
        {
            if (strict)
            {
                var text = new StringBuilder($"{matches.Count} windows match {description}:");
                foreach (var match in matches)
                {
                    text.Append("\n  ").Append(Describe(match));
                }
                text.Append("\nnarrow it down with --window-pid, --window-exe or a longer --window title (or use the 0x... handle)");
                throw new CliException(text.ToString());
            }
            Console.Error.WriteLine($"warning: {matches.Count} windows match \"{value}\", using \"{matches[0].Text}\"");
        }
        return matches[0];
    }

    /// <summary>
    /// * matches anything, ? one character, case does not matter
    /// </summary>
    private static Regex WildcardToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern.Replace('/', '\\')).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>
/// What to capture of a window and how
/// </summary>
internal sealed class WindowCaptureRequest
{
    public WindowCaptureMode Mode = WindowCaptureMode.Auto;
    /// <summary>Capture only the client area</summary>
    public bool Client;
    /// <summary>Region relative to the window (or to its client area with Client), in pixels</summary>
    public Rectangle? Region;
    public bool IncludePopups;
    public bool NoActivate;
    public bool RestoreBehind;
    public int SettleMs = 500;
}

internal sealed class CaptureInfo
{
    public Image Image;
    /// <summary>What the image shows, in physical screen pixels</summary>
    public Rectangle ScreenRect;
    public string Mode;
    public IntPtr Handle;
    public int ProcessId;
}

internal static class WindowCapturer
{
    public static CaptureInfo Capture(WindowDetails window, WindowCaptureRequest request)
    {
        var selected = WindowCaptureHelper.SelectCaptureWindow(window) ?? throw new CliException($"window \"{window.Text}\" has nothing to capture");
        IntPtr handle = selected.Handle;
        IntPtr previousForeground = Native.GetForegroundWindow();
        bool minimized = Native.IsIconic(handle) || selected.Iconic;

        // menus and combos of the application close when it loses the focus, so popups always mean "do not activate"
        bool noActivate = request.NoActivate || request.RestoreBehind || request.IncludePopups;

        if (minimized && !request.RestoreBehind && noActivate)
        {
            throw new CliException($"window \"{selected.Text}\" is minimized and cannot be captured without restoring and activating it; " +
                                   "use --restore-behind to restore it behind the other windows, or leave out --no-activate");
        }

        WindowDetails.NoActivate = noActivate;
        try
        {
            Image image;
            Rectangle imageOrigin;
            Rectangle restoredClient = Rectangle.Empty;
            string mode;
            var popupImages = new List<(Image Image, Rectangle Rect)>();
            Rectangle frame;

            if (minimized && request.RestoreBehind)
            {
                (image, imageOrigin, frame, restoredClient) = CaptureMinimized(selected, request.SettleMs);
                mode = "printwindow";
            }
            else
            {
                WindowCaptureMode resolved;
                using (var process = selected.Process)
                {
                    resolved = ResolveMode(process, request.Mode);
                }
                mode = resolved.ToString().ToLowerInvariant();
                if (resolved == WindowCaptureMode.Screen && !minimized && noActivate)
                {
                    // without --no-activate the window is raised before the screen is copied, so there is nothing to warn about
                    WarnIfCovered(selected);
                }

                var capture = new Base.Core.Capture();
                capture.CaptureDetails.Title = selected.Text;
                var captured = WindowCaptureHelper.CaptureWindow(selected, capture, request.Mode);
                image = captured?.Image ?? throw new CliException("capture failed");
                frame = Native.GetFrameRectangle(handle);
                imageOrigin = new Rectangle(ImageOrigin(image, handle, frame, resolved == WindowCaptureMode.GDI), image.Size);

                if (request.IncludePopups)
                {
                    foreach (var popup in FindPopups(handle, frame))
                    {
                        var popupCapture = new Base.Core.Capture();
                        var popupResult = WindowCaptureHelper.CaptureWindow(new WindowDetails(popup), popupCapture, request.Mode);
                        if (popupResult?.Image == null)
                        {
                            continue;
                        }
                        Rectangle popupFrame = Native.GetFrameRectangle(popup);
                        var origin = ImageOrigin(popupResult.Image, popup, popupFrame, false);
                        popupImages.Add((popupResult.Image, new Rectangle(origin, popupResult.Image.Size)));
                    }
                }
            }

            try
            {
                // canvas with the window and the popups on top of it, at their real positions
                Rectangle canvasRect = imageOrigin;
                foreach (var popup in popupImages)
                {
                    canvasRect = Rectangle.Union(canvasRect, popup.Rect);
                }

                Image canvas = image;
                if (popupImages.Count > 0)
                {
                    canvas = new Bitmap(canvasRect.Width, canvasRect.Height, PixelFormat.Format32bppArgb);
                    using var graphics = Graphics.FromImage(canvas);
                    graphics.DrawImage(image, imageOrigin.X - canvasRect.X, imageOrigin.Y - canvasRect.Y, image.Width, image.Height);
                    foreach (var popup in popupImages)
                    {
                        graphics.DrawImage(popup.Image, popup.Rect.X - canvasRect.X, popup.Rect.Y - canvasRect.Y, popup.Image.Width, popup.Image.Height);
                    }
                    image.Dispose();
                }

                // what to keep of it, in screen coordinates
                Rectangle keep = canvasRect;
                // a window that was restored only for the capture is minimized again by now, its client area was read while it was restored
                Rectangle client = minimized ? restoredClient : Native.GetClientRectangleOnScreen(handle);
                if (request.Client)
                {
                    if (client.Width <= 0 || client.Height <= 0)
                    {
                        throw new CliException("cannot get the client area of the window");
                    }
                    keep = client;
                }
                if (request.Region.HasValue)
                {
                    Rectangle baseRect = request.Client ? client : frame;
                    var region = request.Region.Value;
                    keep = new Rectangle(baseRect.X + region.X, baseRect.Y + region.Y, region.Width, region.Height);
                }
                if (request.Client || request.Region.HasValue)
                {
                    Rectangle clipped = Rectangle.Intersect(keep, canvasRect);
                    // Intersect gives a rectangle without area, but not an empty one, when the rectangles only touch
                    if (clipped.Width <= 0 || clipped.Height <= 0)
                    {
                        throw new CliException(request.Region.HasValue
                            ? $"--region is outside of the captured {(request.Client ? "client area" : "window")} ({canvasRect.Width}x{canvasRect.Height})"
                            : "the client area is outside of the captured image");
                    }
                    if (clipped != keep && request.Region.HasValue)
                    {
                        Console.Error.WriteLine($"warning: --region reaches outside of the window, it was cut to {clipped.Width}x{clipped.Height}");
                    }
                    keep = clipped;
                    var cropped = Crop(canvas, new Rectangle(keep.X - canvasRect.X, keep.Y - canvasRect.Y, keep.Width, keep.Height));
                    canvas.Dispose();
                    canvas = cropped;
                }

                return new CaptureInfo
                {
                    Image = canvas,
                    ScreenRect = keep,
                    Mode = mode,
                    Handle = handle,
                    ProcessId = (int) Native.ProcessId(handle)
                };
            }
            finally
            {
                foreach (var popup in popupImages)
                {
                    popup.Image.Dispose();
                }
            }
        }
        finally
        {
            WindowDetails.NoActivate = false;
            if (noActivate && Native.GetForegroundWindow() != previousForeground && request.RestoreBehind)
            {
                Native.ReturnFocus(previousForeground);
                Console.Error.WriteLine("warning: the focus moved while the window was restored, it was given back");
            }
        }
    }

    /// <summary>
    /// The screen position of the top left corner of an image that was captured of a window: the visible frame, or the whole
    /// window rectangle with its invisible borders (what PrintWindow renders). GDI mode is PrintWindow, but the capture code
    /// replaces its result with a copy of the screen when that looks better, so in that mode the strip of the invisible
    /// border is looked at: PrintWindow leaves it black or transparent.
    /// </summary>
    private static Point ImageOrigin(Image image, IntPtr handle, Rectangle frame, bool gdi)
    {
        Rectangle full = Native.GetWindowRectangle(handle);
        if (image.Size == full.Size && image.Size != frame.Size)
        {
            return full.Location;
        }
        int border = frame.X - full.X;
        if (gdi && border > 2 && image.Size == frame.Size && image is Bitmap bitmap && StripIsBlank(bitmap, border, frame.Y - full.Y))
        {
            return full.Location;
        }
        return frame.Location;
    }

    private static bool StripIsBlank(Bitmap image, int width, int topBorder)
    {
        // the left strip below the top border: nothing there but black or transparent pixels
        int y0 = Math.Min(image.Height - 1, Math.Max(topBorder + 1, 0));
        for (int y = y0; y < image.Height; y += Math.Max(1, image.Height / 32))
        {
            for (int x = 0; x < Math.Min(width - 1, image.Width); x++)
            {
                var pixel = image.GetPixel(x, y);
                if (pixel.A != 0 && (pixel.R > 8 || pixel.G > 8 || pixel.B > 8))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// The mode the capture code is going to use, same rules as WindowCaptureHelper.CaptureWindow
    /// </summary>
    private static WindowCaptureMode ResolveMode(Process process, WindowCaptureMode mode)
    {
        bool dwm = DwmApi.IsDwmEnabled;
        if (mode == WindowCaptureMode.Auto)
        {
            mode = WindowCaptureMode.Screen;
            if (WindowCapture.IsGdiAllowed(process))
            {
                mode = WindowCaptureMode.GDI;
            }
            if (dwm && WindowCapture.IsDwmAllowed(process))
            {
                mode = WindowCaptureMode.Aero;
            }
        }
        else if (mode is WindowCaptureMode.Aero or WindowCaptureMode.AeroTransparent)
        {
            if (!dwm || !WindowCapture.IsDwmAllowed(process))
            {
                mode = WindowCapture.IsGdiAllowed(process) ? WindowCaptureMode.GDI : WindowCaptureMode.Screen;
            }
        }
        else if (mode == WindowCaptureMode.GDI && !WindowCapture.IsGdiAllowed(process))
        {
            mode = WindowCaptureMode.Screen;
        }
        return mode;
    }

    /// <summary>
    /// Screen captures show what is on the screen: say which windows are in front of the window
    /// </summary>
    private static void WarnIfCovered(WindowDetails window)
    {
        IntPtr handle = window.Handle;
        Rectangle frame = Native.GetFrameRectangle(handle);
        if (frame.IsEmpty)
        {
            return;
        }

        int covered = 0;
        var covers = new List<IntPtr>();
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                var point = new Point(frame.X + frame.Width * (2 * column + 1) / 6, frame.Y + frame.Height * (2 * row + 1) / 6);
                IntPtr top = Native.TopLevelWindowAt(point);
                if (top != IntPtr.Zero && top != handle && !IsOwnedBy(top, handle))
                {
                    covered++;
                    if (!covers.Contains(top))
                    {
                        covers.Add(top);
                    }
                }
            }
        }

        if (covered > 0)
        {
            string names = string.Join(", ", covers.Select(c => $"\"{new WindowDetails(c).Text}\" (0x{c.ToInt64():x})"));
            Console.Error.WriteLine($"warning: --mode screen captures what is on the screen, but window \"{window.Text}\" is {(covered == 9 ? "fully" : "partially")} " +
                                    $"covered by {names}; use --mode aero (or auto) to capture the window itself");
        }
    }

    private static bool IsOwnedBy(IntPtr window, IntPtr owner)
    {
        for (IntPtr current = Native.GetWindow(window, Native.GW_OWNER); current != IntPtr.Zero; current = Native.GetWindow(current, Native.GW_OWNER))
        {
            if (current == owner)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Menus, tooltips and combo lists are windows of their own: visible top level windows of the same process, in front of the
    /// window, that overlap it and look like popups (no title bar; popup style, tool window, not activatable or owned by it).
    /// Returned bottom to top.
    /// </summary>
    private static List<IntPtr> FindPopups(IntPtr window, Rectangle frame)
    {
        uint processId = Native.ProcessId(window);
        var all = Native.TopLevelWindowsByZOrder();
        int windowIndex = all.IndexOf(window);
        var popups = new List<IntPtr>();

        for (int i = 0; i < all.Count; i++)
        {
            IntPtr candidate = all[i];
            if (candidate == window || (windowIndex >= 0 && i > windowIndex))
            {
                continue;
            }
            if (Native.ProcessId(candidate) != processId || !Native.IsWindowVisible(candidate) || Native.IsIconic(candidate) || Native.IsCloaked(candidate))
            {
                continue;
            }
            Rectangle rect = Native.GetFrameRectangle(candidate);
            if (rect.Width <= 0 || rect.Height <= 0 || !frame.IntersectsWith(rect))
            {
                continue;
            }

            long style = Native.Style(candidate);
            long exStyle = Native.ExStyle(candidate);
            bool hasTitleBar = (style & Native.WS_CAPTION) == Native.WS_CAPTION;
            bool popupLike = (style & Native.WS_POPUP) != 0
                             || (exStyle & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)) != 0
                             || IsOwnedBy(candidate, window);
            if (!hasTitleBar && popupLike)
            {
                popups.Add(candidate);
            }
        }

        popups.Reverse();
        return popups;
    }

    /// <summary>
    /// Restore a minimized window without activating it, behind everything else, let it paint, grab it with PrintWindow and
    /// minimize it again
    /// </summary>
    private static (Image Image, Rectangle Origin, Rectangle Frame, Rectangle Client) CaptureMinimized(WindowDetails window, int settleMs)
    {
        IntPtr handle = window.Handle;
        Native.ShowWindow(handle, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(handle, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        try
        {
            // wait for the restore, then for the window to paint
            var timeout = Stopwatch.StartNew();
            while (Native.IsIconic(handle) && timeout.ElapsedMilliseconds < 2000)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }
            if (Native.IsIconic(handle))
            {
                throw new CliException($"window \"{window.Text}\" did not restore");
            }

            var settle = Stopwatch.StartNew();
            while (settle.ElapsedMilliseconds < settleMs)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            Rectangle full = Native.GetWindowRectangle(handle);
            Rectangle frame = Native.GetFrameRectangle(handle);
            Rectangle client = Native.GetClientRectangleOnScreen(handle);
            Image image = new WindowDetails(handle).PrintWindow() ?? throw new CliException("PrintWindow returned nothing for the window");
            return (image, new Rectangle(full.Location, image.Size), frame, client);
        }
        finally
        {
            Native.ShowWindow(handle, Native.SW_SHOWMINNOACTIVE);
        }
    }

    public static Bitmap Crop(Image source, Rectangle rect)
    {
        var result = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.DrawImage(source, new Rectangle(0, 0, rect.Width, rect.Height), rect.X, rect.Y, rect.Width, rect.Height, GraphicsUnit.Pixel);
        return result;
    }
}
