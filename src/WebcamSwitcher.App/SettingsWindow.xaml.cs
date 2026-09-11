using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WebcamSwitcher.Core;
using MessageBox = System.Windows.MessageBox;
using Panel = System.Windows.Controls.Panel;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using StackPanel = System.Windows.Controls.StackPanel;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Thickness = System.Windows.Thickness;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace WebcamSwitcher.App;

public partial class SettingsWindow : Window
{
    private readonly PipelineController _pipeline;
    private readonly List<CameraRow> _rows = new();
    private List<CameraConfig> _devices = new();

    public SettingsWindow(PipelineController pipeline)
    {
        InitializeComponent();
        _pipeline = pipeline;
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _devices = await CameraEngine.EnumerateCamerasAsync();

        var config = _pipeline.Config;

        // Ensure configured (possibly unplugged) devices still show in the dropdowns.
        foreach (var cam in config.Cameras)
            if (!_devices.Any(d => d.DeviceId == cam.DeviceId))
                _devices.Add(cam);

        WidthCombo.ItemsSource = new[] { "3840", "1920", "1280", "960", "640" };
        WidthCombo.Text = config.Width.ToString();
        HeightCombo.ItemsSource = new[] { "2160", "1080", "720", "540", "480" };
        HeightCombo.Text = config.Height.ToString();
        FpsCombo.ItemsSource = new[] { "60", "30", "24", "15" };
        FpsCombo.Text = config.Fps.ToString();

        RotateHotkeyBox.Text = config.RotateHotkey ?? "";
        StartWithWindowsBox.IsChecked = config.StartWithWindows;
        StartMinimizedBox.IsChecked = config.StartMinimized;

        for (int i = 0; i < config.Cameras.Count; i++)
        {
            var hotkey = config.Hotkeys is { Length: > 0 } && i < config.Hotkeys.Length ? config.Hotkeys[i] : "";
            AddRow(config.Cameras[i], hotkey);
        }

        NoteText.Text = "Camera and format changes apply immediately. A consumer app may need to re-open the camera to pick up a new resolution.";
    }

    private void AddCamera_Click(object sender, RoutedEventArgs e) => AddRow(null, "");

    private void AddRow(CameraConfig? cam, string hotkey)
    {
        var row = new CameraRow(_devices, cam, hotkey, RemoveRow, _rows.Count);
        _rows.Add(row);
        CamerasPanel.Children.Add(row.Panel);
    }

    private void RemoveRow(CameraRow row)
    {
        _rows.Remove(row);
        CamerasPanel.Children.Remove(row.Panel);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthCombo.Text, out int w) || !int.TryParse(HeightCombo.Text, out int h) || !int.TryParse(FpsCombo.Text, out int fps))
        {
            MessageBox.Show(this, "Width, Height, and FPS must be numbers.", "Invalid output", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var next = new AppConfig { Width = w, Height = h, Fps = fps };

        var cameras = new List<CameraConfig>();
        var hotkeys = new List<string>();
        foreach (var row in _rows)
        {
            var sel = row.SelectedCamera;
            if (sel == null || string.IsNullOrEmpty(sel.DeviceId))
            {
                MessageBox.Show(this, "Each camera must have a device selected.", "Invalid camera", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (Hotkey.Parse(row.HotkeyBox.Text) == null)
            {
                MessageBox.Show(this, $"Invalid hotkey '{row.HotkeyBox.Text}'. Use e.g. 'Ctrl+Alt+1'.", "Invalid hotkey", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var (cw, ch) = row.SelectedCapture;
            cameras.Add(new CameraConfig
            {
                DeviceId = sel.DeviceId,
                FriendlyName = sel.FriendlyName,
                CaptureWidth = cw,
                CaptureHeight = ch
            });
            hotkeys.Add(row.HotkeyBox.Text);
        }

        if (cameras.Count == 0)
        {
            MessageBox.Show(this, "Add at least one camera.", "Invalid", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Hotkey.Parse(RotateHotkeyBox.Text) == null)
        {
            MessageBox.Show(this, "Invalid rotate hotkey.", "Invalid hotkey", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        next.Cameras = cameras;
        next.Hotkeys = hotkeys.ToArray();
        next.RotateHotkey = RotateHotkeyBox.Text;
        next.ActiveIndex = Math.Clamp(_pipeline.ActiveIndex, 0, cameras.Count - 1);
        next.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        next.StartMinimized = StartMinimizedBox.IsChecked == true;

        try
        {
            await _pipeline.ApplyAsync(next);
            ConfigService.Save(next);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to apply settings: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class CameraRow
    {
        public Panel Panel { get; }
        public ComboBox Combo { get; }
        public ComboBox CaptureCombo { get; }
        public TextBox HotkeyBox { get; }

        private static readonly string[] CaptureSizes =
        {
            "Auto", "1920x1080", "1280x720", "1024x576", "960x720", "800x600", "640x480", "640x360"
        };

        public CameraConfig? SelectedCamera => Combo.SelectedItem as CameraConfig;

        /// <summary>Chosen capture size, or (null, null) for "Auto".</summary>
        public (int? W, int? H) SelectedCapture
        {
            get
            {
                var s = CaptureCombo.Text?.Trim();
                if (!string.IsNullOrEmpty(s) && !s.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                {
                    var p = s.Split('x');
                    if (p.Length == 2 && int.TryParse(p[0], out int cw) && int.TryParse(p[1], out int ch))
                        return (cw, ch);
                }
                return (null, null);
            }
        }

        public CameraRow(List<CameraConfig> devices, CameraConfig? selected, string hotkey, Action<CameraRow> remove, int index)
        {
            Combo = new ComboBox
            {
                Width = 220,
                ItemsSource = devices
            };
            // Explicit ItemTemplate so both the dropdown items AND the closed
            // selection box render FriendlyName (DisplayMemberPath is not used by
            // SelectionBoxItemTemplate in a custom ComboBox template).
            if (System.Windows.Application.Current.FindResource("CameraItemTemplate") is System.Windows.DataTemplate template)
                Combo.ItemTemplate = template;
            System.Windows.Automation.AutomationProperties.SetAutomationId(Combo, "CameraCombo" + index);
            if (selected != null)
                Combo.SelectedItem = devices.FirstOrDefault(d => d.DeviceId == selected.DeviceId) ?? selected;

            HotkeyBox = new TextBox
            {
                Width = 110,
                Text = hotkey
            };

            CaptureCombo = new ComboBox
            {
                Width = 104,
                IsEditable = true,
                ItemsSource = CaptureSizes,
                Text = selected?.CaptureWidth is int sw && selected.CaptureHeight is int sh ? $"{sw}x{sh}" : "Auto",
                ToolTip = "Capture resolution. Auto matches the output aspect (avoids black bars)."
            };

            var removeBtn = new Button
            {
                Content = "Remove",
                Padding = new Thickness(8, 3, 8, 3),
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Foreground = Brushes.White
            };
            removeBtn.Click += (_, _) => remove(this);

            var label = new TextBlock
            {
                Text = "Hotkey",
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 6, 0)
            };

            var capLabel = new TextBlock
            {
                Text = "Capture",
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 6, 0)
            };

            Panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            Panel.Children.Add(Combo);
            Panel.Children.Add(capLabel);
            Panel.Children.Add(CaptureCombo);
            Panel.Children.Add(label);
            Panel.Children.Add(HotkeyBox);
            Panel.Children.Add(removeBtn);
        }
    }
}
