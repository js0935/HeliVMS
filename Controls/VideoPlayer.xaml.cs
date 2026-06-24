using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using HeliVMS.Helpers;
using HeliVMS.Models;
using HeliVMS.Services;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HeliVMS.Controls;

public partial class VideoPlayer : UserControl {
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
    private static readonly float[] ZoomLevels = [1.0f, 1.25f, 1.5f, 1.75f, 2.0f];
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
    private readonly DispatcherTimer? _hudTimer;

    // Decoder reconnect state
    private const int MaxDecoderReconnectAttempts = 10;
    private static readonly HashSet<string> _permanentFailures = [];

    // Batch UI frame update — CompositionTarget.Rendering for VSync-aligned per-player frame pacing
    private static bool _renderingSubscribed;
    private static readonly List<VideoPlayer> _activeFramePlayers = [];
    private long _nextRenderTimestamp; // Stopwatch ticks for per-player frame pacing
    private int _renderIntervalTicks;  // Minimum ticks between renders for this player
    private int _renderMissCount;      // Consecutive frames skipped because UI thread was behind
    private bool _hasShownFirstFrame;   // First frame UI transition already done

    // Flyleaf player tracking (for global cleanup on shutdown)
    private static readonly List<Player> _flyleafPlayers = [];
    internal static bool FlyleafEngineReady;
    private System.Windows.UIElement? _flyleafHost;

    // --- Direct3D9 hardware-accelerated rendering (D3DImage) ---
    private D3DRenderer? _d3dRenderer;
    private D3DImageSurface? _d3dImageSurface;
    // Private native buffer: decoder copies into it, UI copies from it — fully decoupled
    private IntPtr _localFrameBuffer = IntPtr.Zero;
    private int _localBufferSize = 0;
    private bool _hasNewFrame = false;
    private int _pendingFrameW, _pendingFrameH, _pendingFrameStride;
    /// <summary>Serialises the critical section: decoder → local copy + UI → bitmap copy.</summary>
    private readonly object _frameCopyLock = new();

    // Fallback WriteableBitmap (pre-allocated once, reused; only when D3D9 unavailable)
    private WriteableBitmap? _decoderBitmap;
    private string? _decoderRtspUrl;

    // In-process FFmpeg streaming service
    private FFmpegStreamingService? _streamingService;
    private int _streamFailCount;

    // Decoder info OSD: FPS / resolution
    private int _osdFrameCount;
    private DateTime _osdLastReset;
    private DispatcherTimer? _osdTimer;

    /// <summary>Target decode height (0 = source height, set before LoadCamera)</summary>
    public int TargetDecodeHeight { get; set; } = 360;
    /// <summary>Target decode FPS (0 = max, set before LoadCamera)</summary>
    public int TargetFps { get; set; } = 15;

    public bool IsMaximized {
        get => _isMaximized;
        set => _isMaximized = value;
    }

