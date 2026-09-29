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
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Greenshot.Cli;

/// <summary>
/// The few Win32 calls the CLI needs itself. The process is per-monitor DPI aware (see the manifest), so every rectangle
/// and point here is in physical pixels.
/// </summary>
internal static class Native
{
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOWMINNOACTIVE = 7;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public const uint PW_RENDERFULLCONTENT = 0x00000002;
    public const uint GW_OWNER = 4;
    public const uint GA_ROOT = 2;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_CAPTION = 0x00C00000;
    public const long WS_POPUP = 0x80000000;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_TOPMOST = 0x00000008;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;

        public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public POINT MinPosition;
        public POINT MaxPosition;
        public RECT NormalPosition;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint command);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    /// <summary>
    /// The visible frame of the window (without the invisible resize borders), the rectangle Greenshot captures
    /// </summary>
    public static Rectangle GetFrameRectangle(IntPtr hWnd)
    {
        if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) == 0)
        {
            return frame.ToRectangle();
        }
        return GetWindowRectangle(hWnd);
    }

    /// <summary>
    /// The whole window including invisible borders (what PrintWindow renders)
    /// </summary>
    public static Rectangle GetWindowRectangle(IntPtr hWnd) => GetWindowRect(hWnd, out RECT rect) ? rect.ToRectangle() : Rectangle.Empty;

    /// <summary>
    /// The position a minimized window gets when it is restored
    /// </summary>
    public static Rectangle GetRestoredRectangle(IntPtr hWnd)
    {
        var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        return GetWindowPlacement(hWnd, ref placement) ? placement.NormalPosition.ToRectangle() : Rectangle.Empty;
    }

    /// <summary>
    /// Client area in screen coordinates
    /// </summary>
    public static Rectangle GetClientRectangleOnScreen(IntPtr hWnd)
    {
        if (!GetClientRect(hWnd, out RECT client))
        {
            return Rectangle.Empty;
        }
        var origin = new POINT();
        if (!ClientToScreen(hWnd, ref origin))
        {
            return Rectangle.Empty;
        }
        return new Rectangle(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    public static bool IsCloaked(IntPtr hWnd) => DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static long Style(IntPtr hWnd) => (uint) GetWindowLong(hWnd, GWL_STYLE);

    public static long ExStyle(IntPtr hWnd) => (uint) GetWindowLong(hWnd, GWL_EXSTYLE);

    public static string ClassName(IntPtr hWnd)
    {
        var text = new StringBuilder(256);
        return GetClassName(hWnd, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
    }

    public static uint ProcessId(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid;
    }

    public static Point CursorPosition() => GetCursorPos(out POINT point) ? new Point(point.X, point.Y) : Point.Empty;

    /// <summary>
    /// The top level window at a screen position
    /// </summary>
    public static IntPtr TopLevelWindowAt(Point point)
    {
        IntPtr hit = WindowFromPoint(new POINT { X = point.X, Y = point.Y });
        return hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GA_ROOT);
    }

    /// <summary>
    /// All top level windows, top of the z-order first
    /// </summary>
    public static System.Collections.Generic.List<IntPtr> TopLevelWindowsByZOrder()
    {
        var windows = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            windows.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>
    /// Give the focus back to a window, same trick as WindowDetails.ToForeground
    /// </summary>
    public static void ReturnFocus(IntPtr hWnd)
    {
        IntPtr foreground = GetForegroundWindow();
        if (hWnd == IntPtr.Zero || hWnd == foreground)
        {
            return;
        }
        uint threadCurrent = GetWindowThreadProcessId(foreground, out _);
        uint threadTarget = GetWindowThreadProcessId(hWnd, out _);
        if (threadCurrent != threadTarget)
        {
            AttachThreadInput(threadCurrent, threadTarget, true);
            SetForegroundWindow(hWnd);
            AttachThreadInput(threadCurrent, threadTarget, false);
        }
        else
        {
            SetForegroundWindow(hWnd);
        }
    }
}
