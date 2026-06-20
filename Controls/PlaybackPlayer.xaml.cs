using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Serilog;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HeliVMS.Controls;

public partial class PlaybackPlayer : UserControl
{
    private WriteableBitmap? _writeableBitmap;

    private static readonly SolidColorBrush PlayingBrush = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private bool _playingBrushApplied;

    // Digital zoom state fields
    private double _zoomLevel = 1.0;
    private const double ZoomMin = 1.0;
    private const double ZoomMax = 8.0;
    private const double ZoomStep = 0.2;
    private Point _panStart;
    private bool _isPanning;

    public string CameraId { get; set; } = "";
    public string CameraName { get; set; } = "";
    public int ChannelNumber { get; set; }

    /// <summary>Recording type badge labels (up to 6 types)</summary>
    /// <summary>Display info with color-coded badges for recording types</summary>
    private static readonly (string Label, Color Color)[] RecTypeInfo =
    {
        ("連續", Color.FromRgb(0x21, 0x96, 0xF3)),   // 0=Continuous
        ("位移", Color.FromRgb(0x4C, 0xAF, 0x50)),   // 1=Motion
        ("警報", Color.FromRgb(0xF4, 0x43, 0x36)),   // 2=Alarm
        ("AI", Color.FromRgb(0xE9, 0x1E, 0x63)),   // 3=AI Event
    };

    public static readonly Color[] AccentPalette =
    {
        Color.FromRgb(0x21, 0x96, 0xF3),
        Color.FromRgb(0x4C, 0xAF, 0x50),
        Color.FromRgb(0xFF, 0x98, 0x00),
        Color.FromRgb(0xE9, 0x1E, 0x63),
        Color.FromRgb(0x9C, 0x27, 0xB0),
        Color.FromRgb(0x00, 0x96, 0x88),
        Color.FromRgb(0xF4, 0x43, 0x36),
        Color.FromRgb(0x3F, 0x51, 0xB5),
        Color.FromRgb(0xFF, 0x57, 0x22),
        Color.FromRgb(0x00, 0x7B, 0xDD),
        Color.FromRgb(0x67, 0x3A, 0xB7),
        Color.FromRgb(0x79, 0x55, 0x48),
        Color.FromRgb(0x60, 0x7D, 0x8B),
        Color.FromRgb(0x8B, 0xC3, 0x4A),
        Color.FromRgb(0x00, 0x89, 0x7B),
        Color.FromRgb(0x5C, 0x6B, 0xC0),
    };

    public event Action<PlaybackPlayer>? MaximizeRequested;
    public event Action<PlaybackPlayer>? PlayPauseRequested;
    public event Action<PlaybackPlayer>? Selected;
    public event Action<PlaybackPlayer>? SetAsMasterRequested;