    public event Action<Camera?>? PtzSelected;
    public event Action<Camera?>? MaximizeRequested;

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(VideoPlayer),
            new PropertyMetadata(false, OnIsSelectedChanged));

    public bool IsSelected {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public event Action<VideoPlayer>? Selected;

    private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
        var player = (VideoPlayer)d;
        var selected = (bool)e.NewValue;
        if (selected) {
            player.PlayerBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x7A, 0xCC));
            player.GlowEffect.Opacity = 1;
        } else {
            player.PlayerBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2D, 0x2D, 0x30));
            player.GlowEffect.Opacity = 0;
        }
    }

    public void SuspendVideo() {
        DecoderFrameImage.Visibility = Visibility.Collapsed;
        StopStreaming();
    }

    public void ResumeVideo() {
        if (_camera is null) { return; }
        var (url, user, pass) = ResolveStreamUrl(_camera, _useSubStream);
        if (!string.IsNullOrEmpty(url)) {
            StopStreaming();
            StartPlaybackDecoder(url, user, pass, _camera);
        }
    }

    public void SetOverlayName(string name) {
        CameraNameText.Text = name;
        CameraNameTextPlaceholder.Text = name;
        var fontSettings = GetCachedSettings();
        if (fontSettings is not null) {
            CameraNameText.FontSize = fontSettings.Settings.OverlayFontSize;
        }
        UpdateHealthBadge();
    }

    public void UpdateHealthBadge() {
        if (_camera is { DisconnectCount: > 0 }) {
            HealthBadge.Text = $"!!{_camera.DisconnectCount}";
            HealthBadge.Visibility = Visibility.Visible;
        } else {
            HealthBadge.Visibility = Visibility.Collapsed;
        }
    }

    private static IEventService? _cachedEventService;
    private static IEventService? GetEventLog() {
        if (_cachedEventService is null) { try { _cachedEventService = App.Services.GetRequiredService<IEventService>(); } catch { } }
        return _cachedEventService;
    }

    private static INotificationService? _cachedNotificationService;
    private static void Notify(string message, string severity = "WARN") {
        if (_cachedNotificationService is null) { try { _cachedNotificationService = App.Services.GetRequiredService<INotificationService>(); } catch { } }
        _cachedNotificationService?.Show(message, severity);
    }

    private static MediaMTXService? _mtxService;
    private static MediaMTXService? TryGetMediaMTX() {
        if (_mtxService is null) {
            try { _mtxService = App.Services.GetRequiredService<MediaMTXService>(); } catch { }
        }
        return _mtxService;
    }

    private static ISettingsService? _cachedSettings;
    private static ISettingsService? GetCachedSettings() {
        if (_cachedSettings is null) {
            try { _cachedSettings = App.Services.GetRequiredService<ISettingsService>(); } catch { }
        }
        return _cachedSettings;
    }

    /// <summary>Resolve optimal stream URL: MediaMTX relay when available, direct RTSP fallback</summary>
    private static (string url, string user, string pass) ResolveStreamUrl(Camera camera, bool useSubStream) {
        var mtx = TryGetMediaMTX();
        if (mtx is not null && mtx.IsRunning) {
            var relayUrl = MediaMTXService.GetRelayRtspUrl(camera, useSubStream);
            return (relayUrl, "", "");
        }

        var rawUrl = useSubStream ? camera.RtspUrlSub : camera.RtspUrl;
        return (rawUrl ?? "", camera.Username ?? "", camera.Password ?? "");
    }

    public void SetFullBleed() {
        PlayerBorder.BorderThickness = new Thickness(0);
        PlayerBorder.CornerRadius = new CornerRadius(0);
    }

    public VideoPlayer() {
        InitializeComponent();
        if (FlyleafEngineReady) {
            _flyleafHost = CreateFlyleafHost();
        }
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        this.LostMouseCapture += VideoPlayer_LostMouseCapture;

        _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hudTimer.Tick += HudTimer_Tick;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static UIElement CreateFlyleafHost() {
        var host = new FlyleafLib.Controls.WPF.FlyleafHost { Visibility = Visibility.Collapsed };
        Panel.SetZIndex(host, 1);
        return host;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AttachFlyleafPlayer(UIElement host, Player player) {
        var fh = (FlyleafLib.Controls.WPF.FlyleafHost)host;
        fh.Player = player;
        fh.IsHitTestVisible = false;
        fh.Visibility = Visibility.Visible;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ResetFlyleafHost(UIElement host) {
        var fh = (FlyleafLib.Controls.WPF.FlyleafHost)host;
        fh.Visibility = Visibility.Collapsed;
        fh.Player = null;
    }

    private void VideoPlayer_LostMouseCapture(object? sender, MouseEventArgs e) {
        if (_isPanning) {
            _isPanning = false;
            Log.Debug("[HeliVMS] Pan ABORT (lost capture): final=({PanX},{PanY})", _panX, _panY);
            if (Mouse.OverrideCursor == Cursors.Hand) {
                Mouse.OverrideCursor = null;
            }
        }
    }

    private bool _overlayActive;

    private void ShowOverlay() {
        if (_overlayActive) return;
        _overlayActive = true;
        OverlayGrid.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        OverlayGrid.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void HideOverlay() {
        if (!_overlayActive) return;
        _overlayActive = false;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)) {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => {
            if (!_overlayActive) OverlayGrid.Visibility = Visibility.Collapsed;
        };
        OverlayGrid.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void RootGrid_MouseEnter(object sender, MouseEventArgs e) {
        ShowOverlay();
    }

    private void RootGrid_MouseLeave(object sender, MouseEventArgs e) {
        HideOverlay();
    }

    private void SnapshotBtn_Click(object sender, RoutedEventArgs e) {
        CtxSnapshot_Click(sender, e);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) {
        var parent = Parent as FrameworkElement;
        while (parent is not null and not DynamicCameraGrid) {
            parent = parent.Parent as FrameworkElement;
        }
        if (parent is DynamicCameraGrid grid && Camera is not null) {
            var idx = Array.IndexOf(grid.GetSlotCameras(), Camera);
            if (idx >= 0) grid.RemoveSlot(idx);
        }
    }

    private void HudTimer_Tick(object? sender, EventArgs e) { HideZoomHud(); }

    private void OnLoaded(object sender, RoutedEventArgs e) {
        _isUnloaded = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) {
        _isUnloaded = true;
        _hudTimer?.Stop();
        CancelTimers();
        ResetReconnectState();
    }

    public void LoadCamera(Camera camera, bool useSubStream = false) {
        DragDiag.Write($"[VideoPlayer] LoadCamera: {camera.Name}({camera.Id}), useSub={useSubStream}, rtsp={camera.RtspUrl}, rtspSub={camera.RtspUrlSub}");
        Log.Debug("[VideoPlayer] LoadCamera: {Name}({Id}), useSub={Sub}, rtsp={Rtsp}, rtspSub={RtspSub}", camera.Name, camera.Id, useSubStream, camera.RtspUrl, camera.RtspUrlSub);
        _isUnloaded = false;
        _camera = camera;
        _useSubStream = useSubStream;
        _onvif = App.Services.GetService<IOnvifService>();
        CameraNameText.Text = camera.Name;
        CameraNameTextPlaceholder.Text = camera.Name;

        var fontSettings = _cachedSettings;
        if (fontSettings is not null) {
            CameraNameText.FontSize = fontSettings.Settings.OverlayFontSize;
        }

        var hasSub = !string.IsNullOrEmpty(camera.RtspUrlSub);
        StreamToggle.Visibility = hasSub ? Visibility.Visible : Visibility.Collapsed;
        UpdateStreamToggleText();

        var (streamUrl, streamUser, streamPass) = ResolveStreamUrl(camera, useSubStream);
        DragDiag.Write($"[VideoPlayer] LoadCamera: resolved url=|{streamUrl}|, user=|{streamUser}|, isEnabled={camera.IsEnabled}");
        Log.Debug("[VideoPlayer] LoadCamera: resolved url={Url}, user={User}, passLen={PassLen}", streamUrl, streamUser, streamPass?.Length ?? 0);
        if (camera.IsEnabled && !string.IsNullOrEmpty(streamUrl)) {
            StatusText.Text = "連線中...";
            DecoderFrameImage.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
            StartPlayback(streamUrl, streamUser, streamPass ?? "", camera);
        } else {
            StatusText.Text = "未連線";
            DecoderFrameImage.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
        }

        PtzOverlay.Visibility = camera.HasPTZ ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartPlayback(string rtspUrl, string username, string password, Camera camera) {
        DragDiag.Write($"[VideoPlayer:{camera.Name}] StartPlayback: url={rtspUrl}");
        Log.Debug("[VideoPlayer:{Name}] StartPlayback: url={Url}", camera.Name, rtspUrl);
        if (TryStartFlyleaf(rtspUrl, username, password)) {
            DragDiag.Write($"[VideoPlayer:{camera.Name}] StartPlayback: using Flyleaf");
            Log.Debug("[VideoPlayer:{Name}] StartPlayback: using Flyleaf", camera.Name);
            return;
        }
        DragDiag.Write($"[VideoPlayer:{camera.Name}] StartPlayback: Flyleaf not available, falling back to FFmpeg decoder");
        Log.Debug("[VideoPlayer:{Name}] StartPlayback: Flyleaf not available, falling back to FFmpeg decoder", camera.Name);
        StartPlaybackDecoder(rtspUrl, username, password, camera);
    }

    private bool TryStartFlyleaf(string rtspUrl, string username, string password) {
        if (!FlyleafEngineReady) { return false; }
        try {
            SaveZoomForRestore();
            ResetZoomState();

            StopStreaming();

            var url = rtspUrl;
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)) {
                var uri = new Uri(rtspUrl);
                var userInfo = $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}";
                url = $"rtsp://{userInfo}@{uri.Host}:{uri.Port}{uri.PathAndQuery}";
            }

            DecoderFrameImage.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Collapsed;
            ReconnectingOverlay.Visibility = Visibility.Collapsed;
            StatusText.Text = "連線中...";

            // Ensure FlyleafHost is in the visual tree (created in constructor, not yet parented)
            if (_flyleafHost is not null && System.Windows.Media.VisualTreeHelper.GetParent(_flyleafHost) is null) {
                RootGrid.Children.Add(_flyleafHost);
            }

            var enableHw = GetCachedSettings()?.Settings.EnableHardwareAcceleration ?? true;
            _flyleafConfig = new Config();
            _flyleafConfig.Video.ClearScreen = true;
            _flyleafConfig.Video.VideoAcceleration = enableHw;
            _flyleafConfig.Decoder.LowDelay = true;
            _flyleafConfig.Decoder.AllowDropFrames = true;
            _flyleafConfig.Player.ZeroLatency = true;

            _flyleafPlayer = new Player(_flyleafConfig);
            if (_flyleafHost is not null) {
                AttachFlyleafPlayer(_flyleafHost, _flyleafPlayer);
            }
            StartInfoOSD();

            var cp = _flyleafPlayer;
            _flyleafPlayer.OpenCompleted += (_, _) => {
                if (_flyleafPlayer != cp) return;
                _ = Dispatcher.BeginInvoke(OnFlyleafOpenCompletedCore);
            };
            _flyleafPlayer.PlaybackStopped += (_, _) => {
                if (_flyleafPlayer != cp) return;
                _ = Dispatcher.BeginInvoke(OnFlyleafPlaybackStoppedCore);
            };

            lock (_flyleafPlayers) { _flyleafPlayers.Add(_flyleafPlayer); }

            _flyleafPlayer.OpenAsync(url);
            return true;
        } catch (Exception ex) {
            Log.Warning(ex, "[VideoPlayer:{Name}] Flyleaf start failed", _camera?.Name);
            CleanupFlyleaf();
            return false;
        }
    }

    private void OnFlyleafOpenCompletedCore() {
        if (_isUnloaded || _camera is null) { return; }
        StatusText.Text = "串流播放中";
        Placeholder.Visibility = Visibility.Collapsed;
        ReconnectingOverlay.Visibility = Visibility.Collapsed;

        if (!_camera.IsConnected) {
            _camera.IsConnected = true;
            _camera.LastConnectedAt = DateTime.Now;
            _camera.FirstConnectedAt ??= DateTime.Now;
            UpdateHealthBadge();
            GetEventLog()?.LogInfo("Connection", "VideoPlayer", $"{_camera.Name} 已成功連接 (Flyleaf)");
            Notify($"{_camera.Name} 已成功連接 (Flyleaf)", "INFO");
        }
    }

    private void OnFlyleafPlaybackStoppedCore() {
        if (_isUnloaded || _camera is null) { return; }

        var wasConnected = _camera.IsConnected;
        _camera.IsConnected = false;
        _camera.LastDisconnectedAt = DateTime.Now;
        _camera.DisconnectCount++;
        UpdateHealthBadge();

        if (wasConnected) {
            GetEventLog()?.LogWarning("Connection", "VideoPlayer", $"{_camera.Name} 連線中斷");
            Notify($"{_camera.Name} 連線中斷", "WARN");
        }

        if (_flyleafPlayer is not null) {
            StatusText.Text = "連線中斷，重新連線中...";
            ReconnectingOverlay.Visibility = Visibility.Visible;
        }
    }

    private void CleanupFlyleaf() {
        if (_flyleafHost is not null) { ResetFlyleafHost(_flyleafHost); }

        if (_flyleafPlayer is not null) {
            lock (_flyleafPlayers) { _flyleafPlayers.Remove(_flyleafPlayer); }
            try { _flyleafPlayer.Stop(); } catch (Exception ex) { Log.Debug("[HeliVMS] Flyleaf Stop: {Msg}", ex.Message); }
            try { _flyleafPlayer.Dispose(); } catch { }
            _flyleafPlayer = null;
            _flyleafConfig = null;
        }
    }

    private string? SaveSnapshotFlyleaf(string directory) {
        if (_flyleafPlayer is null) return null;
        try {
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
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] SaveSnapshotFlyleaf error: {Msg}", ex.Message);
            return null;
        }
    }

    private void StartPlaybackDecoder(string rtspUrl, string username, string password, Camera camera) {
        DragDiag.Write($"[VideoPlayer:{camera.Name}] StartPlaybackDecoder ENTER: url={rtspUrl}");
        SaveZoomForRestore();
        ResetZoomState();

        if (_permanentFailures.Contains(camera.Id)) {
            StatusText.Text = "此攝影機URL已永久失效";
            return;
        }

        StopStreaming();

        _decoderRtspUrl = rtspUrl;
        _streamFailCount = 0;
        DecoderFrameImage.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Collapsed;
        ReconnectingOverlay.Visibility = Visibility.Collapsed;

        var hasFrozenFrame = _decoderBitmap is not null;
        if (hasFrozenFrame) {
            DecoderFrameImage.Visibility = Visibility.Visible;
            ReconnectingOverlay.Visibility = Visibility.Visible;
            ReconnectAttemptText.Text = $"重連 {_streamFailCount}/{MaxDecoderReconnectAttempts} 次";
        } else {
            Placeholder.Visibility = Visibility.Visible;
        }
        StatusText.Text = "連線中...";

        var enableHw = GetCachedSettings()?.Settings.EnableHardwareAcceleration ?? true;
        _streamingService = new FFmpegStreamingService {
            HwDeviceType = enableHw ? AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA : AVHWDeviceType.AV_HWDEVICE_TYPE_NONE,
            UseAsyncRtsp = (GetCachedSettings()?.Settings.UseAsyncRtspLiveView).GetValueOrDefault()
        };

        // Adjust decode height for target FPS
        if (TargetDecodeHeight > 0)
            _streamingService.TargetDecodeHeight = TargetDecodeHeight;
        if (TargetFps > 0) {
            _streamingService.TargetFps = TargetFps;
        }
        _streamingService.FrameReady += OnDecoderFrameReady;
        _streamingService.PlayStatus += OnPlayStatusChanged;
        _streamingService.StreamStuck += OnStreamStuck;
        RegisterActiveFramePlayer();
        _streamingService.Play(rtspUrl, username, password);
        DragDiag.Write($"[VideoPlayer:{camera.Name}] StartPlaybackDecoder: Play() issued");
        StartInfoOSD();
    }

    private void OnPlayStatusChanged(bool isPlaying) {
        if (_isUnloaded) return;
        DragDiag.Write($"[VideoPlayer:{_camera?.Name}] OnPlayStatusChanged: isPlaying={isPlaying}");
        _ = Dispatcher.BeginInvoke(() => {
            if (_isUnloaded || _camera is null) { return; }

            if (isPlaying) {
                _streamFailCount = 0;
                StatusText.Text = "串流播放中";
                ReconnectingOverlay.Visibility = Visibility.Collapsed;
                _camera.IsConnected = true;
                _camera.LastConnectedAt = DateTime.Now;
                _camera.FirstConnectedAt ??= DateTime.Now;
                UpdateHealthBadge();
            } else {
                _streamFailCount++;
                if (_streamFailCount >= MaxDecoderReconnectAttempts) {
                    Log.Warning("[VideoPlayer:{Name}] Max stream reconnect attempts reached, stopping", _camera?.Name);
                    StatusText.Text = "已達最大重連次數，停止";
                    StopStreaming();
                    return;
                }

                StatusText.Text = $"正在重新連線 ({_streamFailCount}/{MaxDecoderReconnectAttempts})";
                ReconnectAttemptText.Text = $"重連 {_streamFailCount}/{MaxDecoderReconnectAttempts} 次";
                if (DecoderFrameImage.Visibility == Visibility.Visible) {
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

    private void OnStreamStuck() {
        if (_isUnloaded) return;
        DragDiag.Write($"[VideoPlayer:{_camera?.Name}] OnStreamStuck");
        _ = Dispatcher.BeginInvoke(() => {
            Log.Warning("[VideoPlayer:{Name}] Stream stuck, restarting service", _camera?.Name);
            StatusText.Text = "串流卡住，重新啟動中...";
            StopStreaming();
            if (!_isUnloaded && _camera is not null && _decoderRtspUrl is not null) {
                StartPlaybackDecoder(_decoderRtspUrl, "", "", _camera);
            }
        });
    }

    private unsafe void OnDecoderFrameReady(IntPtr frameData, int width, int height, int dataSize) {
        if (_isUnloaded || _camera is null) { return; }

        Interlocked.Increment(ref _osdFrameCount);

        // Copy decoder data into the private local buffer under the lock,
        // then release the ring-buffer slot straight away — UI never touches it.
        lock (_frameCopyLock) {
            // (Re)allocate local buffer if needed
            if (_localBufferSize < dataSize) {
                if (_localFrameBuffer != IntPtr.Zero) {
                    Marshal.FreeHGlobal(_localFrameBuffer);
                }
                _localFrameBuffer = Marshal.AllocHGlobal(dataSize);
                _localBufferSize = dataSize;
            }

            Buffer.MemoryCopy((void*)frameData, (void*)_localFrameBuffer, dataSize, dataSize);

            _pendingFrameW = width;
            _pendingFrameH = height;
            _pendingFrameStride = width * 4;
            _hasNewFrame = true;

            // Slot data is now safely copied; return it to the ring buffer immediately.
            _streamingService?.ReleaseFrame(frameData);
        }
    }

    private void StopStreaming() {
        _hasShownFirstFrame = false;
        CleanupFlyleaf();
        UnregisterActiveFramePlayer();
        StopInfoOSD();
        if (_streamingService is not null) {
            _streamingService.FrameReady -= OnDecoderFrameReady;
            _streamingService.PlayStatus -= OnPlayStatusChanged;
            _streamingService.StreamStuck -= OnStreamStuck;
            _streamingService.Stop();
            _streamingService.Dispose();
            _streamingService = null;
        }
    }

    private static void SaveZoomForRestore() {
        // Zoom state is preserved across stream toggles in maximized mode
    }

    private void ResetZoomState() {
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

    private void CancelTimers() {
        _osdTimer?.Stop();
        _osdTimer = null;
    }

    private void StartInfoOSD() {
        InfoOSD.Visibility = Visibility.Visible;
        _osdFrameCount = 0;
        _osdLastReset = DateTime.UtcNow;
        _osdTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _osdTimer.Tick -= OsdTimer_Tick;
        _osdTimer.Tick += OsdTimer_Tick;
        _osdTimer.Start();
    }

    private void StopInfoOSD() {
        InfoOSD.Visibility = Visibility.Collapsed;
        _osdTimer?.Stop();
    }

    private string? _osdCameraPrefix;
    private void OsdTimer_Tick(object? sender, EventArgs e) {
        var prefix = _osdCameraPrefix;
        var camName = _camera?.Name;
        if (prefix is null || !prefix.StartsWith(camName ?? "", StringComparison.Ordinal)) {
            _osdCameraPrefix = $"{camName ?? ""}  ";
            prefix = _osdCameraPrefix;
        }
        if (_flyleafPlayer is { Video.IsOpened: true }) {
            InfoOSText.Text = string.Concat(prefix, "Flyleaf HW  ", _flyleafPlayer.Video.FPSCurrent.ToString("F1"), " FPS");
            return;
        }
        var count = Interlocked.Exchange(ref _osdFrameCount, 0);
        var elapsed = DateTime.UtcNow - _osdLastReset;
        _osdLastReset = DateTime.UtcNow;
        if (elapsed.TotalSeconds > 0) {
            InfoOSText.Text = string.Concat(prefix, _decoderVidWidth.ToString(), "\u00D7", _decoderVidHeight.ToString(), "  ", (count / elapsed.TotalSeconds).ToString("F1"), " FPS");
        }
    }

    private void UpdateStreamToggleText() {
        StreamToggleText.Text = _useSubStream ? "SD" : "HD";
        StreamToggle.ToolTip = _useSubStream
            ? "切換至主串流 (HD)"
            : "切換至子串流 (SD)";
    }

    private void ToggleStream() {
        if (_camera is null) { return; }
        _useSubStream = !_useSubStream;
        var (url, user, pass) = ResolveStreamUrl(_camera, _useSubStream);
        if (string.IsNullOrEmpty(url)) {
            _useSubStream = !_useSubStream;
            return;
        }
        UpdateStreamToggleText();
        StopStreaming();
        StartPlayback(url, user, pass, _camera);
    }

    private void StreamToggle_Click(object sender, RoutedEventArgs e) {
        ToggleStream();
    }

    public void DetachCamera() {
        _isUnloaded = true;
        UnregisterActiveFramePlayer();
        DecoderFrameImage.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        _hudTimer?.Stop();
        CancelTimers();
        ResetReconnectState();
        if (Mouse.OverrideCursor == Cursors.Hand) {
            Mouse.OverrideCursor = null;
        }
    }

    public void UnloadCamera() {
        _isUnloaded = true;
        StopStreaming();
        DecoderFrameImage.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        _hudTimer?.Stop();
        CancelTimers();
        ResetReconnectState();
        if (Mouse.OverrideCursor == Cursors.Hand) {
            Mouse.OverrideCursor = null;
        }
    }

    // ==============================================================================
    //  Digital zoom / pan mouse event handlers
    // ==============================================================================

    private void RootGrid_MouseWheel(object sender, MouseWheelEventArgs e) {
        if (!_isMaximized) { return; }
        if (_decoderVidWidth == 0 || _decoderVidHeight == 0) { return; }

        var direction = e.Delta > 0 ? 1 : -1;
        var newIdx = Math.Clamp(_zoomLevelIndex + direction, 0, ZoomLevels.Length - 1);
        if (newIdx == _zoomLevelIndex) { return; }

        var oldZoom = _digitalZoom;
        _zoomLevelIndex = newIdx;
        _digitalZoom = ZoomLevels[_zoomLevelIndex];

        if (_decoderVidWidth > 0 && _decoderVidHeight > 0) {
            var cursorPos = e.GetPosition(DecoderFrameImage);
            var w = DecoderFrameImage.ActualWidth;
            var h = DecoderFrameImage.ActualHeight;
            if (w > 0 && h > 0) {
                var fracX = Math.Clamp(cursorPos.X / w, 0, 1);
                var fracY = Math.Clamp(cursorPos.Y / h, 0, 1);

                var (oldCropW, oldCropH, _, _) = ComputeCropBounds(oldZoom);
                var (newCropW, newCropH, maxX, maxY) = ComputeCropBounds(_digitalZoom);

                var cursorVidPxX = _panX + (int)(fracX * oldCropW);
                var cursorVidPxY = _panY + (int)(fracY * oldCropH);
                _panX = Math.Clamp(cursorVidPxX - (int)(fracX * newCropW), 0, maxX);
                _panY = Math.Clamp(cursorVidPxY - (int)(fracY * newCropH), 0, maxY);
            }
        }

        ApplyDecoderZoom();
        ShowZoomHud();
        e.Handled = true;
    }

    private void RootGrid_MouseDown(object sender, MouseButtonEventArgs e) {
        if (e.ChangedButton == MouseButton.Middle && _isZoomed) {
            ResetDigitalZoom();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2) {
            if (_isZoomed) {
                ResetDigitalZoom();
                e.Handled = true;
                return;
            }
            MaximizeRequested?.Invoke(_camera);
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) { return; }

        // Single-click: select this player
        if (e.ClickCount == 1) {
            IsSelected = true;
            Selected?.Invoke(this);
        }

        if (_isZoomed) {
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

        if (_camera is not null) {
            var el = this as DependencyObject;
            while (el is not null && el is not Controls.CameraGrid) {
                el = VisualTreeHelper.GetParent(el);
            }
            if (el is Controls.CameraGrid cameraGrid) {
                cameraGrid.TryStartDragFromPlayer(this);
            }
        }
    }

    private void RootGrid_MouseMove(object sender, MouseEventArgs e) {
        if (!_isPanning) { return; }

        var now = DateTime.UtcNow;
        if ((now - _lastPanUpdate).TotalMilliseconds < 33) { return; }
        _lastPanUpdate = now;

        ScheduleHudAutoHide();

        if (_decoderVidWidth == 0 || _decoderVidHeight == 0 || ActualWidth <= 0 || ActualHeight <= 0) {
            return;
        }

        var pt = e.GetPosition(this);
        var dx = pt.X - _panStartScreen.X;
        var dy = pt.Y - _panStartScreen.Y;

        var maxDelta = Math.Min(ActualWidth, ActualHeight) * 0.15;
        dx = Math.Clamp(dx, -maxDelta, maxDelta);
        dy = Math.Clamp(dy, -maxDelta, maxDelta);

        var (cropW, cropH, _, _) = ComputeCropBounds(_digitalZoom);
        var scaleX = (double)cropW / ActualWidth;
        var scaleY = (double)cropH / ActualHeight;

        var rawX = _panBaseX - dx * scaleX + _panRemainderX;
        var rawY = _panBaseY - dy * scaleY + _panRemainderY;
        var newX = (int)Math.Round(rawX, MidpointRounding.AwayFromZero);
        var newY = (int)Math.Round(rawY, MidpointRounding.AwayFromZero);
        _panRemainderX = rawX - newX;
        _panRemainderY = rawY - newY;

        if (newX != _panX || newY != _panY) {
            _panX = newX;
            _panY = newY;
            ApplyDecoderZoom();
        }
        e.Handled = true;
    }

    private void RootGrid_MouseUp(object sender, MouseButtonEventArgs e) {
        if (!_isPanning) { return; }
        _isPanning = false;
        if (Mouse.OverrideCursor == Cursors.Hand) {
            Mouse.OverrideCursor = null;
        }
        Mouse.Capture((IInputElement)sender, CaptureMode.None);
        e.Handled = true;
    }

    private void RootGrid_MouseRightButtonUp(object sender, MouseButtonEventArgs e) {
        if (_isPanning) {
            _isPanning = false;
            if (Mouse.OverrideCursor == Cursors.Hand) {
                Mouse.OverrideCursor = null;
            }
            Mouse.Capture((IInputElement)sender, CaptureMode.None);
        }
        // Update recording context menu header before showing
        if (_camera is not null) {
            var recording = App.Services.GetRequiredService<IRecordingService>();
            CtxRecordingToggle.Header = recording.IsRecording(_camera.Id) ? "停止錄影" : "開始錄影";
        }
        ContextMenu?.IsOpen = true;
    }

    private void ApplyDecoderZoom() {
        if (_digitalZoom <= (1.0f + ZoomEpsilon)) {
            DecoderZoomScale.ScaleX = 1;
            DecoderZoomScale.ScaleY = 1;
            DecoderZoomTranslate.X = 0;
            DecoderZoomTranslate.Y = 0;
            _isZoomed = false;
            return;
        }

        if (_decoderVidWidth == 0 || _decoderVidHeight == 0) { return; }

        var (_, _, maxX, maxY) = ComputeCropBounds(_digitalZoom);
        _panX = Math.Clamp(_panX, 0, maxX);
        _panY = Math.Clamp(_panY, 0, maxY);

        double scale = _digitalZoom;
        DecoderZoomScale.ScaleX = scale;
        DecoderZoomScale.ScaleY = scale;

        var viewW = _decoderVidWidth / scale;
        var viewH = _decoderVidHeight / scale;
        var tx = -(_panX + viewW / 2 - _decoderVidWidth / 2);
        var ty = -(_panY + viewH / 2 - _decoderVidHeight / 2);
        DecoderZoomTranslate.X = tx;
        DecoderZoomTranslate.Y = ty;

        _isZoomed = true;
    }

    private (uint cropW, uint cropH, int maxX, int maxY) ComputeCropBounds(float zoom) {
        var w = _decoderVidWidth;
        var h = _decoderVidHeight;
        if (w == 0 || h == 0) { return (1, 1, 0, 0); }
        var cropW = Math.Max(1u, (uint)(w / (double)zoom));
        var cropH = Math.Max(1u, (uint)(h / (double)zoom));
        var maxX = Math.Max(0, (int)(w - cropW));
        var maxY = Math.Max(0, (int)(h - cropH));
        return (cropW, cropH, maxX, maxY);
    }

    private void UpdateZoomHud() {
        var text = "";
        if (_isZoomed) {
            if (_decoderVidWidth > 0 && _decoderVidHeight > 0) {
                var (_, _, maxX, maxY) = ComputeCropBounds(_digitalZoom);
                var edge = "";
                if (_panX <= 0) { edge += "左"; } else if (_panX >= maxX) edge += "右";
                if (_panY <= 0) { edge += "上"; } else if (_panY >= maxY) edge += "下";
                text = $"{(int)(_digitalZoom * 100)}% {edge}".Trim();
            }
        }
        DecoderZoomHud.Text = text;
    }

    private void ShowZoomHud() {
        UpdateZoomHud();
        DecoderZoomHud.Visibility = Visibility.Visible;
        ScheduleHudAutoHide();
    }

    private void HideZoomHud() {
        DecoderZoomHud.Visibility = Visibility.Collapsed;
    }

    private void ScheduleHudAutoHide() {
        _hudTimer?.Stop();
        _hudTimer?.Start();
    }

    private void ResetDigitalZoom() {
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
        if (Mouse.OverrideCursor == Cursors.Hand) {
            Mouse.OverrideCursor = null;
        }
        HideZoomHud();
    }

    private static void ResetReconnectState() {
    }

    private void CtxFullscreen_Click(object sender, RoutedEventArgs e) {
        var parent = Parent as FrameworkElement;
        while (parent is not null and not Views.LiveView) {
            parent = parent.Parent as FrameworkElement;
        }
        if (parent is Views.LiveView liveView) {
            liveView.ToggleFullScreen();
        }
    }

    private void CtxStreamToggle_Click(object sender, RoutedEventArgs e) {
        ToggleStream();
    }

    private void CtxPtzSelect_Click(object sender, RoutedEventArgs e) {
        PtzSelected?.Invoke(_camera);
    }

    private void CtxBookmark_Click(object sender, RoutedEventArgs e) {
        if (_camera is null) return;
        var now = DateTime.Now;
        var bm = new PlaybackBookmark {
            Seconds = now.TimeOfDay.TotalSeconds,
            Note = $"{_camera.Name} @ {now:HH:mm:ss}"
        };
        var bookmarks = App.Services.GetRequiredService<IBookmarkService>();
        bookmarks.SaveBookmark(bm, now.Date);
    }

    private void CtxRecordingToggle_Click(object sender, RoutedEventArgs e) {
        if (_camera is null) return;
        var recording = App.Services.GetRequiredService<IRecordingService>();
        if (recording.IsRecording(_camera.Id)) {
            recording.StopRecording(_camera.Id);
            CtxRecordingToggle.Header = "開始錄影";
        } else {
            recording.StartRecording(_camera);
            CtxRecordingToggle.Header = "停止錄影";
        }
    }

    public string? SaveSnapshot(string directory) {
        // Try Flyleaf snapshot first (hardware-accelerated)
        if (_flyleafPlayer is not null) {
            return SaveSnapshotFlyleaf(directory);
        }

        // Fallback to decoder WriteableBitmap snapshot
        if (_decoderBitmap is null) return null;

        try {
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
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] SaveSnapshot error: {Msg}", ex.Message);
            return null;
        }
    }

    private void CtxSnapshot_Click(object sender, RoutedEventArgs e) {
        var cameraName = _camera?.Name ?? "Unknown";
        var channel = _camera?.ChannelNumber ?? 0;
        var safeName = SanitizeFileName(cameraName);
        var dialog = new Microsoft.Win32.SaveFileDialog {
            Title = "儲存快照",
            Filter = "PNG 圖片 (*.png)|*.png|JPEG 圖片 (*.jpg)|*.jpg",
            FileName = $"CH{channel:D2}_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var path = SaveSnapshot(Path.GetDirectoryName(dialog.FileName)!);
        if (path is not null) {
            try { File.Move(path, dialog.FileName, overwrite: true); } catch { }
            MessageBox.Show($"快照已儲存至\n{dialog.FileName}", "快照",
                MessageBoxButton.OK, MessageBoxImage.Information);
        } else {
            MessageBox.Show("快照失敗：無法儲存快照", "快照",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string SanitizeFileName(string name) {
        var invalid = Path.GetInvalidFileNameChars();
        for (var i = 0; i < name.Length; i++) {
            var c = name[i];
            for (var j = 0; j < invalid.Length; j++) {
                if (c == invalid[j]) {
                    var chars = name.ToCharArray();
                    chars[i] = '_';
                    for (var k = i + 1; k < chars.Length; k++) {
                        var ck = chars[k];
                        for (var j2 = 0; j2 < invalid.Length; j2++) {
                            if (ck == invalid[j2]) { chars[k] = '_'; break; }
                        }
                    }
                    return new string(chars);
                }
            }
        }
        return name;
    }

    private static readonly Color ColorContinuous = Color.FromRgb(0x21, 0x96, 0xF3);
    private static readonly Color ColorMotion = Color.FromRgb(0x4C, 0xAF, 0x50);
    private static readonly Color ColorAlarm = Color.FromRgb(0xF4, 0x43, 0x36);
    private static readonly Color ColorSmart = Color.FromRgb(0xE9, 0x1E, 0x63);
    private static readonly Color ColorWeighted = Color.FromRgb(0xFF, 0x98, 0x00);
    private static readonly Color ColorNone = Colors.Transparent;

    public void SwitchToMainStream() {
        if (_camera is null) { return; }
        if (!_useSubStream) { return; }
        _useSubStream = false;
        var (url, user, pass) = ResolveStreamUrl(_camera, false);
        if (!string.IsNullOrEmpty(url)) {
            UpdateStreamToggleText();
            StartPlayback(url, user, pass, _camera);
        }
    }

    public void SwitchToSubStream() {
        if (_camera is null) { return; }
        if (_useSubStream) { return; }
        _useSubStream = true;
        var (url, user, pass) = ResolveStreamUrl(_camera, true);
        if (!string.IsNullOrEmpty(url)) {
            UpdateStreamToggleText();
            StartPlayback(url, user, pass, _camera);
        }
    }

    private DateTime? _pendingSeekTime;

    /// <summary>Pause live RTSP decoding and seek to a historical timestamp.</summary>
    public void SwitchToPlayback(DateTime targetTime) {
        _pendingSeekTime = targetTime;
        StopStreaming();
        StatusText.Text = targetTime.ToString("HH:mm:ss");
    }

    /// <summary>Return to live RTSP stream.</summary>
    public void SwitchToLive() {
        _pendingSeekTime = null;
        if (_camera is null) return;
        var (url, user, pass) = ResolveStreamUrl(_camera, _useSubStream);
        if (!string.IsNullOrEmpty(url))
            StartPlayback(url, user, pass, _camera);
    }

    public void SetRecordingMode(ScheduleMode mode) {
        var color = mode switch {
            ScheduleMode.Continuous => ColorContinuous,
            ScheduleMode.Motion => ColorMotion,
            ScheduleMode.Alarm => ColorAlarm,
            ScheduleMode.Smart => ColorSmart,
            ScheduleMode.Weighted => ColorWeighted,
            _ => ColorNone,
        };
        SetRecordingModeColor(color);
    }

    public void SetRecordingModeColor(Color color) {
        RecordingModeDot.Fill = new SolidColorBrush(color);
        RecordingModeDot.Visibility = color == Colors.Transparent
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void SetRecordingIndicator(bool recording) {
        RecIndicator.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
    }

    public void UpdateRecordingElapsed(TimeSpan elapsed) {
        if (elapsed.TotalSeconds > 0) {
            RecTimeText.Visibility = Visibility.Visible;
            RecTimeText.Text = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"h\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
        } else {
            RecTimeText.Visibility = Visibility.Collapsed;
        }
    }

    private void PtzMove(float x, float y, float zoom) {
        if (_camera is null || _onvif is null) { return; }
        _ = _onvif.PTZ_ContinuousMoveAsync(
            _camera.IpAddress ?? "", _camera.OnvifPort,
            _camera.Username ?? "", _camera.Password ?? "",
            x, y, zoom);
    }

    private void PtzStop() {
        if (_camera is null || _onvif is null) { return; }
        _ = _onvif.PTZ_StopAsync(
            _camera.IpAddress ?? "", _camera.OnvifPort,
            _camera.Username ?? "", _camera.Password ?? "");
    }

    private static void EnsureFrameBatchTimer() {
        if (!_renderingSubscribed) {
            CompositionTarget.Rendering += OnCompositionRendering;
            _renderingSubscribed = true;
        }
    }

    private static void OnCompositionRendering(object? sender, EventArgs e) {
        var now = Stopwatch.GetTimestamp();
        VideoPlayer[]? snapshot;
        int count;
        lock (_activeFramePlayers) {
            if (_activeFramePlayers.Count == 0) return;
            var pool = ArrayPool<VideoPlayer>.Shared;
            snapshot = pool.Rent(_activeFramePlayers.Count);
            _activeFramePlayers.CopyTo(snapshot, 0);
            count = _activeFramePlayers.Count;
        }
        try {
            var minInterval = (int)(Stopwatch.Frequency / 30);
            for (var i = 0; i < count; i++) {
                var p = snapshot![i];
                if (p._nextRenderTimestamp <= now) {
                    if (!p._hasNewFrame) {
                        p._renderMissCount++;
                        if (p._renderMissCount > 30) {
                            // Back off render rate for idle players (reduce CPU)
                            p._renderIntervalTicks = Math.Min(p._renderIntervalTicks * 2, (int)(Stopwatch.Frequency / 5));
                            p._renderMissCount = 0;
                        }
                        p._nextRenderTimestamp = now + Math.Max(p._renderIntervalTicks, minInterval);
                        continue;
                    }
                    p._renderMissCount = 0;
                    p.FlushPendingFrame();
                    p._nextRenderTimestamp = now + Math.Max(p._renderIntervalTicks, minInterval);
                }
            }
        } finally {
            ArrayPool<VideoPlayer>.Shared.Return(snapshot);
        }
    }

    private unsafe void FlushPendingFrame() {
        if (_isUnloaded || _camera is null) { return; }

        lock (_frameCopyLock) {
            if (!_hasNewFrame || _localFrameBuffer == IntPtr.Zero) { return; }
            _hasNewFrame = false;

            var width = _pendingFrameW;
            var height = _pendingFrameH;
            var stride = _pendingFrameStride;

            if (!_camera.IsConnected) {
                _camera.IsConnected = true;
                _camera.LastConnectedAt = DateTime.Now;
                _camera.FirstConnectedAt ??= DateTime.Now;
                GetEventLog()?.LogInfo("Connection", "VideoPlayer", $"{_camera.Name} 已成功連接 (Decoder)");
                Notify($"{_camera.Name} 已成功連接 (Decoder)", "INFO");
            }

            if (!_hasShownFirstFrame) {
                _hasShownFirstFrame = true;
                Placeholder.Visibility = Visibility.Collapsed;
                DecoderFrameImage.Visibility = Visibility.Visible;
                StatusText.Text = "串流播放中";
                ReconnectingOverlay.Visibility = Visibility.Collapsed;
                DragDiag.Write($"[VideoPlayer:{_camera.Name}] FlushPendingFrame: FIRST RENDER {width}x{height}");
            }

            _decoderVidWidth = (uint)width;
            _decoderVidHeight = (uint)height;
            UpdateHealthBadge();

            try {
                // --- D3D9 path (GPU surface). Falls back to CPU if surface allocation fails. ---
                if (_d3dImageSurface is not null && _d3dImageSurface.EnsureSize(width, height)) {
                    _d3dImageSurface.PresentFrame(_localFrameBuffer, stride * height, stride);
                } else {
                    // --- Fallback: pre-allocated WriteableBitmap (D3D unavailable) ---
                    if (_decoderBitmap is null ||
                        _decoderBitmap.PixelWidth != width ||
                        _decoderBitmap.PixelHeight != height) {
                        _decoderBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                        DecoderFrameImage.Source = _decoderBitmap;
                    }

                    if (_decoderBitmap is not null) {
                        var dstStride = _decoderBitmap.BackBufferStride;
                        _decoderBitmap.Lock();
                        try {
                            var src = (byte*)_localFrameBuffer;
                            if (stride == dstStride) {
                                Buffer.MemoryCopy(src, (void*)_decoderBitmap.BackBuffer,
                                    dstStride * height, stride * height);
                            } else {
                                var dst = (byte*)_decoderBitmap.BackBuffer;
                                var copyLen = Math.Min(stride, dstStride);
                                for (var y = 0; y < height; y++) {
                                    Buffer.MemoryCopy(src, dst, copyLen, copyLen);
                                    src += stride;
                                    dst += dstStride;
                                }
                            }
                            _decoderBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                        } finally {
                            _decoderBitmap.Unlock();
                        }
                    }
                }
                _renderMissCount = 0;
            } catch when (_isUnloaded) { } catch (Exception ex) {
                Log.Warning(ex, "[VideoPlayer:{Name}] Decoder frame render error", _camera?.Name);
            }
        }
    }

    private void RegisterActiveFramePlayer() {
        // Shared singleton D3D9 device (single device for all players avoids GPU exhaustion)
        _d3dRenderer = D3DRenderer.Instance;

        // Per-player D3DImageSurface (backed by D3D9 offscreen surface from shared device)
        if (_d3dRenderer is not null) {
            _d3dImageSurface = new D3DImageSurface(_d3dRenderer);
            if (_d3dImageSurface.IsValid) {
                DecoderFrameImage.Source = _d3dImageSurface.Image;
            }
        }

        lock (_activeFramePlayers) {
            if (!_activeFramePlayers.Contains(this)) {
                _activeFramePlayers.Add(this);
            }
        }
        // Initialize per-player render interval from target FPS
        var fps = TargetFps > 0 ? TargetFps : 15;
        _renderIntervalTicks = (int)(Stopwatch.Frequency / fps);
        _nextRenderTimestamp = Stopwatch.GetTimestamp();
        EnsureFrameBatchTimer();
    }

    private void UnregisterActiveFramePlayer() {
        lock (_frameCopyLock) {
            // Free per-player private native buffer (no ring-buffer slot to release)
            if (_localFrameBuffer != IntPtr.Zero) {
                Marshal.FreeHGlobal(_localFrameBuffer);
                _localFrameBuffer = IntPtr.Zero;
                _localBufferSize = 0;
            }
            _hasNewFrame = false;
        }

        _d3dImageSurface?.Dispose();
        _d3dImageSurface = null;
        _d3dRenderer = null; // D3DRenderer is a shared singleton — do NOT dispose here
        _decoderBitmap = null;
        lock (_activeFramePlayers) {
            _activeFramePlayers.Remove(this);
            if (_activeFramePlayers.Count == 0 && _renderingSubscribed) {
                CompositionTarget.Rendering -= OnCompositionRendering;
                _renderingSubscribed = false;
            }
        }
    }

    public static void CleanupAllDecoderSessions() {
        if (_renderingSubscribed) {
            CompositionTarget.Rendering -= OnCompositionRendering;
            _renderingSubscribed = false;
        }
        lock (_activeFramePlayers) {
            foreach (var p in _activeFramePlayers) {
                lock (p._frameCopyLock) {
                    if (p._localFrameBuffer != IntPtr.Zero) {
                        Marshal.FreeHGlobal(p._localFrameBuffer);
                        p._localFrameBuffer = IntPtr.Zero;
                        p._localBufferSize = 0;
                    }
                    p._hasNewFrame = false;
                }
                p._d3dImageSurface?.Dispose();
                p._d3dImageSurface = null;
                p._d3dRenderer = null;
            }
            _activeFramePlayers.Clear();
        }
        D3DRenderer.DisposeInstance();
    }

    public static void CleanupAllFlyleafPlayers() {
        lock (_flyleafPlayers) {
            foreach (var p in _flyleafPlayers) {
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

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject {
        while (child is not null) {
            if (child is T match) { return match; }
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
