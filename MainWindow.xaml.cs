using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace VideoZoom;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ZoomState _zoom = new();
    private readonly VlcRenderer _vlc = new();

    private bool _isPanning = false;
    private bool _isMiniDragging = false;
    private Point _lastMouse;

    // Current CroppedBitmap feeding MainImage
    private CroppedBitmap? _currentCrop;
    private WriteableBitmap? _mainZoomBitmap;
    private WriteableBitmap? _lastMiniSource;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _zoom;


        this.PreviewMouseWheel += OnPreviewMouseWheel; // tunneling
        // When the renderer gets its first frame, set up bindings
        CompositionTarget.Rendering += OnRendering;
    }


    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vlc.VideoW == 0 || MainImage.Source == null) return;

        var p = e.GetPosition(MainImage);
        MapMainControlToSource(p, out double srcX, out double srcY);

        double factor = e.Delta > 0 ? 1.25 : 1.0 / 1.25;
        _zoom.ZoomAtPoint(factor, srcX, srcY);

        e.Handled = true; // stop others from “eating” the wheel
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // If no frame yet
        if (_vlc.FullFrameBitmap == null || _vlc.VideoW == 0) return;

        // Initialize zoom extents once
        if (_zoom.VideoW == 0)
        {
            _zoom.VideoW = _vlc.VideoW;
            _zoom.VideoH = _vlc.VideoH;
            _zoom.CenterX = _zoom.VideoW / 2.0;
            _zoom.CenterY = _zoom.VideoH / 2.0;
        }

        // Ensure miniature always points to current FullFrameBitmap instance
        if (MiniImage.Source != _vlc.FullFrameBitmap)
        {
            MiniImage.Source = _vlc.FullFrameBitmap;
        }

        // Compute crop rectangle for zoom
        var rect = _zoom.GetCropRect();
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // Recreate zoom bitmap if size changed (Option A)
        if (_mainZoomBitmap == null ||
            _mainZoomBitmap.PixelWidth != rect.Width ||
            _mainZoomBitmap.PixelHeight != rect.Height)
        {
            _mainZoomBitmap = new WriteableBitmap(rect.Width, rect.Height, 96, 96, PixelFormats.Bgra32, null);
            MainImage.Source = _mainZoomBitmap;
        }

        // Copy cropped region into zoom bitmap
        _mainZoomBitmap.Lock();
        try
        {
            unsafe
            {
                byte* srcBase = (byte*)_vlc.FullFrameBitmap.BackBuffer
                              + rect.Y * _vlc.FullFrameBitmap.BackBufferStride
                              + rect.X * 4;
                byte* dstBase = (byte*)_mainZoomBitmap.BackBuffer;

                int bytesPerRow = rect.Width * 4;
                for (int y = 0; y < rect.Height; y++)
                {
                    Buffer.MemoryCopy(
                        srcBase + y * _vlc.FullFrameBitmap.BackBufferStride,
                        dstBase + y * _mainZoomBitmap.BackBufferStride,
                        _mainZoomBitmap.BackBufferStride,
                        bytesPerRow);
                }
            }

            _mainZoomBitmap.AddDirtyRect(new Int32Rect(0, 0, rect.Width, rect.Height));
        }
        finally
        {
            _mainZoomBitmap.Unlock();
        }

        // Update miniature overlay rectangle
        UpdateMiniOverlayRect(rect);

        // Update seek slider
        UpdateSeekSlider();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vlc.VideoW == 0) return;

        // Only seek if user is dragging (avoid feedback loop)
        if (SeekSlider.IsMouseCaptureWithin)
        {
            double newPos = SeekSlider.Value / 100.0;
            _vlc.Position = newPos; // We'll implement setter in VlcRenderer
        }
    }

    private void UpdateMiniOverlayRect(Int32Rect cropRect)
    {
        if (_vlc.VideoW == 0 || _vlc.VideoH == 0) return;

        // Compute how MiniImage scales the source
        if (MiniImage.Source == null || MiniImage.ActualWidth == 0 || MiniImage.ActualHeight == 0) return;

        var imgW = _vlc.VideoW;
        var imgH = _vlc.VideoH;

        // Uniform scaling
        double scale = Math.Min(MiniImage.ActualWidth / imgW, MiniImage.ActualHeight / imgH);
        double drawW = imgW * scale;
        double drawH = imgH * scale;

        // Image draws centered inside its layout slot (assume top-left 0,0 and center offsets)
        double offsetX = (MiniImage.ActualWidth - drawW) / 2.0;
        double offsetY = (MiniImage.ActualHeight - drawH) / 2.0;

        // Map cropRect from source pixels to mini pixels
        double x = offsetX + cropRect.X * scale;
        double y = offsetY + cropRect.Y * scale;
        double w = cropRect.Width * scale;
        double h = cropRect.Height * scale;

        Canvas.SetLeft(MiniViewRect, x);
        Canvas.SetTop(MiniViewRect, y);
        MiniViewRect.Width = w;
        MiniViewRect.Height = h;

        // Stretch overlay canvas to same size as MiniImage layout
        MiniOverlay.Width = MiniImage.ActualWidth;
        MiniOverlay.Height = MiniImage.ActualHeight;
    }

    private void UpdateSeekSlider()
    {
        if (_vlc.VideoW == 0 || _vlc.FullFrameBitmap == null) return;

        var pos = _vlc.Position; // We'll expose this from VlcRenderer
        SeekSlider.Value = pos * 100; // VLC position is 0.0–1.0
    }


    // -------------------- UI commands ---------------------

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.ts|All Files|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            _vlc.Open(dlg.FileName);
            _zoom.CurrentOpenedFile = dlg.FileName;
            _vlc.Play();
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e) => _vlc.Play();
    private void Pause_Click(object sender, RoutedEventArgs e) => _vlc.Pause();
    private void Stop_Click(object sender, RoutedEventArgs e) => _vlc.Stop();

    // -------------------- Mouse interactions ---------------------

    private void MainHitLayer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vlc.VideoW == 0) return;

        // Convert mouse pos in MainImage control space -> source pixel space
        var pos = e.GetPosition(MainImage);
        if (MainImage.Source == null) return;

        // Determine how the cropped image is scaled to fill MainImage (Stretch=Fill)
        // We map mouse to source by reverse mapping through the scaling
        double srcX, srcY;
        MapMainControlToSource(pos, out srcX, out srcY);

        double factor = e.Delta > 0 ? 1.25 : (1.0 / 1.25);
        _zoom.ZoomAtPoint(factor, srcX, srcY);
    }

    private void MainHitLayer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _isPanning = true;
            _lastMouse = e.GetPosition(MainHitLayer);
            MainHitLayer.CaptureMouse();


            // Closed hand while dragging
           MainHitLayer.Cursor = Cursors.SizeAll;            // dragging


        }
    }

    private void MiniOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _vlc.VideoW == 0 || _vlc.VideoH == 0) return;

        _isMiniDragging = true;
        MiniOverlay.CaptureMouse();
        MiniOverlay.Cursor = Cursors.SizeAll;

        var p = e.GetPosition(MiniOverlay);
        SetZoomCenterFromMiniPoint(p);
    }

    private void MiniOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMiniDragging) return;

        var p = e.GetPosition(MiniOverlay);
        SetZoomCenterFromMiniPoint(p);
    }

    private void MiniOverlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _isMiniDragging = false;
        MiniOverlay.ReleaseMouseCapture();

        if (MiniOverlay.IsMouseOver)
            MiniOverlay.Cursor = Cursors.Hand;
        else
            MiniOverlay.ClearValue(CursorProperty);
    }

    private void MainHitLayer_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;

        var cur = e.GetPosition(MainHitLayer);
        var delta = cur - _lastMouse;
        _lastMouse = cur;

        // Convert delta from control pixels to source pixels
        var scale = GetMainScaleToSource();
        double dxSrc = -delta.X * scale.scaleX;
        double dySrc = -delta.Y * scale.scaleY;
        _zoom.PanBy(dxSrc, dySrc);
    }

    private void MainHitLayer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _isPanning = false;

            MainHitLayer.ReleaseMouseCapture();

            // Back to open hand when hover remains on the surface; otherwise clear
            if (MainHitLayer.IsMouseOver)
                MainHitLayer.Cursor = Cursors.Hand;
            else
                MainHitLayer.ClearValue(CursorProperty);

        }
    }

    // Map a point in MainImage layout space -> source pixel space considering current crop & scaling
    private void MapMainControlToSource(Point p, out double srcX, out double srcY)
    {
        var rect = _zoom.GetCropRect();
        var metrics = GetMainDrawMetrics(rect);

        var xInDraw = Math.Clamp(p.X - metrics.offsetX, 0.0, metrics.drawW);
        var yInDraw = Math.Clamp(p.Y - metrics.offsetY, 0.0, metrics.drawH);

        srcX = rect.X + xInDraw * rect.Width / Math.Max(1.0, metrics.drawW);
        srcY = rect.Y + yInDraw * rect.Height / Math.Max(1.0, metrics.drawH);
    }

    // For pan speed: how many source pixels per 1 control pixel
    private (double scaleX, double scaleY) GetMainScaleToSource()
    {
        var rect = _zoom.GetCropRect();
        var metrics = GetMainDrawMetrics(rect);
        double sx = rect.Width / Math.Max(1.0, metrics.drawW);
        double sy = rect.Height / Math.Max(1.0, metrics.drawH);
        return (sx, sy);
    }

    private (double drawW, double drawH, double offsetX, double offsetY) GetMainDrawMetrics(Int32Rect rect)
    {
        double controlW = Math.Max(1.0, MainImage.ActualWidth);
        double controlH = Math.Max(1.0, MainImage.ActualHeight);

        double sourceW = rect.Width > 0 ? rect.Width : Math.Max(1.0, _mainZoomBitmap?.PixelWidth ?? 1);
        double sourceH = rect.Height > 0 ? rect.Height : Math.Max(1.0, _mainZoomBitmap?.PixelHeight ?? 1);

        double scale = Math.Min(controlW / sourceW, controlH / sourceH);
        double drawW = sourceW * scale;
        double drawH = sourceH * scale;
        double offsetX = (controlW - drawW) / 2.0;
        double offsetY = (controlH - drawH) / 2.0;

        return (drawW, drawH, offsetX, offsetY);
    }

    private void SetZoomCenterFromMiniPoint(Point p)
    {
        var metrics = GetMiniDrawMetrics();
        if (metrics.scale <= 0) return;

        var xInDraw = Math.Clamp(p.X - metrics.offsetX, 0.0, metrics.drawW);
        var yInDraw = Math.Clamp(p.Y - metrics.offsetY, 0.0, metrics.drawH);

        _zoom.CenterX = xInDraw / metrics.scale;
        _zoom.CenterY = yInDraw / metrics.scale;
    }

    private (double scale, double drawW, double drawH, double offsetX, double offsetY) GetMiniDrawMetrics()
    {
        double viewW = Math.Max(1.0, MiniImage.ActualWidth);
        double viewH = Math.Max(1.0, MiniImage.ActualHeight);

        double imgW = Math.Max(1.0, _vlc.VideoW);
        double imgH = Math.Max(1.0, _vlc.VideoH);

        double scale = Math.Min(viewW / imgW, viewH / imgH);
        double drawW = imgW * scale;
        double drawH = imgH * scale;
        double offsetX = (viewW - drawW) / 2.0;
        double offsetY = (viewH - drawH) / 2.0;

        return (scale, drawW, drawH, offsetX, offsetY);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _vlc.Dispose();
    }
}