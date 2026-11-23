using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Anotador;

internal sealed class CaptureNotice : Window
{
    private readonly Forms.Screen screen;
    private readonly DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(9) };

    internal CaptureNotice(BitmapSource image, string path, Forms.Screen screen)
    {
        this.screen = screen;
        Title = "Captura salva";
        Width = 340;
        Height = 248;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(24, 34, 47));

        var layout = new Grid { Margin = new Thickness(14) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new DockPanel();
        var dismiss = new Button { Content = "Fechar", Padding = new Thickness(8, 2, 8, 2) };
        dismiss.Click += (_, _) => Close();
        DockPanel.SetDock(dismiss, Dock.Right);
        heading.Children.Add(dismiss);
        heading.Children.Add(new TextBlock
        {
            Text = "Captura salva",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });
        layout.Children.Add(heading);

        var preview = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(0, 10, 0, 10) };
        Grid.SetRow(preview, 1);
        layout.Children.Add(preview);
        var open = new Button { Content = "Abrir imagem", Padding = new Thickness(10, 5, 10, 5) };
        open.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Close();
            }
            catch (Win32Exception error)
            {
                MessageBox.Show(this, error.Message, "Abrir imagem");
            }
        };
        Grid.SetRow(open, 2);
        layout.Children.Add(open);
        Content = layout;

        timeout.Tick += (_, _) => Close();
        MouseEnter += (_, _) => timeout.Stop();
        MouseLeave += (_, _) => timeout.Start();
        Closed += (_, _) => timeout.Stop();
    }

    internal void ShowAtCorner()
    {
        var area = screen.WorkingArea;
        double scale = Native.MonitorScale(new System.Drawing.Point(area.Left, area.Top));
        int width = (int)Math.Ceiling(Width * scale);
        int height = (int)Math.Ceiling(Height * scale);
        int margin = (int)Math.Ceiling(14 * scale);
        var bounds = new System.Drawing.Rectangle(Math.Max(area.Left, area.Right - width - margin),
            Math.Max(area.Top, area.Bottom - height - margin), width, height);
        Native.Position(this, bounds);
        Show();
        Native.Position(this, bounds);
        timeout.Start();
    }
}
