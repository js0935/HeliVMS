using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using HeliVMS.Helpers;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HeliVMS.Controls;

public partial class VideoPlayer : UserControl
{
    private Camera? _camera;
    public Camera? Camera => _camera;
    private IOnvifService? _onvif;
    private bool _useSubStream;
    public bool IsUsingSubStream => _useSubStream;
    private bool _isUnloaded;
    private bool _isMaximized;

    // Flyleaf hardware-accelerated player
    private Player? _flyleafPlayer;
    private Config? _flyleafConfig;

    // Digital zoom RenderTransform constants
        private const float ZoomEpsilon = 0.001f;
    private static readonly float[] ZoomLevels = { 1.0f, 1.25f, 1.5f, 1.75f, 2.0f };
    private int _zoomLevelIndex;
    private float _digitalZoom = 1.0f;
    private bool _isZoomed;
    private uint _decoderVidWidth, _decoderVidHeight;
    private int _panX, _panY;
    private double _panRemainderX, _panRemainderY;
    private bool _isPanning;
    private Point _panStartScreen;
    private int _panBaseX, _panBaseY;
    private DateTime _lastPanUpdate;
    private DispatcherTimer? _hudTimer;

    // Decoder reconnect state
    private const int MaxDecoderReconnectAttempts = 10;
    private static int _connectionSeq;
    private int _loadCameraSeq;
    private static int _loadCameraGen;
    private static readonly HashSet<string> _permanentFailures = new();

    // Batch UI frame update
    private static DispatcherTimer? _frameBatchTimer;
    private static readonly List<VideoPlayer> _activeFramePlayers = new();

    // Flyleaf player tracking (for global cleanup on shutdown)
    private static readonly List<Player> _flyleafPlayers = new();
    private byte[]? _pendingFrameData;
    private int _pendingFrameW, _pendingFrameH;
    private volatile bool _hasPendingFrame;

    // In-process FFmpeg streaming service
    private FFmpegStreamingService? _streamingService;
    private WriteableBitmap? _decoderBitmap;
    private string? _decoderRtspUrl;
    private int _streamFailCount;

    // Decoder info OSD: FPS / resolution
    private int _osdFrameCount;
    private DateTime _osdLastReset;
    private DispatcherTimer? _osdTimer;

    /// <summary>Target decode height (0 = source height, set before LoadCamera)</summary>
    public int TargetDecodeHeight { get; set; } = 360;
    /// <summary>Target decode FPS (0 = max, set before LoadCamera)</summary>
    public int TargetFps { get; set; } = 15;

    public bool IsMaximized
    {
        get => _isMaximized;
        set => _isMaximized = value;
    }

    public event Action<Camera?>? PtzSelected;
    public event Action<Camera?>? MaximizeRequested;

    public void SuspendVideo()
    {
        DecoderFrameImage.Visibility = Visibility.Collapsed;
        StopStreaming();
    }

    public void ResumeVideo()
    {
        if (_camera is null) { return; }
        var (url, user, pass) = ResolveStreamUrl(_camera, _useSubStream);
        if (!string.IsNullOrEmpty(url))
        {
            StopStreaming();
            StartPlaybackDecoder(url, user, pass, _camera);
        }
    }

