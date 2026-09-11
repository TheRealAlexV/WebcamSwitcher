using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WebcamSwitcher.Core;

namespace WebcamSwitcher.App;

public partial class MainWindow : Window
{
    private readonly PipelineController _pipeline;
    private readonly ObservableCollection<PreviewVm> _previews = new();
    private readonly object _renderLock = new();
    private readonly Dictionary<int, long> _lastRenderTicks = new();

    public MainWindow(PipelineController pipeline)
    {
        InitializeComponent();
        _pipeline = pipeline;
        Previews.ItemsSource = _previews;
        _pipeline.SourcesChanged += OnSourcesChanged;
        _pipeline.VirtualCameraStateChanged += OnStateChanged;
        _pipeline.CaptureStateChanged += OnStateChanged;

        RebuildPreviews();
        UpdateToggles();
    }

    private void OnStateChanged() => Dispatcher.BeginInvoke(UpdateToggles);

    private void UpdateToggles()
    {
        VcamToggle.Content = _pipeline.VirtualCameraActive ? "Virtual Camera: ON" : "Virtual Camera: OFF";
        CaptureToggle.Content = _pipeline.CaptureActive ? "Cameras: ON" : "Cameras: OFF";
        UpdateStatus();
    }

    private void VcamToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_pipeline.VirtualCameraActive)
            _pipeline.StopVirtualCamera();
        else
            _pipeline.StartVirtualCamera();
        UpdateToggles();
    }

    private async void CaptureToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_pipeline.CaptureActive)
            await _pipeline.StopCaptureAsync();
        else
            await _pipeline.StartCaptureAsync();
        UpdateToggles();
    }

    private void OnSourcesChanged() => Dispatcher.BeginInvoke(RebuildPreviews);

    private void RebuildPreviews()
    {
        _previews.Clear();
        lock (_renderLock)
            _lastRenderTicks.Clear();

        var sources = _pipeline.Sources;
        for (int i = 0; i < sources.Count; i++)
        {
            int idx = i;
            var vm = new PreviewVm(idx, sources[i].DisplayName);
            sources[i].PreviewReady += (bgra, w, h) => OnPreview(idx, vm, bgra);
            _previews.Add(vm);
        }

        UpdateActive();
        UpdateStatus();
    }

    private void OnPreview(int idx, PreviewVm vm, byte[] bgra)
    {
        long now = DateTime.UtcNow.Ticks;
        bool recent;
        lock (_renderLock)
        {
            recent = _lastRenderTicks.TryGetValue(idx, out var t) && (now - t) < TimeSpan.TicksPerMillisecond * 66;
            if (!recent)
                _lastRenderTicks[idx] = now;
        }
        if (recent)
            return; // ~15fps throttle

        var copy = new byte[bgra.Length];
        Buffer.BlockCopy(bgra, 0, copy, 0, bgra.Length);
        Dispatcher.BeginInvoke(() => Render(vm, copy));
    }

    private void Render(PreviewVm vm, byte[] bgra)
    {
        var bmp = vm.Bitmap;
        bmp.WritePixels(new Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight), bgra, bmp.PixelWidth * 4, 0);
    }

    private void Preview_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is int idx)
        {
            _pipeline.ActiveIndex = idx;
            UpdateActive();
        }
    }

    private void UpdateActive()
    {
        int active = _pipeline.ActiveIndex;
        for (int i = 0; i < _previews.Count; i++)
            _previews[i].IsActive = i == active;
    }

    private void UpdateStatus()
    {
        var cfg = _pipeline.Config;
        var cam = _pipeline.CaptureActive
            ? $"{_pipeline.Sources.Count} camera(s) · {cfg.Width}x{cfg.Height} @ {cfg.Fps}fps"
            : "capture stopped";
        var vcam = _pipeline.VirtualCameraActive ? "vCam ON" : "vCam OFF";
        StatusText.Text = $"{cam} · {vcam}";
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_pipeline) { Owner = this }.ShowDialog();
        UpdateActive();
        UpdateStatus();
    }
}
