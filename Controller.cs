using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Gdi = System.Drawing;

namespace Anotador;

internal sealed class Controller
{
    private const int DrawHotkey = 1;
    private const int VisibilityHotkey = 2;
    private const int FinishHotkey = 3;
    private const int RecordingHotkey = 4;
    private const uint ExcludeFromCapture = 0x11;
    private CancellationTokenSource? recordingStop;
    private Task? recording;
    private Button recordingButton = null!;
    private CaptureNotice? notice;
    private readonly Application app;
    private readonly HwndSource messages;
    private readonly Forms.NotifyIcon tray;
    private readonly Gdi.Icon trayIcon;
    private readonly Window toolbar;
    private readonly System.Windows.Controls.Primitives.UniformGrid controls = new() { Columns = 4 };
    private readonly Dictionary<Tool, Button> toolButtons = new();
    private readonly Dictionary<Tool, Cursor> toolCursors = new();
    private Board? restrictedBoard;
    private Button? clearButton;
    private Button? modeButton;
    private bool toolbarOpen;
    private readonly Dictionary<string, Board> boards = new();
    private readonly List<Overlay> overlays = new();
    private readonly Dictionary<int, string> hotkeys = new();
    private bool drawing;
    private bool visible = true;
    private bool displayUpdatePending;
    private bool shuttingDown;

