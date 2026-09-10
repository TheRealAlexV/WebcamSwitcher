using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WebcamSwitcher.Core;

namespace WebcamSwitcher.App;

public sealed class PreviewVm : INotifyPropertyChanged
{
    public int Index { get; }
    public string Label { get; set; } = "";
    public WriteableBitmap Bitmap { get; }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PreviewVm(int index, string label)
    {
        Index = index;
        Label = label;
        Bitmap = new WriteableBitmap(CameraSource.PreviewWidth, CameraSource.PreviewHeight, 96, 96, PixelFormats.Bgra32, null);
    }
}
