using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Anotador;

internal enum Tool { Pen, Arrow, Rectangle, Text, Eraser, Area, Lens }

internal sealed record Mark(Tool Tool, List<Point> Points, Color Color, string? Text = null, Rect? Clip = null)
{
    internal Rect Bounds => new(Points[0], Points[^1]);

    private FormattedText Format(double scale, Color? highlight = null) => new(Text!, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 24,
        new SolidColorBrush(highlight ?? Color), scale);

    internal void Draw(DrawingContext context, double scale, Color? highlight = null)
    {
        if (Clip is Rect clip) context.PushClip(new RectangleGeometry(clip));
        try { DrawContent(context, scale, highlight); }
        finally { if (Clip.HasValue) context.Pop(); }
    }

    private void DrawContent(DrawingContext context, double scale, Color? highlight)
    {
        Point start = Points[0], end = Points[^1];
        var brush = new SolidColorBrush(highlight ?? Color);
        var pen = new Pen(brush, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round };
        switch (Tool)
        {
            case Tool.Pen:
                if (Points.Count == 1)
                {
                    context.DrawEllipse(brush, null, start, 2, 2);
                    return;
                }
                var path = new StreamGeometry();
                using (var geometry = path.Open())
                {
                    geometry.BeginFigure(start, false, false);
                    geometry.PolyLineTo(Points.GetRange(1, Points.Count - 1), true, false);
                }
                context.DrawGeometry(null, pen, path);
                break;
            case Tool.Arrow:
                context.DrawLine(pen, start, end);
                if (ArrowHead(start, end) is { } head)
                {
                    context.DrawLine(pen, end, head.Left);
                    context.DrawLine(pen, end, head.Right);
                }
                break;
            case Tool.Rectangle:
                context.DrawRectangle(null, pen, Bounds);
                break;
            case Tool.Text:
                context.DrawText(Format(scale, highlight), start);
                break;
        }
    }

    private static (Point Left, Point Right)? ArrowHead(Point start, Point end)
    {
        Vector axis = end - start;
        if (axis.Length < 1) return null;
        double length = Math.Min(20, axis.Length * 0.45);
        axis.Normalize();
        var normal = new Vector(-axis.Y, axis.X) * (length * 0.5);
        Point basePoint = end - axis * length;
        return (basePoint + normal, basePoint - normal);
    }

    private static bool NearSegment(Point point, Point a, Point b)
    {
        Vector span = b - a;
        double fraction = span.LengthSquared == 0 ? 0
            : Math.Clamp(Vector.Multiply(point - a, span) / span.LengthSquared, 0, 1);
        return (point - (a + fraction * span)).LengthSquared <= 144;
    }

    internal bool Hit(Point point, double scale)
    {
        if (Clip is Rect clip && !clip.Contains(point)) return false;
        Point a = Points[0], b = Points[^1];
        switch (Tool)
        {
            case Tool.Text:
                var text = Format(scale);
                return new Rect(a, new Size(text.WidthIncludingTrailingWhitespace, text.Height)).Contains(point);
            case Tool.Rectangle:
                Rect r = Bounds;
                return NearSegment(point, r.TopLeft, r.TopRight) || NearSegment(point, r.TopRight, r.BottomRight)
                    || NearSegment(point, r.BottomRight, r.BottomLeft) || NearSegment(point, r.BottomLeft, r.TopLeft);
            case Tool.Arrow:
                return NearSegment(point, a, b) || (ArrowHead(a, b) is { } head
                    && (NearSegment(point, b, head.Left) || NearSegment(point, b, head.Right)));
            default:
                if (Points.Count == 1) return NearSegment(point, a, a);
                for (int i = 1; i < Points.Count; i++)
                    if (NearSegment(point, Points[i - 1], Points[i])) return true;
                return false;
        }
    }
}

internal sealed class Board
{
    internal Rect? Region { get; set; }
    internal List<Mark> Marks { get; private set; } = new();
    private readonly List<List<Mark>> history = new();

