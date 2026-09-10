using System.Windows;
using System.Windows.Forms;
using WebcamSwitcher.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace WebcamSwitcher.App;

public partial class App : Application
{
    private VirtualCameraService? _vcam;
    private FramePublisher? _publisher;
    private CameraEngine? _engine;
    private HotkeyService? _hotkeys;
    private NotifyIcon? _tray;
    private MainWindow? _mainWindow;
    private AppConfig _config = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _config = ConfigService.Load();

        // Auto-populate cameras if none configured.
        if (_config.Cameras.Count == 0)
        {
            try
            {
                var cams = await CameraEngine.EnumerateCamerasAsync();
                var physical = cams.Where(c => !c.FriendlyName.Contains("WebcamSwitcher", StringComparison.OrdinalIgnoreCase)
                                               && !c.FriendlyName.Contains("Windows Virtual Camera", StringComparison.OrdinalIgnoreCase)).ToList();
                _config.Cameras = physical.Take(2).ToList();
                if (_config.Hotkeys == null || _config.Hotkeys.Length == 0)
                    _config.Hotkeys = new[] { "Ctrl+Alt+1", "Ctrl+Alt+2" };
                _config.RotateHotkey ??= "Ctrl+Alt+N";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Enumeration failed: {ex}");
            }
        }

        // Compose services.
        _vcam = new VirtualCameraService();
        bool vcamOk = _vcam.Start();

        _publisher = new FramePublisher(_config.Width, _config.Height, _config.Fps);
        _engine = new CameraEngine(_publisher);

        int started = 0;
        try { started = await _engine.StartAsync(_config); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Engine start failed: {ex}"); }

        _hotkeys = new HotkeyService(_config, _engine);
        _hotkeys.Register();

        CreateTray();

        _mainWindow = new MainWindow(_engine, _config, vcamOk);
        _mainWindow.Closed += (_, _) => _mainWindow = null;
        _mainWindow.Show();
    }

    private void CreateTray()
    {
        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "WebcamSwitcher",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        _tray.ContextMenuStrip = menu;

        // Keep a reference to update the checked state.
        var items = new ToolStripMenuItem[_engine!.Sources.Count];
        for (int i = 0; i < items.Length; i++)
        {
            int idx = i;
            var name = _engine.Sources[i].DisplayName;
            var item = new ToolStripMenuItem(name, null, (_, _) => { _engine.ActiveIndex = idx; });
            items[i] = item;
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _tray.DoubleClick += (_, _) => OpenSettings();
        menu.Opening += (_, _) =>
        {
            for (int i = 0; i < items.Length && i < _engine.Sources.Count; i++)
                items[i].Checked = _engine.ActiveIndex == i;
        };
    }

    private void OpenSettings()
    {
        _mainWindow?.Show();
        _mainWindow?.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        try { _engine?.DisposeAsync().AsTask().Wait(3000); } catch { }
        _vcam?.Dispose();
        ConfigService.Save(_config);
        base.OnExit(e);
    }
}