    private bool _isSelected;
    private DispatcherTimer? _clickTimer;
    private static readonly Brush SelectedBorderBrush = new SolidColorBrush(Color.FromArgb(180, 0x4C, 0xAF, 0x50));
    private static readonly Brush NormalBorderBrush = new SolidColorBrush(Color.FromArgb(51, 0x80, 0x80, 0x80));

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            if (value)
            {
                var bdr = (Border)VisualTreeHelper.GetChild(this, 0);
                bdr.BorderBrush = SelectedBorderBrush;
                bdr.BorderThickness = new Thickness(2);
            }
            else
            {
                var bdr = (Border)VisualTreeHelper.GetChild(this, 0);
                bdr.BorderBrush = NormalBorderBrush;
                bdr.BorderThickness = new Thickness(1);
            }
        }
    }

    public PlaybackPlayer()
    {
        InitializeComponent();

        // Start with InfoOverlay hidden until video is shown
        InfoOverlay.Visibility = Visibility.Collapsed;

        MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) { return; }

            if (e.ClickCount == 1)
            {
                // First: select this player
                Selected?.Invoke(this);

                // Single click: toggle play/pause after debounce delay
                _clickTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                DispatcherTimer timer = _clickTimer;
                timer.Tick += (_, _) => // REVIEW: lambda captures 'this' ??consider weak event pattern
                {
                    timer.Stop();
                    PlayPauseRequested?.Invoke(this);
                };
                timer.Start();
            }
            else if (e.ClickCount == 2)
            {
                _clickTimer?.Stop();
                if (_zoomLevel > 1.0)
                {
                    ResetZoom();
                }
                else
                {
                    MaximizeRequested?.Invoke(this);
                }
            }
        };

        PreviewMouseWheel += OnPreviewMouseWheel;

        MouseDown += OnMiddleMouseDown;
        MouseMove += OnMiddleMouseMove;
        MouseUp += OnMiddleMouseUp;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) { return; }
        if (VideoImage.Visibility != Visibility.Visible) { return; }

        double delta = e.Delta > 0 ? ZoomStep : -ZoomStep;
        double newZoom = Math.Clamp(_zoomLevel + delta, ZoomMin, ZoomMax);
        if (Math.Abs(newZoom - _zoomLevel) < 0.01) { return; }

        var pos = e.GetPosition(VideoImage);
        double relX = (pos.X / VideoImage.ActualWidth - 0.5) * 2;
        double relY = (pos.Y / VideoImage.ActualHeight - 0.5) * 2;

        double scaleFactor = newZoom / _zoomLevel;
        _zoomLevel = newZoom;
        ZoomScale.ScaleX = _zoomLevel;
        ZoomScale.ScaleY = _zoomLevel;

        // Adjust pan to keep cursor point stable
        ZoomTranslate.X = ZoomTranslate.X * scaleFactor - relX * (VideoImage.ActualWidth * (scaleFactor - 1) / 2);
        ZoomTranslate.Y = ZoomTranslate.Y * scaleFactor - relY * (VideoImage.ActualHeight * (scaleFactor - 1) / 2);

        ClampPanOffset();
        UpdateZoomOverlay();
        e.Handled = true;
    }

    private void OnMiddleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _zoomLevel <= 1.0) { return; }
        _isPanning = true;
        _panStart = e.GetPosition(VideoImage);
        Cursor = Cursors.SizeAll;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMiddleMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || e.MiddleButton != MouseButtonState.Pressed) { return; }
        var pos = e.GetPosition(VideoImage);
        Vector delta = pos - _panStart;
        _panStart = pos;

        ZoomTranslate.X += delta.X;
        ZoomTranslate.Y += delta.Y;
        ClampPanOffset();
        e.Handled = true;
    }

    private void OnMiddleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) { return; }
        _isPanning = false;
        Cursor = Cursors.Arrow;
        ReleaseMouseCapture();
    }

    private void ClampPanOffset()
    {
        if (VideoImage.ActualWidth <= 0 || VideoImage.ActualHeight <= 0) { return; }
        double maxX = (VideoImage.ActualWidth * _zoomLevel - VideoImage.ActualWidth) / 2;
        double maxY = (VideoImage.ActualHeight * _zoomLevel - VideoImage.ActualHeight) / 2;
        ZoomTranslate.X = Math.Clamp(ZoomTranslate.X, -maxX, maxX);
        ZoomTranslate.Y = Math.Clamp(ZoomTranslate.Y, -maxY, maxY);
    }

    public void ResetZoom()
    {
        _zoomLevel = 1.0;
        ZoomScale.ScaleX = 1;
        ZoomScale.ScaleY = 1;
        ZoomTranslate.X = 0;
        ZoomTranslate.Y = 0;
        UpdateZoomOverlay();
    }

    public void ZoomToPoint(double zoomFactor, Point center)
    {
        if (VideoImage.Visibility != Visibility.Visible) { return; }
        _zoomLevel = Math.Clamp(zoomFactor, ZoomMin, ZoomMax);
        ZoomScale.ScaleX = _zoomLevel;
        ZoomScale.ScaleY = _zoomLevel;
        double relX = (center.X / VideoImage.ActualWidth - 0.5) * 2;
        double relY = (center.Y / VideoImage.ActualHeight - 0.5) * 2;
        ZoomTranslate.X = -relX * (VideoImage.ActualWidth * (_zoomLevel - 1) / 2);
        ZoomTranslate.Y = -relY * (VideoImage.ActualHeight * (_zoomLevel - 1) / 2);
        ClampPanOffset();
        UpdateZoomOverlay();
    }

    private void UpdateZoomOverlay()
    {
        if (_zoomLevel > 1.0)
        {
            ZoomOverlay.Visibility = Visibility.Visible;
            ZoomLevelText.Text = $"{_zoomLevel:F1}x";
        }
        else
        {
            ZoomOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void ZoomResetBtn_Click(object sender, RoutedEventArgs e)
    {
        ResetZoom();
    }

    /// <summary>Update progress bar (0.0 ~ 1.0)</summary>
    public void UpdateProgress(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        double trackWidth = ProgressTrack.ActualWidth;
        ProgressFill.Width = trackWidth * fraction;
    }

    /// <summary>Reset progress bar</summary>
    public void ResetProgress()
    {
        ProgressFill.Width = 0;
    }

    /// <summary>Set channel info and update UI labels</summary>
    public void SetChannelInfo(int channel, string name, string cameraId)
    {
        ChannelNumber = channel;
        CameraName = name;
        CameraId = cameraId;
        ChannelLabel.Text = $"CH{channel:D2}";
        CameraNameText.Text = name;
        OverlayText.Text = name;

        // Assign accent color from palette
        var accent = AccentPalette[(channel - 1) % AccentPalette.Length];
        AccentBar.Background = new SolidColorBrush(accent);

        // Apply accent color with transparency to camera name text
        CameraNameText.Foreground = new SolidColorBrush(Color.FromArgb(220, accent.R, accent.G, accent.B));
    }

    /// <summary>Mark this cell as the sync master with highlight border</summary>
    public void SetMaster(bool isMaster)
    {
        MasterBadge.Visibility = isMaster ? Visibility.Visible : Visibility.Collapsed;
        if (isMaster)
        {
            AccentBar.Width = 4;
            AccentBar.Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        }
        else
        {
            var ch = ChannelNumber > 0 ? ChannelNumber : 1;
            AccentBar.Width = 3;
            AccentBar.Background = new SolidColorBrush(AccentPalette[(ch - 1) % AccentPalette.Length]);
        }
    }

    /// <summary>Display a decoded video frame from pooled buffer</summary>
    public void ShowFrame(PooledBuffer frame)
    {
        int width = frame.Width;
        int height = frame.Height;
        if (_writeableBitmap is null ||
            _writeableBitmap.PixelWidth != width ||
            _writeableBitmap.PixelHeight != height)
        {
            _writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            VideoImage.Source = _writeableBitmap;
            VideoImage.Visibility = Visibility.Visible;
            NoSignalOverlay.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Collapsed;
            InfoOverlay.Visibility = Visibility.Visible;
            SetLoading(false);
        }
        else
        {
            NoSignalOverlay.Visibility = Visibility.Collapsed;
        }

        var copySize = _writeableBitmap.BackBufferStride * height;
        try
        {
            _writeableBitmap.Lock();
            Marshal.Copy(frame.Data, 0, _writeableBitmap.BackBuffer, Math.Min(frame.DataSize, copySize));
            _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            _writeableBitmap.Unlock();
        }

        if (!_playingBrushApplied)
        {
            StatusDot.Background = PlayingBrush;
            _playingBrushApplied = true;
        }
    }

    /// <summary>Show no-signal overlay with optional custom message</summary>
    public void ShowNoSignal(string message = "無訊號")
    {
        SetLoading(false);
        NoSignalOverlay.Visibility = Visibility.Visible;
        StatusDot.Background = Brushes.Red;
        var txt = (TextBlock)NoSignalOverlay.Child;
        txt.Text = message;
    }

    /// <summary>Set recording type badge: 0=Continuous, 1=Motion, 2=Alarm, 3=AI Event</summary>
    public void SetRecordingType(int recordType)
    {
        if (recordType < 0 || recordType >= RecTypeInfo.Length)
        {
            RecTypeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var (label, color) = RecTypeInfo[recordType];
        RecTypeText.Text = label;
        RecTypeBadge.Background = new SolidColorBrush(color);
        RecTypeBadge.Visibility = Visibility.Visible;
    }

    /// <summary>Set speed badge text (hidden for 1x)</summary>
    public void SetSpeedText(string text)
    {
        if (string.IsNullOrEmpty(text) || text == "1x")
        {
            SpeedBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            SpeedText.Text = text;
            SpeedBadge.Visibility = Visibility.Visible;
        }
    }

    private DispatcherTimer? _speedOSDTimer;

    /// <summary>Show speed OSD briefly, auto-hide after 1.5s</summary>
    public void ShowSpeedOSD(string text)
    {
        if (_speedOSDTimer is null)
        {
            _speedOSDTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _speedOSDTimer.Tick += (_, _) => // REVIEW: lambda captures 'this' ??consider weak event pattern
            {
                _speedOSDTimer.Stop();
                SpeedOSD.Visibility = Visibility.Collapsed;
            };
        }

        SpeedOSText.Text = text;
        SpeedOSD.Visibility = Visibility.Visible;
        _speedOSDTimer.Stop();
        _speedOSDTimer.Start();
    }

    /// <summary>Update timestamp display</summary>
    public void UpdateTimestamp(string text)
    {
        TimestampText.Text = text;
    }

    public string? SaveSnapshot(string directory)
    {
        if (_writeableBitmap is null) { return null; }

        try
        {
            Directory.CreateDirectory(directory);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeName = SanitizeFileName(CameraName);
            var fileName = $"CH{ChannelNumber:D2}_{safeName}_{timestamp}.png";
            var filePath = System.IO.Path.Combine(directory, fileName);

            int w = _writeableBitmap.PixelWidth;
            int h = _writeableBitmap.PixelHeight;

            // Composite frame + timestamp overlay using DrawingVisual
            var visual = new DrawingVisual();
            var text = $"{ChannelNumber}  {CameraName}  {TimestampText.Text}";
            double barHeight = 26;
            double fontSize = Math.Max(10, Math.Min(16, h * 0.025));

            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(_writeableBitmap, new Rect(0, 0, w, h));

                // Semi-transparent bar at bottom
                var barRect = new Rect(0, h - barHeight, w, barHeight);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), null, barRect);

                // Timestamp text
                var typeface = new Typeface("Consolas");
                var ft = new FormattedText(text,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface, fontSize, Brushes.White, 1.0)
                {
                    Trimming = TextTrimming.None
                };

                // Center text horizontally in the bar
                double textX = Math.Max(4, (w - ft.Width) / 2);
                double textY = h - barHeight + (barHeight - ft.Height) / 2;
                dc.DrawText(ft, new Point(textX, textY));
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var stream = File.OpenWrite(filePath);
            encoder.Save(stream);

            return filePath;
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] PlaybackPlayer.SaveSnapshot error: {Msg}", ex.Message);
            return null;
        }
    }

    private void CtxSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "HeliVMS_Screenshots");
        var path = SaveSnapshot(dir);
        if (path is not null)
        {
            Log.Debug("[HeliVMS] 已儲存快照至 {Path}", path);
        }
    }

    private void CtxMaximize_Click(object sender, RoutedEventArgs e)
    {
        MaximizeRequested?.Invoke(this);
    }

    private void CtxSetMaster_Click(object sender, RoutedEventArgs e)
    {
        SetAsMasterRequested?.Invoke(this);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            for (int j = 0; j < invalid.Length; j++)
            {
                if (chars[i] == invalid[j]) { chars[i] = '_'; break; }
            }
        }
        return new string(chars);
    }

    /// <summary>Compact mode for grid with many channels: smaller overlay/placeholder</summary>
    public void SetCompactMode(bool compact)
    {
        if (compact)
        {
            ChannelLabel.FontSize = 18;
            CameraNameText.FontSize = 9;
            StatusText.FontSize = 8;
            InfoOverlay.Height = 22;
            OverlayText.FontSize = 9;
            TimestampText.FontSize = 7;
        }
        else
        {
            ChannelLabel.FontSize = 26;
            CameraNameText.FontSize = 11;
            StatusText.FontSize = 10;
            InfoOverlay.Height = 28;
            OverlayText.FontSize = 11;
            TimestampText.FontSize = 9;
        }
    }

    private DispatcherTimer? _loadingTimer;
    private int _loadingDotCount;

    /// <summary>Show loading state with animated dots</summary>
    public void SetLoading(bool loading, string message = "載入中")
    {
        if (loading)
        {
            NoSignalOverlay.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
            StatusText.Text = message + "...";
            _loadingDotCount = 0;
            if (_loadingTimer is null)
            {
                _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _loadingTimer.Tick += (_, _) =>
                {
                    _loadingDotCount = (_loadingDotCount + 1) % 4;
                    StatusText.Text = message + new string('.', _loadingDotCount);
                };
            }
            _loadingTimer.Start();
        }
        else
        {
            if (_loadingTimer?.IsEnabled == true) _loadingTimer.Stop();
        }
    }

    public void ClearDisplay()
    {
        _writeableBitmap = null;
        VideoImage.Source = null;
        VideoImage.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        NoSignalOverlay.Visibility = Visibility.Collapsed;
        InfoOverlay.Visibility = Visibility.Collapsed;
        StatusText.Text = "無訊號";
        StatusDot.Background = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        SetLoading(false);
    }
}