    internal void Save()
    {
        if (history.Count == 100) history.RemoveAt(0);
        history.Add(new List<Mark>(Marks));
    }

    internal void Undo()
    {
        if (history.Count == 0) return;
        Marks = history[^1];
        history.RemoveAt(history.Count - 1);
    }
}

internal sealed class DrawingSurface : FrameworkElement
{
    private readonly Controller controller;
    internal readonly Board Board;
    private Mark? preview;
    private bool erasing;
    private bool savedErase;
    private Point? pointer;
    private Point? areaStart;
    private Rect? areaPreview;
    internal bool Interactive;
    internal System.Drawing.Rectangle MonitorBounds;
    internal System.Drawing.Rectangle WorkingArea;

    private Rect DesktopArea()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var bounds = System.Drawing.Rectangle.Intersect(MonitorBounds, WorkingArea);
        if (bounds.IsEmpty) return new Rect(RenderSize);
        return new Rect((bounds.Left - MonitorBounds.Left) / dpi.DpiScaleX,
            (bounds.Top - MonitorBounds.Top) / dpi.DpiScaleY,
            bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY);
    }

    internal Rect? CaptureRegion => Board.Region is Rect region
        && Rect.Intersect(region, DesktopArea()) is Rect clipped && clipped.Width >= 1 && clipped.Height >= 1
        ? clipped : null;

    private Rect InputArea => !controller.SelectingRegion && CaptureRegion is Rect region
        ? region : DesktopArea();

    internal DrawingSurface(Controller controller, Board board)
    {
        this.controller = controller;
        Board = board;
        Cursor = Cursors.Cross;
        MouseLeftButtonDown += Down;
        MouseMove += Move;
        MouseEnter += (_, e) => TrackPointer(e.GetPosition(this));
        MouseLeave += (_, _) => TrackPointer(null);
        MouseLeftButtonUp += Up;
        LostMouseCapture += (_, _) => Cancel();
    }

    internal void DrawMarks(DrawingContext context, int highlighted = -1, Rect? clip = null)
    {
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        context.PushClip(new RectangleGeometry(clip ?? DesktopArea()));
        for (int index = 0; index < Board.Marks.Count; index++)
        {
            Mark mark = Board.Marks[index];
            Color? highlight = index == highlighted
                ? (mark.Color.G > 150 && mark.Color.B > 150 ? Colors.DeepPink : Colors.Cyan)
                : null;
            mark.Draw(context, scale, highlight);
        }
        context.Pop();
    }

    protected override void OnRender(DrawingContext context)
    {
        Rect input = InputArea;
        if (Interactive)
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, input);
        int highlighted = Interactive && controller.SelectedTool == Tool.Eraser && pointer is Point point
            ? EraserTarget(point) : -1;
        DrawMarks(context, highlighted, Interactive ? input : null);
        if (preview != null)
        {
            context.PushClip(new RectangleGeometry(input));
            preview.Draw(context, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            context.Pop();
        }
        if (Interactive && (areaPreview ?? CaptureRegion) is Rect boundary)
        {
            // Draw just inside the input region, leaving the surrounding desktop clickable.
            boundary.Inflate(-1, -1);
            if (!boundary.IsEmpty)
            {
                context.DrawRectangle(null, new Pen(Brushes.Black, 2), boundary);
                context.DrawRectangle(null, new Pen(Brushes.DeepSkyBlue, 2) { DashStyle = DashStyles.Dash }, boundary);
            }
        }
    }

    internal void Cancel()
    {
        preview = null;
        areaStart = null;
        areaPreview = null;
        pointer = null;
        erasing = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (!Interactive || controller.Busy) return;
        Point p = e.GetPosition(this);
        if (!InputArea.Contains(p)) return;
        controller.FocusToolbar();
        e.Handled = true;
        if (controller.SelectingRegion)
        {
            if (CaptureMouse()) areaStart = p;
            return;
        }
        controller.ActiveSurface = this;
        if (controller.SelectedTool == Tool.Text)
        {
            string? value = controller.AskText();
            if (value == null) return;
            Board.Save();
            Board.Marks.Add(new Mark(Tool.Text, new() { p }, controller.Ink, value, CaptureRegion));
            InvalidateVisual();
            return;
        }
        if (!CaptureMouse()) return;
        if (controller.SelectedTool == Tool.Eraser)
        {
            erasing = true;
            savedErase = false;
            Erase(p);
        }
        else
        {
            preview = new Mark(controller.SelectedTool, new() { p }, controller.Ink, Clip: CaptureRegion);
            if (preview.Tool != Tool.Pen) preview.Points.Add(p);
            InvalidateVisual();
        }
    }

    private int EraserTarget(Point point)
    {
        if (!InputArea.Contains(point)) return -1;
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return Board.Marks.FindLastIndex(mark => mark.Hit(point, scale));
    }

    internal void ClearRegion()
    {
        if (CaptureRegion is not Rect region) return;
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        List<Mark> contained = Board.Marks.FindAll(mark =>
        {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open()) mark.Draw(context, scale);
            return region.Contains(drawing.Bounds);
        });
        if (contained.Count == 0) return;
        Board.Save();
        foreach (Mark mark in contained) Board.Marks.Remove(mark);
        InvalidateVisual();
    }

    private void TrackPointer(Point? point)
    {
        pointer = point;
        if (Interactive && controller.SelectedTool == Tool.Eraser) InvalidateVisual();
    }

    private void Erase(Point p)
    {
        int index = EraserTarget(p);
        if (index < 0) return;
        if (!savedErase) Board.Save();
        savedErase = true;
        Board.Marks.RemoveAt(index);
        InvalidateVisual();
    }

    private Rect SelectionRect(Point start, Point end) => Rect.Intersect(new Rect(start, end), DesktopArea());

    private void UpdatePreview(Point p)
    {
        if (preview == null) return;
        if (preview.Tool == Tool.Pen)
        {
            if ((p - preview.Points[^1]).LengthSquared >= 1) preview.Points.Add(p);
        }
        else preview.Points[^1] = p;
        InvalidateVisual();
    }

    private void Move(object sender, MouseEventArgs e)
    {
        Point p = e.GetPosition(this);
        TrackPointer(p);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        if (areaStart is Point start)
        {
            areaPreview = SelectionRect(start, p);
            InvalidateVisual();
        }
        else if (erasing) Erase(p);
        else UpdatePreview(p);
    }

    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        Point p = e.GetPosition(this);
        if (areaStart is Point start)
        {
            Rect area = SelectionRect(start, p);
            Cancel();
            if (area.Width >= 8 && area.Height >= 8)
            {
                if (controller.SelectedTool == Tool.Lens) _ = controller.SearchLens(this, area);
                else controller.SelectArea(this, area);
            }
            e.Handled = true;
            return;
        }
        if (erasing) Erase(p);
        else if (preview != null)
        {
            UpdatePreview(p);
            if (preview.Tool == Tool.Pen || (preview.Points[^1] - preview.Points[0]).LengthSquared >= 4)
            {
                Board.Save();
                Board.Marks.Add(preview);
            }
        }
        Cancel();
        if (IsMouseOver) TrackPointer(p);
        e.Handled = true;
    }
}

internal sealed class Overlay : Window
{
    internal Forms.Screen Screen { get; }
    internal DrawingSurface Surface { get; }

    internal Overlay(Controller controller, Forms.Screen screen, Board board)
    {
        Screen = screen;
        Surface = new DrawingSurface(controller, board)
        {
            MonitorBounds = screen.Bounds,
            WorkingArea = screen.WorkingArea
        };
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Content = Surface;
        SetMode(false);
    }

    internal void SetMode(bool drawing)
    {
        Surface.Cancel();
        Surface.Interactive = drawing;
        Native.SetClickThrough(this, !drawing);
        Surface.InvalidateVisual();
    }

    internal void Reveal()
    {
        if (!IsVisible)
        {
            Native.Position(this, Screen.Bounds);
            Show();
        }
        Native.Position(this, Screen.Bounds);
    }
}





