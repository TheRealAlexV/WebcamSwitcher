using System.Windows.Interop;
using WebcamSwitcher.Core;

namespace WebcamSwitcher.App;

public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private HwndSource? _source;
    private readonly AppConfig _config;
    private readonly CameraEngine _engine;
    private readonly Dictionary<int, int> _idToCamera = new();
    private int _rotateId = -1;
    private int _nextId = 1;

    public event Action? Rotated;

    public HotkeyService(AppConfig config, CameraEngine engine)
    {
        _config = config;
        _engine = engine;
    }

    public void Register()
    {
        var p = new HwndSourceParameters("WebcamSwitcherHotkeys")
        {
            Width = 0, Height = 0, PositionX = 0, PositionY = 0,
            WindowStyle = 0, ExtendedWindowStyle = 0
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);

        var hotkeys = _config.Hotkeys ?? Array.Empty<string>();
        for (int i = 0; i < hotkeys.Length && i < _config.Cameras.Count; i++)
        {
            var hk = Hotkey.Parse(hotkeys[i]);
            if (hk != null && NativeInterop.RegisterHotKey(_source.Handle, _nextId, hk.Modifiers, hk.Vk))
                _idToCamera[_nextId++] = i;
        }

        var rotate = Hotkey.Parse(_config.RotateHotkey);
        if (rotate != null && NativeInterop.RegisterHotKey(_source.Handle, _nextId, rotate.Modifiers, rotate.Vk))
            _rotateId = _nextId++;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_idToCamera.TryGetValue(id, out int cam))
            {
                _engine.ActiveIndex = cam;
                handled = true;
            }
            else if (id == _rotateId)
            {
                Rotate();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void Rotate()
    {
        int count = _engine.Sources.Count;
        if (count == 0)
            return;
        _engine.ActiveIndex = (_engine.ActiveIndex + 1) % count;
        Rotated?.Invoke();
    }

    public void Dispose()
    {
        if (_source != null)
        {
            foreach (var id in _idToCamera.Keys)
                NativeInterop.UnregisterHotKey(_source.Handle, id);
            if (_rotateId >= 0)
                NativeInterop.UnregisterHotKey(_source.Handle, _rotateId);
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
