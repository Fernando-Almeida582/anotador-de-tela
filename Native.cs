using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Anotador;

internal static class Native
{
    internal const uint AltControl = 0x4003;
    internal const uint NoActivate = 0x0010;
    internal const uint NoOwnerZOrder = 0x0200;
    internal static readonly IntPtr Topmost = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    internal static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmFlush();

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(System.Drawing.Point point, uint flags);

    internal static double MonitorScale(System.Drawing.Point point)
    {
        return GetDpiForMonitor(MonitorFromPoint(point, 2), 0, out uint x, out _) == 0
            ? x / 96.0 : 1;
    }

    internal static void SetClickThrough(Window window, bool enabled)
    {
        const int extendedStyle = -20;
        const int transparent = 0x20;
        const int noActivate = 0x08000000;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        int style = GetWindowLong(handle, extendedStyle) | noActivate;
        SetWindowLong(handle, extendedStyle, enabled ? style | transparent : style & ~transparent);
    }

    internal static void Raise(Window window)
    {
        const uint noSizeOrMove = 0x0003;
        SetWindowPos(new WindowInteropHelper(window).Handle, Topmost,
            0, 0, 0, 0, noSizeOrMove | NoActivate | NoOwnerZOrder);
    }

    internal static void Position(Window window, System.Drawing.Rectangle bounds)
    {
        SetWindowPos(new WindowInteropHelper(window).EnsureHandle(), Topmost,
            bounds.X, bounds.Y, bounds.Width, bounds.Height, NoActivate | NoOwnerZOrder);
    }
}