    internal bool Busy { get; private set; }
    internal DrawingSurface? ActiveSurface { get; set; }
    internal Tool SelectedTool { get; private set; } = Tool.Pen;
    internal bool SelectingRegion => SelectedTool is Tool.Area or Tool.Lens;
    internal Color Ink { get; private set; } = Color.FromRgb(255, 69, 90);

    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var controller = new Controller(app);
        app.Exit += (_, _) => controller.Dispose();
        app.Run();
    }

    private Controller(Application app)
    {
        this.app = app;
        toolbar = BuildToolbar();
        using (Stream iconStream = OpenIcon())
        using (var icon = new Gdi.Icon(iconStream, Forms.SystemInformation.SmallIconSize))
            trayIcon = (Gdi.Icon)icon.Clone();
        using (Stream iconStream = OpenIcon())
            toolbar.Icon = BitmapFrame.Create(iconStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        messages = new HwndSource(new HwndSourceParameters("Anotador") { ParentWindow = new IntPtr(-3) });
        messages.AddHook(ReceiveMessage);
        SetHotkey(DrawHotkey, 'A', 'D');
        SetHotkey(VisibilityHotkey, 'H', 'V');
        SetHotkey(FinishHotkey, 'S', 'G');
        SetHotkey(RecordingHotkey, 'R', 'F');

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Label("Ativar ou pausar desenho", DrawHotkey), null, (_, _) => Post(ToggleDrawing));
        menu.Items.Add(Label("Ocultar ou mostrar marcas", VisibilityHotkey), null, (_, _) => Post(ToggleVisibility));
        menu.Items.Add(Label("Concluir e salvar", FinishHotkey), null, (_, _) => Post(() => _ = Capture(false, finish: true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Copiar captura do monitor sob o cursor", null, (_, _) => Post(() => _ = Capture(false, true)));
        menu.Items.Add("Salvar captura do monitor sob o cursor…", null, (_, _) => Post(() => _ = Capture(true, true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        var recordScreen = new Forms.ToolStripMenuItem("Gravar monitor inteiro");
        recordScreen.Click += (_, _) => Post(() => StartRecording(false));
        var recordRegion = new Forms.ToolStripMenuItem("Gravar área selecionada");
        recordRegion.Click += (_, _) => Post(() => StartRecording(true));
        var stopVideo = new Forms.ToolStripMenuItem(Label("Parar gravação", RecordingHotkey));
        stopVideo.Click += (_, _) => Post(() => recordingStop?.Cancel());
        menu.Items.AddRange(new Forms.ToolStripItem[] { recordScreen, recordRegion, stopVideo });
        menu.Opening += (_, _) =>
        {
            recordScreen.Enabled = !Busy && recording == null;
            recordRegion.Enabled = recordScreen.Enabled && SelectedRegionOverlay() != null;
            stopVideo.Enabled = recordingStop is { IsCancellationRequested: false };
        };
        menu.Items.Add(new Forms.ToolStripSeparator());
        var startupItem = new Forms.ToolStripMenuItem("Adicionar à inicialização do Windows");
        startupItem.Click += (_, _) => Post(ToggleStartup);
        menu.Items.Add(startupItem);
        menu.Opening += (_, _) => RefreshStartupMenu(startupItem);
        menu.Items.Add("Sair", null, (_, _) => Post(Shutdown));
        tray = new Forms.NotifyIcon
        {
            Text = "Anotador de tela",
            Icon = trayIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Post(ToggleDrawing);
        };
        RefreshMonitors();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    private static Stream OpenIcon() => typeof(Controller).Assembly.GetManifestResourceStream("Anotador.Icon")!;

    private static void RefreshStartupMenu(Forms.ToolStripMenuItem item)
    {
        bool registered = false;
        try { registered = StartupRegistration.IsRegistered(); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }

        item.Text = registered ? "Remover da inicialização do Windows" : "Adicionar à inicialização do Windows";
        item.Checked = registered;
    }

    private void ToggleStartup()
    {
        try
        {
            bool enabled = StartupRegistration.Toggle();
            tray.ShowBalloonTip(2500, "Anotador", enabled
                ? "O Anotador abrirá na bandeja quando você entrar no Windows."
                : "O Anotador foi removido da inicialização do Windows.", Forms.ToolTipIcon.Info);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show($"Não foi possível alterar a inicialização do Windows.\n{error.Message}",
                "Anotador", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Post(Action action) => app.Dispatcher.BeginInvoke(action);

    private void SetHotkey(int id, char first, char fallback)
    {
        foreach (char key in new[] { first, fallback })
        {
            if (!Native.RegisterHotKey(messages.Handle, id, Native.AltControl, key)) continue;
            hotkeys.Add(id, $"Ctrl+Alt+{key}");
            return;
        }
    }

    private string Label(string label, int id) => hotkeys.TryGetValue(id, out string? shortcut)
        ? $"{label} ({shortcut})" : $"{label} (atalho indisponível)";

    private IntPtr ReceiveMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312) return IntPtr.Zero;
        handled = true;
        if (wParam.ToInt32() == DrawHotkey) ToggleDrawing();
        if (wParam.ToInt32() == VisibilityHotkey) ToggleVisibility();
        if (wParam.ToInt32() == FinishHotkey) _ = Capture(false, finish: true);
        if (wParam.ToInt32() == RecordingHotkey)
        {
            if (recordingStop != null) recordingStop.Cancel();
            else StartRecording(SelectedRegionOverlay() != null);
        }
        return IntPtr.Zero;
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) => Post(() =>
    {
        if (shuttingDown) return;
        recordingStop?.Cancel();
        if (Busy) displayUpdatePending = true;
        else RefreshMonitors();
    });

    private void RefreshMonitors()
    {
        string? selected = overlays.FirstOrDefault(item => item.Surface == ActiveSurface)?.Screen.DeviceName;
        foreach (Overlay overlay in overlays) overlay.Close();
        overlays.Clear();
        foreach (Forms.Screen screen in Forms.Screen.AllScreens)
        {
            if (!boards.TryGetValue(screen.DeviceName, out Board? board))
                boards.Add(screen.DeviceName, board = new Board());
            var overlay = new Overlay(this, screen, board);
            overlay.Surface.Cursor = toolCursors[SelectedTool];
            overlay.PreviewKeyDown += KeyDown;
            overlays.Add(overlay);
        }
        if (restrictedBoard != null && !overlays.Any(item => item.Surface.Board == restrictedBoard))
            ResetArea();
        ActiveSurface = overlays.FirstOrDefault(item => item.Screen.DeviceName == selected)?.Surface
            ?? MonitorAt(Forms.Cursor.Position)?.Surface;
        ApplyVisibility();
        if (toolbarOpen) PlaceToolbar(Forms.Cursor.Position);
    }

    private Overlay? MonitorAt(Gdi.Point point)
    {
        string name = Forms.Screen.FromPoint(point).DeviceName;
        return overlays.FirstOrDefault(item => item.Screen.DeviceName == name);
    }

    private void ApplyVisibility()
    {
        foreach (Overlay overlay in overlays)
        {
            overlay.SetMode(CanDrawOn(overlay.Surface));
            if (visible) overlay.Reveal();
            else overlay.Hide();
        }
        UpdateModeButton();
        if (toolbarOpen)
        {
            if (!toolbar.IsVisible) toolbar.Show();
            Native.Raise(toolbar);
        }
        else toolbar.Hide();
    }

    private void ToggleDrawing()
    {
        if (Busy || shuttingDown) return;
        if (drawing)
        {
            PauseDrawing();
            return;
        }

        drawing = true;
        visible = true;
        var cursor = Forms.Cursor.Position;
        ActiveSurface = overlays.FirstOrDefault(item => item.Surface.Board == restrictedBoard)?.Surface
            ?? MonitorAt(cursor)?.Surface;
        if (!toolbarOpen)
        {
            toolbarOpen = true;
            PlaceToolbar(cursor);
        }
        ApplyVisibility();
        FocusToolbar();
    }

    private void PauseDrawing()
    {
        drawing = false;
        if (SelectingRegion) SelectTool(Tool.Pen);
        ApplyVisibility();
    }

    private void HideToolbar()
    {
        if (Busy || shuttingDown) return;
        toolbarOpen = false;
        PauseDrawing();
    }

    private void ToggleVisibility()
    {
        if (Busy || shuttingDown) return;
        visible = !visible;
        PauseDrawing();
    }

    private async void Shutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        recordingStop?.Cancel();
        if (recording != null) await recording;
        app.Shutdown();
    }
    internal void FocusToolbar()
    {
        if (toolbar.IsVisible && !toolbar.IsActive) toolbar.Activate();
    }

    private void KeyDown(object sender, KeyEventArgs e)
    {
        if (Busy) return;
        if (e.Key == Key.Escape && toolbarOpen)
        {
            e.Handled = true;
            if (e.IsRepeat) return;
            if (drawing) PauseDrawing();
            else HideToolbar();
        }
        else if (drawing && e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Undo();
            e.Handled = true;
        }
    }

    private Window BuildToolbar()
    {
        var window = new Window
        {
            Title = "Anotador de tela",
            Width = 552,
            Height = 172,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = new SolidColorBrush(Color.FromRgb(30, 40, 53)),
            Content = controls
        };
        controls.Margin = new Thickness(6);
        foreach ((Tool tool, string label, string icon) in new[]
        {
            (Tool.Pen, "Caneta", "M2,13 L3,10 11,2 14,5 6,13 Z M9,4 L12,7"),
            (Tool.Arrow, "Seta", "M2,13 L13,2 M7,2 H13 V8"),
            (Tool.Rectangle, "Retângulo", "M2,3 H14 V13 H2 Z"),
            (Tool.Text, "Texto", "M2,5 V3 H14 V5 M8,3 V13 M5,13 H11"),
            (Tool.Eraser, "Borracha", "M2,9 L8,3 Q9,2 10,3 L14,7 Q15,8 14,9 L10,13 H6 Z M5,6 L11,12 M10,13 H14"),
            (Tool.Area, "Área", "M2,6 V2 H6 M10,2 H14 V6 M14,10 V14 H10 M6,14 H2 V10"),
            (Tool.Lens, "Lens", "M2,5 V2 H5 M11,2 H14 V5 M14,11 V14 H11 M5,14 H2 V11 M11,8 A3,3 0 1 1 5,8 A3,3 0 1 1 11,8 Z")
        })
        {
            toolButtons.Add(tool, AddButton(label, icon, () => SelectTool(tool)));
            toolCursors.Add(tool, ToolCursor.Create(icon));
        }
        toolButtons[Tool.Lens].ToolTip = "Arraste uma área. Ao soltar, o recorte é enviado ao Google Lens.";
        AddButton("Tela inteira", "M1,1 H15 V15 H1 Z M6,6 L3,3 M3,6 V3 H6 M10,10 L13,13 M10,13 H13 V10", ResetArea);
        AddButton("Cor", "M8,2 C4,2 2,5 2,8 C2,11 4,14 8,14 Q10,14 9,12 Q8,10 10,10 H12 Q14,10 14,8 C14,4 11,2 8,2 Z M5,7 H5.2 M8,5 H8.2 M11,6 H11.2", ChangeColor);
        AddButton("Desfazer", "M6,2 L2,6 6,10 M2,6 H10 Q14,6 14,10 Q14,13 10,13 H8", Undo);
        clearButton = AddButton("Limpar tela", "M2,4 H14 M6,4 V2 H10 V4 M4,4 L5,14 H11 L12,4 M7,7 V11 M9,7 V11", Clear);
        AddButton("Copiar", "M6,6 H14 V14 H6 Z M10,4 V2 H2 V10 H4", () => _ = Capture(false));
        AddButton("Salvar…", "M2,2 H11 L14,5 V14 H2 Z M5,2 V6 H10 V2 M5,14 V9 H11 V14", () => _ = Capture(true));
        modeButton = AddButton("Pausar", "M4,3 H6 V13 H4 Z M10,3 H12 V13 H10 Z", ToggleDrawing);
        AddButton("Concluir e salvar", "M14,8 A6,6 0 1 1 2,8 A6,6 0 1 1 14,8 Z M5,8 L7,10 11,6", () => _ = Capture(false, finish: true));
        AddButton("Ocultar barra", "M3,8 H13 M3,3 H13 V13 H3 Z", HideToolbar);
        recordingButton = AddButton("Gravar…", "M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 Z", ShowRecordingMenu);
        UpdateToolButtons();
        UpdateModeButton();
        window.Closing += (_, e) =>
        {
            if (shuttingDown) return;
            e.Cancel = true;
            Post(HideToolbar);
        };
        window.PreviewKeyDown += KeyDown;
        window.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) window.DragMove();
        };
        return window;
    }

    private Overlay? SelectedRegionOverlay() => overlays.FirstOrDefault(item =>
        item.Surface.Board == restrictedBoard && item.Surface.CaptureRegion.HasValue);

    private void ShowRecordingMenu()
    {
        if (recordingStop != null)
        {
            recordingStop.Cancel();
            return;
        }

        var menu = new ContextMenu { PlacementTarget = recordingButton };
        var screen = new MenuItem { Header = "Gravar monitor inteiro" };
        screen.Click += (_, _) => StartRecording(false);
        var region = new MenuItem
        {
            Header = "Gravar área selecionada",
            IsEnabled = SelectedRegionOverlay() != null,
            ToolTip = "Selecione um recorte com a ferramenta Área."
        };
        ToolTipService.SetShowOnDisabled(region, true);
        region.Click += (_, _) => StartRecording(true);
        menu.Items.Add(screen);
        menu.Items.Add(region);
        menu.IsOpen = true;
    }

    private void StartRecording(bool regionOnly)
    {
        if (Busy || shuttingDown || recording != null) return;

        var target = regionOnly ? SelectedRegionOverlay()
            : overlays.FirstOrDefault(item => item.Surface == ActiveSurface) ?? MonitorAt(Forms.Cursor.Position);
        if (target == null) return;

        var bounds = target.Screen.Bounds;
        if (regionOnly)
        {
            if (target.Surface.CaptureRegion is not Rect region) return;
            bounds = Screenshot.RegionBounds(target, region);
        }
        recording = Record(bounds, target.Screen);
    }

    private async Task Record(Gdi.Rectangle bounds, Forms.Screen screen)
    {
        using var stop = new CancellationTokenSource();
        recordingStop = stop;
        UpdateRecordingButton(true);
        IntPtr handle = new WindowInteropHelper(toolbar).EnsureHandle();
        bool toolbarExcluded = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            && Native.SetWindowDisplayAffinity(handle, ExcludeFromCapture);
        try
        {
            await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Anotador");
            var result = await Task.Run(() => ScreenRecording.Record(bounds, folder, stop.Token));
            if (!shuttingDown)
                ShowSavedNotice(result.Preview, result.Path, screen, "Gravação salva", "Abrir vídeo");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show($"Não foi possível salvar a gravação.\n{error.Message}", "Anotador",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (toolbarExcluded) Native.SetWindowDisplayAffinity(handle, 0);
            recordingStop = null;
            recording = null;
            UpdateRecordingButton(false);
        }
    }

    private void UpdateRecordingButton(bool active)
    {
        var label = (StackPanel)recordingButton.Content;
        string caption = active ? "■ Parar gravação" : "Gravar…";
        ((TextBlock)label.Children[1]).Text = caption;
        recordingButton.Background = active ? Brushes.LightCoral : Brushes.WhiteSmoke;
        recordingButton.ToolTip = Label(active ? "Parar gravação" : "Iniciar gravação", RecordingHotkey);
        System.Windows.Automation.AutomationProperties.SetName(recordingButton, caption);
        tray.Text = active ? "Anotador de tela • Gravando" : "Anotador de tela";
    }

    private void UpdateModeButton()
    {
        if (modeButton?.Content is not StackPanel label) return;
        string caption = drawing ? "Pausar" : "Retomar desenho";
        ((TextBlock)label.Children[1]).Text = caption;
        var icon = (System.Windows.Shapes.Path)((Canvas)label.Children[0]).Children[0];
        icon.Data = Geometry.Parse(drawing ? "M4,3 H6 V13 H4 Z M10,3 H12 V13 H10 Z" : "M5,2 L13,8 5,14 Z");
        modeButton.ToolTip = drawing ? "Usar o PC com a barra visível (Esc)" : "Voltar a desenhar na tela. Esc oculta a barra.";
        System.Windows.Automation.AutomationProperties.SetName(modeButton, caption);
    }

    private Button AddButton(string text, string icon, Action action)
    {
        var symbol = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(icon),
            Stroke = new SolidColorBrush(Color.FromRgb(32, 64, 86)),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        var iconBox = new Canvas { Width = 16, Height = 16, Margin = new Thickness(0, 0, 5, 0) };
        iconBox.Children.Add(symbol);
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(iconBox);
        label.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = label,
            ToolTip = text,
            Margin = new Thickness(3),
            Padding = new Thickness(6, 3, 6, 3),
            MinHeight = 24
        };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => { if (!Busy) action(); };
        controls.Children.Add(button);
        return button;
    }

    private void SelectTool(Tool tool)
    {
        SelectedTool = tool;
        UpdateToolButtons();
        foreach (Overlay overlay in overlays) RefreshSurface(overlay);
    }

    private bool CanDrawOn(DrawingSurface surface) => drawing
        && (SelectingRegion || restrictedBoard == null || surface.Board == restrictedBoard);

    private void RefreshSurface(Overlay overlay)
    {
        overlay.Surface.Cursor = toolCursors[SelectedTool];
        overlay.SetMode(CanDrawOn(overlay.Surface));
    }

    internal void SelectArea(DrawingSurface surface, Rect region)
    {
        if (restrictedBoard != null) restrictedBoard.Region = null;
        restrictedBoard = surface.Board;
        restrictedBoard.Region = region;
        ActiveSurface = surface;
        SelectTool(Tool.Pen);
    }

    private void ResetArea()
    {
        if (restrictedBoard != null) restrictedBoard.Region = null;
        restrictedBoard = null;
        SelectTool(SelectingRegion ? Tool.Pen : SelectedTool);
    }

    private void UpdateToolButtons()
    {
        if (clearButton?.Content is StackPanel label)
        {
            string text = restrictedBoard == null ? "Limpar tela" : "Limpar área";
            ((TextBlock)label.Children[1]).Text = text;
            clearButton.ToolTip = text;
            System.Windows.Automation.AutomationProperties.SetName(clearButton, text);
        }
        foreach ((Tool tool, Button button) in toolButtons)
        {
            button.Background = tool == SelectedTool ? Brushes.LightSkyBlue : Brushes.WhiteSmoke;
            System.Windows.Automation.AutomationProperties.SetItemStatus(button,
                tool == SelectedTool ? "Selecionado" : "");
        }
    }

    private sealed record DialogOwner(IntPtr Handle) : Forms.IWin32Window;

    private void ChangeColor()
    {
        using var dialog = new Forms.ColorDialog
        {
            Color = Gdi.Color.FromArgb(Ink.R, Ink.G, Ink.B),
            FullOpen = true
        };
        Busy = true;
        try
        {
            if (dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(toolbar).Handle))
                == Forms.DialogResult.OK)
                Ink = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        }
        finally { FinishOperation(); }
    }

    private void PlaceToolbar(Gdi.Point cursor)
    {
        var screen = Forms.Screen.FromPoint(cursor);
        Gdi.Rectangle area = screen.WorkingArea;
        double factor = Native.MonitorScale(cursor);
        int width = (int)Math.Ceiling(toolbar.Width * factor);
        int height = (int)Math.Ceiling(toolbar.Height * factor);
        int x = Math.Clamp(cursor.X + 16, area.Left, Math.Max(area.Left, area.Right - width));
        int y = Math.Clamp(cursor.Y + 20, area.Top, Math.Max(area.Top, area.Bottom - height));
        var bounds = new Gdi.Rectangle(x, y, width, height);
        Native.Position(toolbar, bounds);
        if (!toolbar.IsVisible) toolbar.Show();
        Native.Position(toolbar, bounds);
    }

    private void Undo()
    {
        ActiveSurface?.Cancel();
        ActiveSurface?.Board.Undo();
        ActiveSurface?.InvalidateVisual();
    }

    private void Clear()
    {
        if (ActiveSurface == null || ActiveSurface.Board.Marks.Count == 0) return;
        if (restrictedBoard != null)
        {
            ActiveSurface.ClearRegion();
            return;
        }
        ActiveSurface.Cancel();
        ActiveSurface.Board.Save();
        ActiveSurface.Board.Marks.Clear();
        ActiveSurface.InvalidateVisual();
    }

    internal string? AskText()
    {
        var layout = new StackPanel { Margin = new Thickness(14) };
        var input = new TextBox { MaxLength = 250, MinWidth = 300, Margin = new Thickness(0, 0, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var add = new Button { Content = "Inserir", IsDefault = true, MinWidth = 70, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 70 };
        layout.Children.Add(input);
        layout.Children.Add(buttons);
        buttons.Children.Add(add);
        buttons.Children.Add(cancel);
        var dialog = new Window
        {
            Title = "Inserir texto",
            Content = layout,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = toolbar
        };
        add.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => input.Focus();
        Busy = true;
        try { return dialog.ShowDialog() == true ? input.Text.Trim() : null; }
        finally
        {
            Busy = false;
            // Let the input handler commit the mark before rebuilding the monitor windows.
            if (displayUpdatePending) Post(FinishOperation);
        }
    }

    private void FinishOperation()
    {
        Busy = false;
        if (!displayUpdatePending || shuttingDown) return;
        displayUpdatePending = false;
        RefreshMonitors();
    }

    private async Task HideCaptureWindows()
    {
        notice?.Close();
        toolbar.Hide();
        foreach (Overlay overlay in overlays)
        {
            overlay.Surface.Cancel();
            overlay.Hide();
        }
        await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!shuttingDown) Native.DwmFlush();
    }

    internal async Task SearchLens(DrawingSurface surface, Rect region)
    {
        if (Busy || shuttingDown) return;
        Overlay? target = overlays.FirstOrDefault(item => item.Surface == surface);
        if (target == null) return;
        Busy = true;
        try
        {
            BitmapSource image;
            try
            {
                await HideCaptureWindows();
                if (shuttingDown) return;
                image = Screenshot.ReadRegion(target, region);
            }
            finally
            {
                if (!shuttingDown)
                {
                    drawing = false;
                    SelectTool(Tool.Pen);
                    ApplyVisibility();
                }
            }

            await LensSearch.OpenAsync(image);
        }
        catch (Exception error) when (error is Win32Exception or IOException or ArgumentException
            or InvalidOperationException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            if (!shuttingDown)
                MessageBox.Show($"A pesquisa no Google Lens não foi concluída.\n{error.Message}",
                    "Google Lens", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { FinishOperation(); }
    }

    private async Task Capture(bool save, bool fromTray = false, bool finish = false)
    {
        if (Busy || shuttingDown) return;
        Overlay? target = overlays.FirstOrDefault(item => item.Surface.Board == restrictedBoard)
            ?? (fromTray ? null : overlays.FirstOrDefault(item => item.Surface == ActiveSurface))
            ?? MonitorAt(Forms.Cursor.Position);
        if (target == null) return;
        Busy = true;
        try
        {
            BitmapSource image;
            try
            {
                await HideCaptureWindows();
                if (shuttingDown) return;
                image = Screenshot.Render(target, visible);
            }
            finally
            {
                if (!shuttingDown)
                {
                    ApplyVisibility();
                    if (drawing) FocusToolbar();
                }
            }

            if (finish)
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Anotador");
                string path = Screenshot.SaveAutomatic(image, folder);
                drawing = false;
                visible = false;
                ApplyVisibility();
                ShowSavedNotice(image, path, target.Screen, "Captura salva", "Abrir imagem");
            }
            else if (save)
            {
                var picker = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Salvar captura",
                    Filter = "Imagem PNG (*.png)|*.png",
                    DefaultExt = ".png",
                    FileName = $"Anotacao-{DateTime.Now:yyyyMMdd-HHmmss}.png"
                };
                if ((toolbar.IsVisible ? picker.ShowDialog(toolbar) : picker.ShowDialog()) != true) return;
                using var output = File.Create(picker.FileName);
                Screenshot.WritePng(image, output);
                tray.ShowBalloonTip(2000, "Anotador", "Captura salva.", Forms.ToolTipIcon.Info);
            }
            else
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { Clipboard.SetImage(image); break; }
                    catch (System.Runtime.InteropServices.COMException) when (attempt < 3)
                    {
                        await Task.Delay(50);
                    }
                }
                tray.ShowBalloonTip(2000, "Anotador", "Captura copiada.", Forms.ToolTipIcon.Info);
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            if (!shuttingDown)
                MessageBox.Show($"A captura não foi concluída.\n{error.Message}", "Anotador",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { FinishOperation(); }
    }

    private void ShowSavedNotice(BitmapSource preview, string path, Forms.Screen screen,
        string title, string openLabel)
    {
        notice?.Close();
        var popup = new CaptureNotice(preview, path, screen, title, openLabel);
        popup.Closed += (_, _) => notice = null;
        notice = popup;
        popup.ShowAtCorner();
    }

    private void Dispose()
    {
        shuttingDown = true;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        foreach (int id in hotkeys.Keys) Native.UnregisterHotKey(messages.Handle, id);
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        trayIcon.Dispose();
        messages.Dispose();
        foreach (Cursor cursor in toolCursors.Values) cursor.Dispose();
    }
}



