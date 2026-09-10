using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WebcamSwitcher.Core;
using Image = System.Windows.Controls.Image;
using Color = System.Windows.Media.Color;

namespace WebcamSwitcher.App;

public partial class MainWindow : Window
{
    private readonly CameraEngine _engine;
    private readonly AppConfig _config;
    private readonly bool _vcamOk;
    private readonly WriteableBitmap[] _previews = new WriteableBitmap[2];
    private readonly DateTime[] _lastRender = new DateTime[2];
    private readonly Image[] _images;
    private readonly Border[] _borders;
    private readonly TextBlock[] _labels;

    public MainWindow(CameraEngine engine, AppConfig config, bool vcamOk)
    {
        InitializeComponent();
        _engine = engine;
        _config = config;
        _vcamOk = vcamOk;

        _images = new[] { PreviewA, PreviewB };
        _borders = new[] { BorderA, BorderB };
        _labels = new[] { LabelA, LabelB };

        for (int i = 0; i < 2; i++)
        {
            _previews[i] = new WriteableBitmap(
                CameraSource.PreviewWidth, CameraSource.PreviewHeight, 96, 96, PixelFormats.Bgra32, null);
            _images[i].Source = _previews[i];
            _lastRender[i] = DateTime.MinValue;
            _borders[i].Tag = i;
            _borders[i].MouseLeftButtonDown += Preview_Click;
        }

        var sources = _engine.Sources;
        for (int i = 0; i < sources.Count && i < 2; i++)
        {
            int idx = i;
            sources[i].PreviewReady += (bgra, w, h) => OnPreview(idx, bgra, w, h);
            _labels[i].Text = sources[i].DisplayName;
        }

        StatusText.Text = vcamOk
            ? $"{sources.Count} camera(s) · virtual camera active"
            : $"{sources.Count} camera(s) · virtual camera NOT registered";

        UpdateActive();
    }

    private void Preview_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is int idx && idx < _engine.Sources.Count)
        {
            _engine.ActiveIndex = idx;
            UpdateActive();
        }
    }

    private void OnPreview(int idx, byte[] bgra, int w, int h)
    {
        if ((DateTime.Now - _lastRender[idx]).TotalMilliseconds < 66)
            return; // ~15fps throttle
        _lastRender[idx] = DateTime.Now;

        var copy = new byte[bgra.Length];
        Buffer.BlockCopy(bgra, 0, copy, 0, bgra.Length);
        Dispatcher.BeginInvoke(() => Render(idx, copy));
    }

    private void Render(int idx, byte[] bgra)
    {
        if (idx < 0 || idx >= _previews.Length)
            return;
        var bmp = _previews[idx];
        bmp.WritePixels(new Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight), bgra, bmp.PixelWidth * 4, 0);
    }

    public void UpdateActive()
    {
        int active = _engine.ActiveIndex;
        for (int i = 0; i < _borders.Length; i++)
            _borders[i].BorderBrush = i == active
                ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
                : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_config) { Owner = this }.ShowDialog();
    }
}
