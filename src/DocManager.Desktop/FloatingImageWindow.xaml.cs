using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using FormsScreen = System.Windows.Forms.Screen;
using WpfPoint = System.Windows.Point;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace DocManager.Desktop;

public partial class FloatingImageWindow : Window
{
    private double _nativeWidth;
    private double _nativeHeight;
    private double _displayWidth;
    private double _displayHeight;
    private double _currentScale = 1.0;
    private WpfPoint _dragStart;
    private bool _isDragging;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _isClickThrough;
    private int _currentOpacityPercent = 100;

    public event EventHandler<EventArgs>? OpacityChanged;
    public event EventHandler<EventArgs>? ClosedByUser;
    public event EventHandler<EventArgs>? BoundsChanged;

    public int CurrentOpacityPercent => _currentOpacityPercent;

    public FloatingImageWindow()
    {
        InitializeComponent();
        SourceInitialized += FloatingImageWindow_SourceInitialized;
        Closing += FloatingImageWindow_Closing;
        KeyDown += FloatingImageWindow_KeyDown;
        LocationChanged += (s, e) => BoundsChanged?.Invoke(this, EventArgs.Empty);
        SizeChanged += (s, e) => BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FloatingImageWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = PresentationSource.FromVisual(this);
        if (source is HwndSource hwndSource)
        {
            hwndSource.AddHook(DpiMessageHook);
        }
    }

