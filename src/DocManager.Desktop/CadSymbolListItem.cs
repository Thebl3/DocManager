using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using DocManager.Core;

namespace DocManager.Desktop;

public sealed class CadSymbolListItem : INotifyPropertyChanged
{
    private bool _hasImage;
    private int _pixelWidth;
    private int _pixelHeight;
    private string _source = string.Empty;
    private BitmapSource? _preview;

    public required string Key { get; init; }
    public required string DisplayModel { get; init; }
    public int UsageCount { get; set; }
    public bool HasImage { get => _hasImage; private set { if (_hasImage == value) return; _hasImage = value; Notify(); Notify(nameof(Status)); } }
    public string Status => HasImage ? "Đã có ảnh" : "Chưa có ảnh";
    public int PixelWidth { get => _pixelWidth; private set { if (_pixelWidth == value) return; _pixelWidth = value; Notify(); Notify(nameof(Dimensions)); } }
    public int PixelHeight { get => _pixelHeight; private set { if (_pixelHeight == value) return; _pixelHeight = value; Notify(); Notify(nameof(Dimensions)); } }
    public string Dimensions => PixelWidth > 0 && PixelHeight > 0 ? $"{PixelWidth} × {PixelHeight} px" : "Chưa có kích thước";
    public string Source { get => _source; private set { if (_source == value) return; _source = value; Notify(); } }
    public BitmapSource? Preview { get => _preview; private set { if (ReferenceEquals(_preview, value)) return; _preview = value; Notify(); } }

    public void SetMapping(CadSymbolMapping? mapping, BitmapSource? preview)
    {
        HasImage = mapping is not null;
        PixelWidth = mapping?.PixelWidth ?? 0;
        PixelHeight = mapping?.PixelHeight ?? 0;
        Source = mapping?.Source ?? string.Empty;
        Preview = preview;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