    public void SetOverlayName(string name)
    {
        CameraNameText.Text = name;
        CameraNameTextPlaceholder.Text = name;
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            CameraNameText.FontSize = settings.Settings.OverlayFontSize;
        }
        catch (Exception ex) { Log.Debug("[HeliVMS] SetOverlayName error: {Msg}", ex.Message); }
        UpdateHealthBadge();
    }

    public void UpdateHealthBadge()
    {
        if (_camera is { DisconnectCount: > 0 })
        {
            HealthBadge.Text = $"!!{_camera.DisconnectCount}";
            HealthBadge.Visibility = Visibility.Visible;
        }
        else
        {
            HealthBadge.Visibility = Visibility.Collapsed;
        }
    }

    private static IEventService? GetEventLog()
    {
        try { return App.Services.GetRequiredService<IEventService>(); }
        catch { return null; }
    }

    private static void Notify(string message, string severity = "WARN")
    {
        try { App.Services.GetRequiredService<INotificationService>().Show(message, severity); }
        catch { }
    }

    private static MediaMTXService? TryGetMediaMTX()
    {
        try { return App.Services.GetRequiredService<MediaMTXService>(); }
        catch { return null; }
    }

    /// <summary>Resolve optimal stream URL: MediaMTX relay when available, direct RTSP fallback</summary>
    private (string url, string user, string pass) ResolveStreamUrl(Camera camera, bool useSubStream)
    {
        var mtx = TryGetMediaMTX();
        if (mtx is not null && mtx.IsRunning)
        {
            var relayUrl = mtx.GetRelayRtspUrl(camera, useSubStream);
            return (relayUrl, "", "");
        }

        var rawUrl = useSubStream ? camera.RtspUrlSub : camera.RtspUrl;
        return (rawUrl ?? "", camera.Username ?? "", camera.Password ?? "");
    }

    public void SetFullBleed()
    {
        PlayerBorder.BorderThickness = new Thickness(0);
        PlayerBorder.CornerRadius = new CornerRadius(0);
    }

    public VideoPlayer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        this.LostMouseCapture += VideoPlayer_LostMouseCapture;

        _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hudTimer.Tick += HudTimer_Tick;
    }

    private void VideoPlayer_LostMouseCapture(object? sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            Log.Debug("[HeliVMS] Pan ABORT (lost capture): final=({PanX},{PanY})", _panX, _panY);
            if (Mouse.OverrideCursor == Cursors.Hand)
            {
                Mouse.OverrideCursor = null;
            }
        }
    }

    private void HudTimer_Tick(object? sender, EventArgs e) { HideZoomHud(); }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _hudTimer?.Stop();
        CancelTimers();
        ResetReconnectState();
    }

    public void LoadCamera(Camera camera, bool useSubStream = false)
    {
        _isUnloaded = false;
        _camera = camera;
        _useSubStream = useSubStream;
        _onvif = App.Services.GetService<IOnvifService>();
        CameraNameText.Text = camera.Name;
        CameraNameTextPlaceholder.Text = camera.Name;

        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            CameraNameText.FontSize = settings.Settings.OverlayFontSize;
        }
        catch (Exception ex) { Log.Debug("[HeliVMS] LoadCamera font error: {Msg}", ex.Message); }

        var hasSub = !string.IsNullOrEmpty(camera.RtspUrlSub);
        StreamToggle.Visibility = hasSub ? Visibility.Visible : Visibility.Collapsed;
        UpdateStreamToggleText();

        var (streamUrl, streamUser, streamPass) = ResolveStreamUrl(camera, useSubStream);
        if (camera.IsEnabled && !string.IsNullOrEmpty(streamUrl))
        {
            StatusText.Text = "連線中...";
            DecoderFrameImage.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
            int delayMs = (Interlocked.Increment(ref _connectionSeq) % 64) * 100;
            var urlCopy = streamUrl;
            var userCopy = streamUser;
            var passCopy = streamPass;
            var camCopy = camera;
            var disp = Dispatcher;
            var capturedGen = _loadCameraSeq = Interlocked.Increment(ref _loadCameraGen);
            _ = Task.Delay(delayMs).ContinueWith(_ =>
            {
                disp.InvokeAsync(() =>
                {
                    if (_isUnloaded || _camera is null || capturedGen != _loadCameraSeq) { return; }
                    StartPlayback(urlCopy, userCopy, passCopy, camCopy);
                }, DispatcherPriority.Normal);
            });
        }
        else
        {
            StatusText.Text = "未連線";
            DecoderFrameImage.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
        }

        PtzOverlay.Visibility = camera.HasPTZ ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartPlayback(string rtspUrl, string username, string password, Camera camera)
    {
        if (TryStartFlyleaf(rtspUrl, username, password))
        {
            return;
        }
        StartPlaybackDecoder(rtspUrl, username, password, camera);
    }

    private bool TryStartFlyleaf(string rtspUrl, string username, string password)
    {
        try
        {
            SaveZoomForRestore();
            ResetZoomState();

            StopStreaming();

            var url = rtspUrl;
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                var uri = new Uri(rtspUrl);
                var userInfo = $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}";
                url = $"rtsp://{userInfo}@{uri.Host}:{uri.Port}{uri.PathAndQuery}";
            }

            DecoderFrameImage.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Collapsed;
            ReconnectingOverlay.Visibility = Visibility.Collapsed;
            StatusText.Text = "連線中...";

            _flyleafConfig = new Config();
            _flyleafConfig.Video.ClearScreen = true;
            _flyleafConfig.Video.VideoAcceleration = true;
            _flyleafConfig.Decoder.LowDelay = true;
            _flyleafConfig.Decoder.AllowDropFrames = true;
            _flyleafConfig.Player.ZeroLatency = true;

            _flyleafPlayer = new Player(_flyleafConfig);
            FlyleafHost.Player = _flyleafPlayer;
            FlyleafHost.Visibility = Visibility.Visible;
            StartInfoOSD();

            var cp = _flyleafPlayer;
            _flyleafPlayer.OpenCompleted += (_, _) =>
            {
                if (_flyleafPlayer != cp) return;
                _ = Dispatcher.BeginInvoke(OnFlyleafOpenCompletedCore);
            };
            _flyleafPlayer.PlaybackStopped += (_, _) =>
            {
                if (_flyleafPlayer != cp) return;
                _ = Dispatcher.BeginInvoke(OnFlyleafPlaybackStoppedCore);
            };

            lock (_flyleafPlayers) { _flyleafPlayers.Add(_flyleafPlayer); }

            _flyleafPlayer.OpenAsync(url);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VideoPlayer:{Name}] Flyleaf start failed", _camera?.Name);
            CleanupFlyleaf();
            return false;
        }
    }

    private void OnFlyleafOpenCompletedCore()
    {
        if (_isUnloaded || _camera is null) { return; }
        StatusText.Text = "串流播放中";
        Placeholder.Visibility = Visibility.Collapsed;
        ReconnectingOverlay.Visibility = Visibility.Collapsed;

        if (!_camera.IsConnected)
        {
            _camera.IsConnected = true;
            _camera.LastConnectedAt = DateTime.Now;
            _camera.FirstConnectedAt ??= DateTime.Now;
            UpdateHealthBadge();
            GetEventLog()?.LogInfo("Connection", "VideoPlayer", $"{_camera.Name} 已成功連接 (Flyleaf)");
            Notify($"{_camera.Name} 已成功連接 (Flyleaf)", "INFO");
        }
    }

    private void OnFlyleafPlaybackStoppedCore()
    {
        if (_isUnloaded || _camera is null) { return; }

        bool wasConnected = _camera.IsConnected;
        _camera.IsConnected = false;
        _camera.LastDisconnectedAt = DateTime.Now;
        _camera.DisconnectCount++;
        UpdateHealthBadge();

        if (wasConnected)
        {
            GetEventLog()?.LogWarning("Connection", "VideoPlayer", $"{_camera.Name} 連線中斷");
            Notify($"{_camera.Name} 連線中斷", "WARN");
        }

        if (_flyleafPlayer is not null)
        {
            StatusText.Text = "連線中斷，重新連線中...";
            ReconnectingOverlay.Visibility = Visibility.Visible;
        }
    }

    private void CleanupFlyleaf()
    {
        FlyleafHost.Visibility = Visibility.Collapsed;
        FlyleafHost.Player = null;

        if (_flyleafPlayer is not null)
        {
            lock (_flyleafPlayers) { _flyleafPlayers.Remove(_flyleafPlayer); }
            try { _flyleafPlayer.Stop(); } catch (Exception ex) { Log.Debug("[HeliVMS] Flyleaf Stop: {Msg}", ex.Message); }
            try { _flyleafPlayer.Dispose(); } catch { }
            _flyleafPlayer = null;
            _flyleafConfig = null;
        }
    }

    private string? SaveSnapshotFlyleaf(string directory)
    {
        if (_flyleafPlayer is null) return null;
        try
        {
            Directory.CreateDirectory(directory);
            var cameraName = _camera?.Name ?? "Unknown";
            var channel = _camera?.ChannelNumber ?? 0;
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeName = SanitizeFileName(cameraName);
            var fileName = $"CH{channel:D2}_{safeName}_{timestamp}.png";
            var filePath = Path.Combine(directory, fileName);

            var bmp = _flyleafPlayer.TakeSnapshotToBitmapSource();
            if (bmp is null) return null;

            using var stream = File.Create(filePath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(stream);
            return filePath;
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] SaveSnapshotFlyleaf error: {Msg}", ex.Message);
            return null;
        }
    }

    private void StartPlaybackDecoder(string rtspUrl, string username, string password, Camera camera)
    {
        SaveZoomForRestore();
        ResetZoomState();

        if (_permanentFailures.Contains(camera.Id))
        {
            StatusText.Text = "此攝影機URL已永久失效";
            return;
        }

        StopStreaming();

        _decoderRtspUrl = rtspUrl;
        _streamFailCount = 0;
        DecoderFrameImage.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Collapsed;
        ReconnectingOverlay.Visibility = Visibility.Collapsed;

        bool hasFrozenFrame = _decoderBitmap is not null;
        if (hasFrozenFrame)
        {
            DecoderFrameImage.Visibility = Visibility.Visible;
            ReconnectingOverlay.Visibility = Visibility.Visible;
            ReconnectAttemptText.Text = $"重連 {_streamFailCount}/{MaxDecoderReconnectAttempts} 次";
        }
        else
        {
            Placeholder.Visibility = Visibility.Visible;
        }
        StatusText.Text = "連線中...";

        _streamingService = new FFmpegStreamingService();
        // Adjust decode height for target FPS
        if (TargetDecodeHeight > 0)
            _streamingService.TargetDecodeHeight = TargetDecodeHeight;
        if (TargetFps > 0)
        {
            _streamingService.TargetFps = TargetFps;
        }
        _streamingService.FrameReady += OnDecoderFrameReady;
        _streamingService.PlayStatus += OnPlayStatusChanged;
        _streamingService.StreamStuck += OnStreamStuck;
        RegisterActiveFramePlayer();
        _streamingService.Play(rtspUrl, username, password);
        StartInfoOSD();
    }

    private void OnPlayStatusChanged(bool isPlaying)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUnloaded || _camera is null) { return; }

            if (isPlaying)
            {
                _streamFailCount = 0;
                StatusText.Text = "串流播放中";
                ReconnectingOverlay.Visibility = Visibility.Collapsed;
                _camera.IsConnected = true;
                _camera.LastConnectedAt = DateTime.Now;
                _camera.FirstConnectedAt ??= DateTime.Now;
                UpdateHealthBadge();
            }
            else
            {
                _streamFailCount++;
                if (_streamFailCount >= MaxDecoderReconnectAttempts)
                {
                    Log.Warning("[VideoPlayer:{Name}] Max stream reconnect attempts reached, stopping", _camera?.Name);
                    StatusText.Text = "已達最大重連次數，停止";
                    StopStreaming();
                    return;
                }

                StatusText.Text = $"正在重新連線 ({_streamFailCount}/{MaxDecoderReconnectAttempts})";
                ReconnectAttemptText.Text = $"重連 {_streamFailCount}/{MaxDecoderReconnectAttempts} 次";
                if (DecoderFrameImage.Visibility == Visibility.Visible)
                {
                    ReconnectingOverlay.Visibility = Visibility.Visible;
                }
                _camera.IsConnected = false;
                _camera.LastDisconnectedAt = DateTime.Now;
                _camera.DisconnectCount++;
                UpdateHealthBadge();
                GetEventLog()?.LogWarning("Connection", "VideoPlayer", $"{_camera.Name} 連線中斷");
                Notify($"{_camera.Name} 連線中斷", "WARN");
            }
        });
    }

    private void OnStreamStuck()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            Log.Warning("[VideoPlayer:{Name}] Stream stuck, restarting service", _camera?.Name);
            StatusText.Text = "串流卡住，重新啟動中...";
            StopStreaming();
            if (!_isUnloaded && _camera is not null && _decoderRtspUrl is not null)
            {
                StartPlaybackDecoder(_decoderRtspUrl, "", "", _camera);
            }
        });
    }

    private void OnDecoderFrameReady(byte[] frameData, int width, int height)
    {
        if (_isUnloaded || _camera is null) { return; }

        Interlocked.Increment(ref _osdFrameCount);

        var rented = ArrayPool<byte>.Shared.Rent(frameData.Length);
        Buffer.BlockCopy(frameData, 0, rented, 0, frameData.Length);

        var old = Interlocked.Exchange(ref _pendingFrameData, rented);
        if (old is not null) { ArrayPool<byte>.Shared.Return(old); }

        _pendingFrameW = width;
        _pendingFrameH = height;
        _hasPendingFrame = true;
    }

    private void StopStreaming()
    {
        CleanupFlyleaf();
        UnregisterActiveFramePlayer();
        StopInfoOSD();
        if (_streamingService is not null)
        {
            _streamingService.FrameReady -= OnDecoderFrameReady;
            _streamingService.PlayStatus -= OnPlayStatusChanged;
            _streamingService.StreamStuck -= OnStreamStuck;
            _streamingService.Stop();
            _streamingService.Dispose();
            _streamingService = null;
        }
    }

    private void SaveZoomForRestore()
    {
        // Zoom state is preserved across stream toggles in maximized mode
    }

    private void ResetZoomState()
    {
        _zoomLevelIndex = 0;
        _digitalZoom = 1.0f;
        _isZoomed = false;
        _decoderVidWidth = 0;
        _decoderVidHeight = 0;
        _panX = 0;
        _panY = 0;
        _panRemainderX = 0;
        _panRemainderY = 0;
        _isPanning = false;
        DecoderZoomScale.ScaleX = 1;
        DecoderZoomScale.ScaleY = 1;
        DecoderZoomTranslate.X = 0;
        DecoderZoomTranslate.Y = 0;
        HideZoomHud();
    }

    private void CancelTimers()
    {
        _osdTimer?.Stop();
        _osdTimer = null;
    }

    private void StartInfoOSD()
    {
        InfoOSD.Visibility = Visibility.Visible;
        _osdFrameCount = 0;
        _osdLastReset = DateTime.UtcNow;
        _osdTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _osdTimer.Tick -= OsdTimer_Tick;
        _osdTimer.Tick += OsdTimer_Tick;
        _osdTimer.Start();
    }

    private void StopInfoOSD()
    {
        InfoOSD.Visibility = Visibility.Collapsed;
        _osdTimer?.Stop();
    }

    private void OsdTimer_Tick(object? sender, EventArgs e)
    {
        var camName = _camera?.Name ?? "";
        if (_flyleafPlayer is { Video.IsOpened: true })
        {
            InfoOSText.Text = $"{camName}  Flyleaf HW  {_flyleafPlayer.Video.FPSCurrent:F1} FPS";
            return;
        }
        int count = Interlocked.Exchange(ref _osdFrameCount, 0);
        var elapsed = DateTime.UtcNow - _osdLastReset;
        _osdLastReset = DateTime.UtcNow;
        double fps = elapsed.TotalSeconds > 0 ? count / elapsed.TotalSeconds : 0;
        InfoOSText.Text = $"{camName}  {_decoderVidWidth}\u00D7{_decoderVidHeight}  {fps:F1} FPS";
    }

    private void UpdateStreamToggleText()
    {
        StreamToggleText.Text = _useSubStream ? "SD" : "HD";
        StreamToggle.ToolTip = _useSubStream
            ? "切換至主串流 (HD)"
            : "切換至子串流 (SD)";
    }

    private void ToggleStream()
    {
        if (_camera is null) { return; }
        _useSubStream = !_useSubStream;
        var (url, user, pass) = ResolveStreamUrl(_camera, _useSubStream);
        if (string.IsNullOrEmpty(url))
        {
            _useSubStream = !_useSubStream;
            return;
        }
        UpdateStreamToggleText();
        StopStreaming();
        StartPlayback(url, user, pass, _camera);
    }

    private void StreamToggle_Click(object sender, RoutedEventArgs e)
    {
        ToggleStream();
    }

    public void DetachCamera()
    {
        _isUnloaded = true;
        UnregisterActiveFramePlayer();
        DecoderFrameImage.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        _hudTimer?.Stop();
        CancelTimers();
        ResetReconnectState();
        if (Mouse.OverrideCursor == Cursors.Hand)
        {
            Mouse.OverrideCursor = null;
        }
    }

    public void UnloadCamera()
    {
        _isUnloaded = true;
        StopStreaming();
        DecoderFrameImage.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        _hudTimer?.Stop();
        CancelTimers();
        ResetReconnectState();
        if (Mouse.OverrideCursor == Cursors.Hand)
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ==============================================================================
    //  Digital zoom / pan mouse event handlers
    // ==============================================================================

    private void RootGrid_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isMaximized) { return; }
        if (_decoderVidWidth == 0 || _decoderVidHeight == 0) { return; }

        int direction = e.Delta > 0 ? 1 : -1;
        int newIdx = Math.Clamp(_zoomLevelIndex + direction, 0, ZoomLevels.Length - 1);
        if (newIdx == _zoomLevelIndex) { return; }

        float oldZoom = _digitalZoom;
        _zoomLevelIndex = newIdx;
        _digitalZoom = ZoomLevels[_zoomLevelIndex];

        if (_decoderVidWidth > 0 && _decoderVidHeight > 0)
        {
            var cursorPos = e.GetPosition(DecoderFrameImage);
            double w = DecoderFrameImage.ActualWidth;
            double h = DecoderFrameImage.ActualHeight;
            if (w > 0 && h > 0)
            {
                double fracX = Math.Clamp(cursorPos.X / w, 0, 1);
                double fracY = Math.Clamp(cursorPos.Y / h, 0, 1);

                var (oldCropW, oldCropH, _, _) = ComputeCropBounds(oldZoom);
                var (newCropW, newCropH, maxX, maxY) = ComputeCropBounds(_digitalZoom);

                int cursorVidPxX = _panX + (int)(fracX * oldCropW);
                int cursorVidPxY = _panY + (int)(fracY * oldCropH);
                _panX = Math.Clamp(cursorVidPxX - (int)(fracX * newCropW), 0, maxX);
                _panY = Math.Clamp(cursorVidPxY - (int)(fracY * newCropH), 0, maxY);
            }
        }

        ApplyDecoderZoom();
        ShowZoomHud();
        e.Handled = true;
    }

    private void RootGrid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _isZoomed)
        {
            ResetDigitalZoom();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            if (_isZoomed)
            {
                ResetDigitalZoom();
                e.Handled = true;
                return;
            }
            MaximizeRequested?.Invoke(_camera);
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) { return; }

        if (_isZoomed)
        {
            _isPanning = true;
            _panStartScreen = e.GetPosition(this);
            _panBaseX = _panX;
            _panBaseY = _panY;
            _lastPanUpdate = DateTime.MinValue;
            Mouse.Capture((IInputElement)sender);
            Mouse.OverrideCursor = Cursors.Hand;
            Log.Debug("[HeliVMS] Pan START: base=({PanBaseX},{PanBaseY}), zoom={Zoom:F2}", _panBaseX, _panBaseY, _digitalZoom);
            e.Handled = true;
            return;
        }

        if (_camera is not null)
        {
            var el = this as DependencyObject;
            while (el is not null && el is not Controls.CameraGrid)
            {
                el = VisualTreeHelper.GetParent(el);
            }
            if (el is Controls.CameraGrid cameraGrid)
            {
                cameraGrid.TryStartDragFromPlayer(this);
            }
        }
    }

    private void RootGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) { return; }

        var now = DateTime.UtcNow;
        if ((now - _lastPanUpdate).TotalMilliseconds < 33) { return; }
        _lastPanUpdate = now;

        ScheduleHudAutoHide();

        if (_decoderVidWidth == 0 || _decoderVidHeight == 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var pt = e.GetPosition(this);
        double dx = pt.X - _panStartScreen.X;
        double dy = pt.Y - _panStartScreen.Y;

        double maxDelta = Math.Min(ActualWidth, ActualHeight) * 0.15;
        dx = Math.Clamp(dx, -maxDelta, maxDelta);
        dy = Math.Clamp(dy, -maxDelta, maxDelta);

        var (cropW, cropH, _, _) = ComputeCropBounds(_digitalZoom);
        double scaleX = (double)cropW / ActualWidth;
        double scaleY = (double)cropH / ActualHeight;

        double rawX = _panBaseX - dx * scaleX + _panRemainderX;
        double rawY = _panBaseY - dy * scaleY + _panRemainderY;
        int newX = (int)Math.Round(rawX, MidpointRounding.AwayFromZero);
        int newY = (int)Math.Round(rawY, MidpointRounding.AwayFromZero);
        _panRemainderX = rawX - newX;
        _panRemainderY = rawY - newY;

        if (newX != _panX || newY != _panY)
        {
            _panX = newX;
            _panY = newY;
            ApplyDecoderZoom();
        }
        e.Handled = true;
    }

    private void RootGrid_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) { return; }
        _isPanning = false;
        if (Mouse.OverrideCursor == Cursors.Hand)
        {
            Mouse.OverrideCursor = null;
        }
        Mouse.Capture((IInputElement)sender, CaptureMode.None);
        e.Handled = true;
    }

    private void RootGrid_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            if (Mouse.OverrideCursor == Cursors.Hand)
            {
                Mouse.OverrideCursor = null;
            }
            Mouse.Capture((IInputElement)sender, CaptureMode.None);
        }
        ContextMenu?.IsOpen = true;
    }

    private void ApplyDecoderZoom()
    {
        if (_digitalZoom <= (1.0f + ZoomEpsilon))
        {
            DecoderZoomScale.ScaleX = 1;
            DecoderZoomScale.ScaleY = 1;
            DecoderZoomTranslate.X = 0;
            DecoderZoomTranslate.Y = 0;
            _isZoomed = false;
            return;
        }

        if (_decoderVidWidth == 0 || _decoderVidHeight == 0) { return; }

        var (cropW, cropH, maxX, maxY) = ComputeCropBounds(_digitalZoom);
        _panX = Math.Clamp(_panX, 0, maxX);
        _panY = Math.Clamp(_panY, 0, maxY);

        double scale = _digitalZoom;
        DecoderZoomScale.ScaleX = scale;
        DecoderZoomScale.ScaleY = scale;

        double viewW = _decoderVidWidth / scale;
        double viewH = _decoderVidHeight / scale;
        double tx = -(_panX + viewW / 2 - _decoderVidWidth / 2);
        double ty = -(_panY + viewH / 2 - _decoderVidHeight / 2);
        DecoderZoomTranslate.X = tx;
        DecoderZoomTranslate.Y = ty;

        _isZoomed = true;
    }

    private (uint cropW, uint cropH, int maxX, int maxY) ComputeCropBounds(float zoom)
    {
        uint w = _decoderVidWidth;
        uint h = _decoderVidHeight;
        if (w == 0 || h == 0) { return (1, 1, 0, 0); }
        uint cropW = Math.Max(1u, (uint)(w / (double)zoom));
        uint cropH = Math.Max(1u, (uint)(h / (double)zoom));
        int maxX = Math.Max(0, (int)(w - cropW));
        int maxY = Math.Max(0, (int)(h - cropH));
        return (cropW, cropH, maxX, maxY);
    }

    private void UpdateZoomHud()
    {
        string text = "";
        if (_isZoomed)
        {
            if (_decoderVidWidth > 0 && _decoderVidHeight > 0)
            {
                var (_, _, maxX, maxY) = ComputeCropBounds(_digitalZoom);
                string edge = "";
                if (_panX <= 0) { edge += "左"; }
                else if (_panX >= maxX) edge += "右";
                if (_panY <= 0) { edge += "上"; }
                else if (_panY >= maxY) edge += "下";
                text = $"{(int)(_digitalZoom * 100)}% {edge}".Trim();
            }
        }
        DecoderZoomHud.Text = text;
    }

    private void ShowZoomHud()
    {
        UpdateZoomHud();
        DecoderZoomHud.Visibility = Visibility.Visible;
        ScheduleHudAutoHide();
    }

    private void HideZoomHud()
    {
        DecoderZoomHud.Visibility = Visibility.Collapsed;
    }

    private void ScheduleHudAutoHide()
    {
        _hudTimer?.Stop();
        _hudTimer?.Start();
    }

    private void ResetDigitalZoom()
    {
        _zoomLevelIndex = 0;
        _digitalZoom = 1.0f;
        _panX = 0;
        _panY = 0;
        _panRemainderX = 0;
        _panRemainderY = 0;
        _isPanning = false;
        _isZoomed = false;
        DecoderZoomScale.ScaleX = 1;
        DecoderZoomScale.ScaleY = 1;
        DecoderZoomTranslate.X = 0;
        DecoderZoomTranslate.Y = 0;
        if (Mouse.OverrideCursor == Cursors.Hand)
        {
            Mouse.OverrideCursor = null;
        }
        HideZoomHud();
    }

    private void ResetReconnectState()
    {
    }

    private void CtxFullscreen_Click(object sender, RoutedEventArgs e)
    {
        var parent = Parent as FrameworkElement;
        while (parent is not null and not Views.LiveView)
        {
            parent = parent.Parent as FrameworkElement;
        }
        if (parent is Views.LiveView liveView)
        {
            liveView.ToggleFullScreen();
        }
    }

    private void CtxStreamToggle_Click(object sender, RoutedEventArgs e)
    {
        ToggleStream();
    }

    private void CtxPtzSelect_Click(object sender, RoutedEventArgs e)
    {
        PtzSelected?.Invoke(_camera);
    }

    public string? SaveSnapshot(string directory)
    {
        // Try Flyleaf snapshot first (hardware-accelerated)
        if (_flyleafPlayer is not null)
        {
            return SaveSnapshotFlyleaf(directory);
        }

        // Fallback to decoder WriteableBitmap snapshot
        if (_decoderBitmap is null) return null;

        try
        {
            Directory.CreateDirectory(directory);

            var cameraName = _camera?.Name ?? "Unknown";
            var channel = _camera?.ChannelNumber ?? 0;
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeName = SanitizeFileName(cameraName);
            var fileName = $"CH{channel:D2}_{safeName}_{timestamp}.png";
            var filePath = Path.Combine(directory, fileName);

            using var stream = File.Create(filePath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_decoderBitmap));
            encoder.Save(stream);

            return filePath;
        }
        catch (Exception ex)
        {
            Log.Debug("[HeliVMS] SaveSnapshot error: {Msg}", ex.Message);
            return null;
        }
    }

    private void CtxSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "HeliVMS_Screenshots");
        var path = SaveSnapshot(dir);
        if (path is not null)
        {
            Log.Debug("[HeliVMS] 快照已儲存至 {Path}", path);
            MessageBox.Show($"快照已儲存至\n{path}", "快照",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("快照失敗：無法儲存快照", "快照",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    private static readonly Color ColorContinuous = Color.FromRgb(0x21, 0x96, 0xF3);
    private static readonly Color ColorMotion = Color.FromRgb(0x4C, 0xAF, 0x50);
    private static readonly Color ColorAlarm = Color.FromRgb(0xF4, 0x43, 0x36);
    private static readonly Color ColorSmart = Color.FromRgb(0xE9, 0x1E, 0x63);
    private static readonly Color ColorWeighted = Color.FromRgb(0xFF, 0x98, 0x00);
    private static readonly Color ColorNone = Colors.Transparent;

    public void SwitchToMainStream()
    {
        if (_camera is null) { return; }
        if (!_useSubStream) { return; }
        _useSubStream = false;
        var (url, user, pass) = ResolveStreamUrl(_camera, false);
        if (!string.IsNullOrEmpty(url))
        {
            UpdateStreamToggleText();
            StartPlayback(url, user, pass, _camera);
        }
    }

    public void SwitchToSubStream()
    {
        if (_camera is null) { return; }
        if (_useSubStream) { return; }
        _useSubStream = true;
        var (url, user, pass) = ResolveStreamUrl(_camera, true);
        if (!string.IsNullOrEmpty(url))
        {
            UpdateStreamToggleText();
            StartPlayback(url, user, pass, _camera);
        }
    }

    public void SetRecordingMode(ScheduleMode mode)
    {
        var color = mode switch
        {
            ScheduleMode.Continuous => ColorContinuous,
            ScheduleMode.Motion => ColorMotion,
            ScheduleMode.Alarm => ColorAlarm,
            ScheduleMode.Smart => ColorSmart,
            ScheduleMode.Weighted => ColorWeighted,
            _ => ColorNone,
        };
        SetRecordingModeColor(color);
    }

    public void SetRecordingModeColor(Color color)
    {
        RecordingModeDot.Fill = new SolidColorBrush(color);
        RecordingModeDot.Visibility = color == Colors.Transparent
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void PtzMove(float x, float y, float zoom)
    {
        if (_camera is null || _onvif is null) { return; }
        _ = _onvif.PTZ_ContinuousMoveAsync(
            _camera.IpAddress ?? "", _camera.OnvifPort,
            _camera.Username ?? "", _camera.Password ?? "",
            x, y, zoom);
    }

    private void PtzStop()
    {
        if (_camera is null || _onvif is null) { return; }
        _ = _onvif.PTZ_StopAsync(
            _camera.IpAddress ?? "", _camera.OnvifPort,
            _camera.Username ?? "", _camera.Password ?? "");
    }

    private static void EnsureFrameBatchTimer()
    {
        if (_frameBatchTimer is null)
        {
            _frameBatchTimer = new DispatcherTimer(DispatcherPriority.Render);
            _frameBatchTimer.Interval = TimeSpan.FromMilliseconds(100);
            _frameBatchTimer.Tick += OnFrameBatchTick;
            _frameBatchTimer.Start();
        }
    }

    private static void OnFrameBatchTick(object? sender, EventArgs e)
    {
        lock (_activeFramePlayers)
        {
            foreach (var p in _activeFramePlayers)
            {
                p.FlushPendingFrame();
            }
        }
    }

    private void FlushPendingFrame()
    {
        if (!_hasPendingFrame) { return; }
        if (_isUnloaded || _camera is null) { _hasPendingFrame = false; return; }
        _hasPendingFrame = false;

        var data = Interlocked.Exchange(ref _pendingFrameData, null);
        if (data is null) { return; }

        try
        {
            var width = _pendingFrameW;
            var height = _pendingFrameH;

            if (_camera is not null)
            {
                if (!_camera.IsConnected)
                {
                    _camera.IsConnected = true;
                    _camera.LastConnectedAt = DateTime.Now;
                    _camera.FirstConnectedAt ??= DateTime.Now;
                    GetEventLog()?.LogInfo("Connection", "VideoPlayer", $"{_camera.Name} 已成功連接 (Decoder)");
                    Notify($"{_camera.Name} 已成功連接 (Decoder)", "INFO");
                }
            }

            if (Placeholder.Visibility == Visibility.Visible)
            {
                Placeholder.Visibility = Visibility.Collapsed;
                DecoderFrameImage.Visibility = Visibility.Visible;
                StatusText.Text = "串流播放中";
            }
            if (ReconnectingOverlay.Visibility == Visibility.Visible)
            {
                ReconnectingOverlay.Visibility = Visibility.Collapsed;
            }

            _decoderVidWidth = (uint)width;
            _decoderVidHeight = (uint)height;
            UpdateHealthBadge();
            if (_decoderBitmap is null || _decoderBitmap.PixelWidth != width || _decoderBitmap.PixelHeight != height)
            {
                _decoderBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                DecoderFrameImage.Source = _decoderBitmap;
            }

            if (_decoderBitmap is not null)
            {
                _decoderBitmap.Lock();
                try
                {
                    Marshal.Copy(data, 0, _decoderBitmap.BackBuffer, Math.Min(data.Length,
                        _decoderBitmap.BackBufferStride * height));
                    _decoderBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                }
                finally
                {
                    _decoderBitmap.Unlock();
                }
            }
        }
        catch when (_isUnloaded) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VideoPlayer:{Name}] Decoder frame render error", _camera?.Name);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private void RegisterActiveFramePlayer()
    {
        lock (_activeFramePlayers)
        {
            if (!_activeFramePlayers.Contains(this))
            {
                _activeFramePlayers.Add(this);
            }
        }
        EnsureFrameBatchTimer();
    }

    private void UnregisterActiveFramePlayer()
    {
        lock (_activeFramePlayers)
        {
            _activeFramePlayers.Remove(this);
        }
    }

    public static void CleanupAllDecoderSessions()
    {
        if (_frameBatchTimer is not null)
        {
            _frameBatchTimer.Stop();
            _frameBatchTimer = null;
        }
        lock (_activeFramePlayers)
        {
            _activeFramePlayers.Clear();
        }
    }

    public static void CleanupAllFlyleafPlayers()
    {
        lock (_flyleafPlayers)
        {
            foreach (var p in _flyleafPlayers)
            {
                try { p.Stop(); } catch { }
                try { p.Dispose(); } catch { }
            }
            _flyleafPlayers.Clear();
        }
    }

    private void PtzUp_Click(object sender, RoutedEventArgs e) { PtzMove(0, 0.5f, 0); }
    private void PtzDown_Click(object sender, RoutedEventArgs e) { PtzMove(0, -0.5f, 0); }
    private void PtzLeft_Click(object sender, RoutedEventArgs e) { PtzMove(-0.5f, 0, 0); }
    private void PtzRight_Click(object sender, RoutedEventArgs e) { PtzMove(0.5f, 0, 0); }
    private void PtzZoomIn_Click(object sender, RoutedEventArgs e) { PtzMove(0, 0, 0.5f); }
    private void PtzZoomOut_Click(object sender, RoutedEventArgs e) { PtzMove(0, 0, -0.5f); }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) { return match; }
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
