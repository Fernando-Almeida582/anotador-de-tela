using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Anotador;

internal static class ToolCursor
{
    internal static Cursor Create(string symbol)
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var tip = Geometry.Parse("M2,0 V8 M0,2 H8");
            var icon = Geometry.Parse(symbol).Clone();
            icon.Transform = new TranslateTransform(10, 9);
            foreach (Geometry geometry in new[] { tip, icon })
            {
                context.DrawGeometry(null, Outline(Brushes.Black, 3.5), geometry);
                context.DrawGeometry(null, Outline(Brushes.White, 1.4), geometry);
            }
        }

        var image = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var png = new MemoryStream();
        encoder.Save(png);
        using var cursor = new MemoryStream();
        using var writer = new BinaryWriter(cursor);
        writer.Write((ushort)0);
        writer.Write((ushort)2); // CUR
        writer.Write((ushort)1);
        writer.Write((byte)size);
        writer.Write((byte)size);
        writer.Write((ushort)0);
        writer.Write((ushort)2); // Hotspot at the crosshair.
        writer.Write((ushort)2);
        writer.Write((uint)png.Length);
        writer.Write(22u);
        png.WriteTo(cursor);
        cursor.Position = 0;
        return new Cursor(cursor, scaleWithDpi: true);
    }

    private static Pen Outline(Brush brush, double width) => new(brush, width)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round
    };
}

