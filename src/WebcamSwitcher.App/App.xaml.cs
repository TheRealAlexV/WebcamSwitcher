using System.Windows;
using System.Windows.Forms;
using WebcamSwitcher.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace WebcamSwitcher.App;

public partial class App : Application
{
    private PipelineController? _pipeline;
    private NotifyIcon? _tray;
    private ToolStripMenuItem[]? _trayCameraItems;
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

        _pipeline = new PipelineController();
        try { await _pipeline.StartAsync(_config); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Pipeline start failed: {ex}"); }
        _pipeline.SourcesChanged += OnSourcesChanged;

        CreateTray();

        if (!_config.StartMinimized)
            ShowMain();
    }

    private void OnSourcesChanged()
    {
        Dispatcher.BeginInvoke(RebuildTrayMenu);
    }

    private void ShowMain()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow(_pipeline!);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void CreateTray()
    {
        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "WebcamSwitcher",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowMain();

        var menu = new ContextMenuStrip();
        _tray.ContextMenuStrip = menu;

        RebuildTrayMenu();

        menu.Opening += (_, _) => UpdateTrayChecks();
    }

    private void RebuildTrayMenu()
    {
        if (_tray?.ContextMenuStrip == null)
            return;

        var menu = _tray.ContextMenuStrip;
        menu.Items.Clear();

        var sources = _pipeline?.Sources ?? Array.Empty<CameraSource>();
        _trayCameraItems = new ToolStripMenuItem[sources.Count];
        for (int i = 0; i < sources.Count; i++)
        {
            int idx = i;
            var item = new ToolStripMenuItem(sources[i].DisplayName, null, (_, _) => { if (_pipeline != null) _pipeline.ActiveIndex = idx; });
            _trayCameraItems[i] = item;
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
    }

    private void UpdateTrayChecks()
    {
        if (_trayCameraItems == null || _pipeline == null)
            return;
        for (int i = 0; i < _trayCameraItems.Length; i++)
            _trayCameraItems[i].Checked = _pipeline.ActiveIndex == i;
    }

    private void OpenSettings()
    {
        ShowMain();
        if (_pipeline != null)
            new SettingsWindow(_pipeline) { Owner = _mainWindow }.ShowDialog();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_pipeline != null)
        {
            try { await _pipeline.DisposeAsync(); } catch { }
        }
        ConfigService.Save(_config);
        base.OnExit(e);
    }
}
