using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gdi = System.Drawing;

namespace Anotador;

internal static class Screenshot
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);

    private static void CopyDesktop(Gdi.Bitmap bitmap, Gdi.Rectangle area)
    {
        const uint sourceCopyWithLayeredWindows = 0x40CC0020;
        using var destination = Gdi.Graphics.FromImage(bitmap);
        using var desktop = Gdi.Graphics.FromHwnd(IntPtr.Zero);
        IntPtr sourceDc = desktop.GetHdc();
        try
        {
            IntPtr destinationDc = destination.GetHdc();
            try
            {
                if (!BitBlt(destinationDc, 0, 0, area.Width, area.Height, sourceDc, area.X, area.Y,
                        sourceCopyWithLayeredWindows))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { destination.ReleaseHdc(destinationDc); }
        }
        finally { desktop.ReleaseHdc(sourceDc); }
    }

    internal static void WritePng(BitmapSource image, Stream output)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(output);
    }

    internal static string SaveAutomatic(BitmapSource image, string folder)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"Anotacao-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}.png");
        string temporary = path + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew))
                WritePng(image, output);
            File.Move(temporary, path);
            return path;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static BitmapSource ReadRegion(Overlay overlay, Rect selection)
    {
        var screen = overlay.Screen.Bounds;
        var dpi = VisualTreeHelper.GetDpi(overlay.Surface);
        Int32Rect pixels = PixelRegion(selection, dpi, screen.Width, screen.Height);
        var bounds = new Gdi.Rectangle(screen.X + pixels.X, screen.Y + pixels.Y, pixels.Width, pixels.Height);
        using var bitmap = new Gdi.Bitmap(bounds.Width, bounds.Height, Gdi.Imaging.PixelFormat.Format32bppRgb);
        CopyDesktop(bitmap, bounds);
        IntPtr handle = bitmap.GetHbitmap();
        try
        {
            BitmapSource region = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero,
                Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            region.Freeze();
            return region;
        }
        finally { DeleteObject(handle); }
    }

    internal static BitmapSource Render(Overlay overlay, bool includeMarks)
    {
        Gdi.Rectangle area = overlay.Screen.Bounds;
        using var bitmap = new Gdi.Bitmap(area.Width, area.Height, Gdi.Imaging.PixelFormat.Format32bppRgb);
        CopyDesktop(bitmap, area);
        IntPtr handle = bitmap.GetHbitmap();
        try
        {
            BitmapSource desktop = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero,
                Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            DpiScale dpi = VisualTreeHelper.GetDpi(overlay.Surface);
            Rect? selection = overlay.Surface.CaptureRegion;
            if (!includeMarks || overlay.Surface.Board.Marks.Count == 0)
            {
                return Crop(desktop, selection, dpi);
            }
            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawImage(desktop, new Rect(0, 0, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY));
                overlay.Surface.DrawMarks(context);
            }
            var result = new RenderTargetBitmap(area.Width, area.Height, dpi.PixelsPerInchX,
                dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            result.Render(visual);
            return Crop(result, selection, dpi);
        }
        finally { DeleteObject(handle); }
    }

    private static BitmapSource Crop(BitmapSource bitmap, Rect? selection, DpiScale dpi)
    {
        if (selection is Rect area)
            bitmap = new CroppedBitmap(bitmap, PixelRegion(area, dpi, bitmap.PixelWidth, bitmap.PixelHeight));
        bitmap.Freeze();
        return bitmap;
    }

    private static Int32Rect PixelRegion(Rect area, DpiScale dpi, int width, int height)
    {
        int left = Math.Clamp((int)Math.Floor(area.Left * dpi.DpiScaleX), 0, width - 1);
        int top = Math.Clamp((int)Math.Floor(area.Top * dpi.DpiScaleY), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(area.Right * dpi.DpiScaleX), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(area.Bottom * dpi.DpiScaleY), top + 1, height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }
}




