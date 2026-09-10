using System.Windows;
using WebcamSwitcher.Core;
using MessageBox = System.Windows.MessageBox;

namespace WebcamSwitcher.App;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        CamAText.Text = _config.Cameras.Count > 0 ? _config.Cameras[0].FriendlyName : "—";
        CamBText.Text = _config.Cameras.Count > 1 ? _config.Cameras[1].FriendlyName : "—";
        HotkeyA.Text = _config.Hotkeys is { Length: > 0 } ? _config.Hotkeys[0] : "";
        HotkeyB.Text = _config.Hotkeys is { Length: > 1 } ? _config.Hotkeys[1] : "";
        HotkeyRotate.Text = _config.RotateHotkey ?? "";

        NoteText.Text = $"Output: {_config.Width}x{_config.Height} @ {_config.Fps}fps · Changes apply after restart.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Hotkey.Parse(HotkeyA.Text) == null ||
            Hotkey.Parse(HotkeyB.Text) == null ||
            Hotkey.Parse(HotkeyRotate.Text) == null)
        {
            MessageBox.Show(this, "One or more hotkeys are invalid. Use formats like 'Ctrl+Alt+1' or 'Ctrl+Alt+N'.", "Invalid hotkey", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.Hotkeys = new[] { HotkeyA.Text, HotkeyB.Text };
        _config.RotateHotkey = HotkeyRotate.Text;
        ConfigService.Save(_config);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