    private IntPtr DpiMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmDpiChanged = 0x02E0;
        if (msg == WmDpiChanged)
        {
            SizeToNativeImage();
        }
        return IntPtr.Zero;
    }

    public void ApplySavedBounds(double? left, double? top, double? width, double? height)
    {
        if (left.HasValue && top.HasValue && width.HasValue && height.HasValue)
        {
            Left = left.Value;
            Top = top.Value;
            Width = width.Value;
            Height = height.Value;
        }
    }

    public (double, double, double, double)? GetCurrentBounds()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top) || double.IsNaN(Width) || double.IsNaN(Height))
            return null;
        return (Left, Top, Width, Height);
    }

    public void LoadImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            DisplayImage.Source = null;
            _nativeWidth = 0;
            _nativeHeight = 0;
            _displayWidth = 0;
            _displayHeight = 0;
            _currentScale = 1.0;
            return;
        }

        try
        {
            using (var stream = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 0)
                {
                    var frame = decoder.Frames[0];
                    _nativeWidth = frame.PixelWidth;
                    _nativeHeight = frame.PixelHeight;

                    var maxDisplaySize = GetMaxDisplaySize();
                    double displayScale = _nativeWidth > maxDisplaySize || _nativeHeight > maxDisplaySize
                        ? Math.Min(maxDisplaySize / _nativeWidth, maxDisplaySize / _nativeHeight)
                        : 1.0;

                    if (displayScale < 1.0)
                    {
                        int displayWidth = (int)(_nativeWidth * displayScale);
                        int displayHeight = (int)(_nativeHeight * displayScale);
                        _displayWidth = displayWidth;
                        _displayHeight = displayHeight;
                        var scaledFrame = new TransformedBitmap(frame, new ScaleTransform(displayScale, displayScale));
                        scaledFrame.Freeze();
                        DisplayImage.Source = scaledFrame;
                    }
                    else
                    {
                        frame.Freeze();
                        DisplayImage.Source = frame;
                        _displayWidth = _nativeWidth;
                        _displayHeight = _nativeHeight;
                    }

                    _currentScale = 1.0;
                    SizeToNativeImage();
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Không thể tải ảnh từ {path}: {ex.Message}", ex);
        }
    }

    private double GetMaxDisplaySize()
    {
        var screens = FormsScreen.AllScreens;
        double maxWorkArea = 600;
        if (screens.Length > 0)
        {
            maxWorkArea = screens.Select(s => Math.Max(s.WorkingArea.Width, s.WorkingArea.Height)).Max();
        }
        return maxWorkArea * 2;
    }

    public void SetOpacityPercent(double percent)
    {
        var clamped = (int)Math.Max(10, Math.Min(100, percent));
        _currentOpacityPercent = clamped;
        byte alpha = (byte)(clamped * 255 / 100);
        if (_hwnd != IntPtr.Zero)
        {
            FloatingImageInterop.SetOpacityAlpha(_hwnd, alpha);
        }
        OpacityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetClickThrough(bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (enabled)
        {
            FloatingImageInterop.EnableClickThrough(_hwnd);
            _isClickThrough = true;
        }
        else
        {
            FloatingImageInterop.DisableClickThrough(_hwnd);
            _isClickThrough = false;
        }
    }

    public void SetTopmost(bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;
        Topmost = enabled;
        if (enabled)
            FloatingImageInterop.ForceTopmost(_hwnd);
        else
            FloatingImageInterop.RemoveTopmost(_hwnd);
    }

    private void SizeToNativeImage()
    {
        if (_nativeWidth <= 0 || _nativeHeight <= 0) return;

        double dpiScaleX = 1.0;
        double dpiScaleY = 1.0;

        try
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                dpiScaleX = source.CompositionTarget.TransformFromDevice.M11;
                dpiScaleY = source.CompositionTarget.TransformFromDevice.M22;
            }
        }
        catch
        {
            dpiScaleX = 1.0;
            dpiScaleY = 1.0;
        }

        var screens = FormsScreen.AllScreens;
        double maxWidthPhysical = 800;
        double maxHeightPhysical = 600;

        if (screens.Length > 0)
        {
            var screen = screens.FirstOrDefault(s => s.Bounds.Contains((int)Left, (int)Top)) ?? screens[0];
            var workArea = screen.WorkingArea;
            maxWidthPhysical = workArea.Width * 0.8;
            maxHeightPhysical = workArea.Height * 0.8;
        }

        var maxWidthDip = maxWidthPhysical * dpiScaleX;
        var maxHeightDip = maxHeightPhysical * dpiScaleY;

        var (scaledWidth, scaledHeight) = FloatingImageLayout.Fit(
            _nativeWidth, _nativeHeight, _currentScale * dpiScaleX,
            maxWidthDip, maxHeightDip);

        Width = scaledWidth;
        Height = scaledHeight;
        _displayWidth = scaledWidth / dpiScaleX;
        _displayHeight = scaledHeight / dpiScaleY;
    }

    private void DisplayImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClickThrough) return;
        _dragStart = e.GetPosition(null);
        _isDragging = true;
        DisplayImage.CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDragging)
        {
            _isDragging = false;
            DisplayImage.ReleaseMouseCapture();
        }
    }

    protected override void OnMouseMove(WpfMouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && DisplayImage.IsMouseCaptured)
        {
            var pos = e.GetPosition(null);
            var delta = pos - _dragStart;
            Left += delta.X;
            Top += delta.Y;
            _dragStart = pos;
        }
    }

    private void DisplayImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isClickThrough) return;

        bool isCtrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (isCtrlPressed)
        {
            int delta = e.Delta > 0 ? 5 : -5;
            var newOpacity = (Opacity * 100) + delta;
            SetOpacityPercent(newOpacity);
            e.Handled = true;
        }
        else
        {
            var scaleDelta = e.Delta > 0 ? 1.1 : (1.0 / 1.1);
            var newScale = _currentScale * scaleDelta;
            newScale = Math.Max(0.1, Math.Min(8.0, newScale));

            if (Math.Abs(newScale - _currentScale) > 0.01)
            {
                _currentScale = newScale;
                SizeToNativeImage();
            }
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (!_isClickThrough)
        {
            ShowContextMenu(e.GetPosition(this));
            e.Handled = true;
        }
    }

    private void ShowContextMenu(WpfPoint position)
    {
        var menu = new ContextMenu();

        var resetItem = new MenuItem { Header = "Kích thước gốc" };
        resetItem.Click += (s, e) =>
        {
            _currentScale = 1.0;
            SizeToNativeImage();
        };
        menu.Items.Add(resetItem);

        var fitItem = new MenuItem { Header = "Vừa màn hình" };
        fitItem.Click += (s, e) =>
        {
            _currentScale = 1.0;
            SizeToNativeImage();
        };
        menu.Items.Add(fitItem);

        menu.Items.Add(new Separator());

        var opacityLabels = new[] { "10%", "25%", "50%", "75%", "100%" };
        var opacityValues = new[] { 10.0, 25.0, 50.0, 75.0, 100.0 };
        foreach (var (label, value) in opacityLabels.Zip(opacityValues))
        {
            var item = new MenuItem { Header = $"Độ mờ {label}" };
            item.Click += (s, e) => SetOpacityPercent(value);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var clickThroughItem = new MenuItem
        {
            Header = "Xuyên chuột",
            IsCheckable = true,
            IsChecked = _isClickThrough
        };
        clickThroughItem.Click += (s, e) => SetClickThrough(!_isClickThrough);
        menu.Items.Add(clickThroughItem);

        menu.Items.Add(new Separator());

        var closeItem = new MenuItem { Header = "Đóng" };
        closeItem.Click += (s, e) => Close();
        menu.Items.Add(closeItem);

        menu.IsOpen = true;
    }

    private void FloatingImageWindow_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_isClickThrough)
        {
            Close();
            e.Handled = true;
        }
    }

    private void FloatingImageWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ClosedByUser?.Invoke(this, EventArgs.Empty);
    }
}
