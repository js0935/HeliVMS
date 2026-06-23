using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HeliVMS.Controls;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HeliVMS.Views;

public enum PlaybackMode {
    Live,
    CustomSeek
}

public partial class LiveView : UserControl {
    private ICameraService? _lazyCamera;
    private ICameraService CameraService => _lazyCamera ??= App.Services.GetRequiredService<ICameraService>();
    private IVideoIndexService? _lazyVideoIndex;
    private IVideoIndexService VideoIndex => _lazyVideoIndex ??= App.Services.GetRequiredService<IVideoIndexService>();
    private Camera? _ptzCamera;
    private bool _isFullScreen;
    private bool _initialized;

    private PlaybackMode _playbackMode = PlaybackMode.Live;
    private DispatcherTimer? _liveTicker;
    private DispatcherTimer? _recordingBarTimer;
    private double _playbackSpeed = 1.0;
    private static readonly double[] FwdSpeeds = [1.0, 2.0, 4.0, 8.0, 16.0];
    private static readonly Color[] BarColors = [
        Color.FromRgb(0x00, 0x7A, 0xCC),
        Color.FromRgb(0x00, 0xCC, 0x7A),
        Color.FromRgb(0xCC, 0x7A, 0x00),
        Color.FromRgb(0x7A, 0x00, 0xCC),
        Color.FromRgb(0xCC, 0x00, 0x7A),
        Color.FromRgb(0x00, 0xCC, 0xCC),
        Color.FromRgb(0xCC, 0xCC, 0x00),
        Color.FromRgb(0x7A, 0xCC, 0x00),
    ];
    private int _fwdSpeedIndex;
    private DateTime _timelineDay = DateTime.Today;
    private bool _timelineSyncing;
    private bool _isDraggingTimeline;

    private const int DefaultGridSize = 4;
    private const int MaxSlots = 64;

    public LiveView() {
        InitializeComponent();
        Loaded += LiveView_Loaded;
        Unloaded += (_, _) => Cleanup();
    }

    // ═══════════════════════════════════════════════════════════
    //  Loaded / Ignition
    // ═══════════════════════════════════════════════════════════

    private void LiveView_Loaded(object sender, RoutedEventArgs e) {
        if (_initialized) return;
        _initialized = true;

        VideoGrid.DropCameraRequested += OnDropCameraToGrid;
        VideoGrid.SlotChanged += OnSlotChanged;

        VideoGrid.SetSlotCount(DefaultGridSize);
        Log.Debug("[LiveView] Grid layout set to {Size}x{Size}",
            DefaultGridSize, DefaultGridSize);

        ReloadAllCamerasIntoGrid();
        StartLiveTicker();
        RefreshRecordingBars();
        StartRecordingBarTimer();

        Log.Debug("[LiveView] Loaded — cameras in grid: {Count}",
            VideoGrid.GetSlotCameras().Count(c => c is not null));
    }

    private void Cleanup() {
        _liveTicker?.Stop();
        _liveTicker = null;
        _recordingBarTimer?.Stop();
        _recordingBarTimer = null;
    }

    // ═══════════════════════════════════════════════════════════
    //  Camera Grid — Load / Reload
    // ═══════════════════════════════════════════════════════════

    private void ReloadAllCamerasIntoGrid() {
        try {
            var all = CameraService.GetAllCameras();
            if (all.Count == 0) {
                Log.Warning("[LiveView] No cameras returned from service");
                VideoGrid.SetSlotCount(DefaultGridSize);
                return;
            }

            var count = Math.Min(all.Count, MaxSlots);
            var arr = new Camera?[count];
            for (int i = 0; i < count; i++)
                arr[i] = all[i];

            VideoGrid.LoadCameras(arr);
            Log.Debug("[LiveView] Loaded {Count}/{Total} cameras into grid", count, all.Count);
        } catch (Exception ex) {
            Log.Error(ex, "[LiveView] Failed to load cameras");
        }
    }

    private void OnSlotChanged(Camera? camera, int slotIndex) {
        _ptzCamera = camera is { HasPTZ: true } ? camera : null;
        PtzBar.Visibility = _ptzCamera is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_ptzCamera is not null)
            PtzLabel.Text = _ptzCamera.Name;
    }

    private void OnDropCameraToGrid(string cameraId, int slotIndex) {
        try {
            var camera = CameraService.GetCameraById(cameraId);
            if (camera is null) {
                Log.Warning("[LiveView] Drop: camera {Id} not found", cameraId);
                return;
            }

            var existing = VideoGrid.GetCameraAt(slotIndex);
            if (existing is not null) {
                for (int i = 0; i < MaxSlots; i++) {
                    if (VideoGrid.GetCameraAt(i)?.Id == cameraId && i != slotIndex) {
                        VideoGrid.AssignSlot(i, existing);
                        break;
                    }
                }
            }

            VideoGrid.AssignSlot(slotIndex, camera);
            Log.Debug("[LiveView] Camera {Name} dropped into slot {Slot}",
                camera.Name, slotIndex);
        } catch (Exception ex) {
            Log.Error(ex, "[LiveView] DropCameraToGrid failed");
        }
    }

    private void Layout_Click(object sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var size))
            VideoGrid.SetSlotCount(size);
    }

    // ═══════════════════════════════════════════════════════════
    //  Timeline / Live Ticker
    // ═══════════════════════════════════════════════════════════

    private void StartLiveTicker() {
        _liveTicker = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Normal,
            OnLiveTickerTick,
            Dispatcher);
        _liveTicker.Start();
    }

    private void OnLiveTickerTick(object? sender, EventArgs e) {
        if (_playbackMode != PlaybackMode.Live) return;
        var elapsed = DateTime.Now.TimeOfDay.TotalSeconds;
        _timelineSyncing = true;
        GlobalTimeline.Value = Math.Clamp(elapsed, 0, 86400);
        _timelineSyncing = false;
        UpdateTimelineTimeLabel(DateTime.Now);
    }

    private void GlobalTimeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        if (_timelineSyncing) return;
        var time = _timelineDay.AddSeconds(e.NewValue);
        UpdateTimelineTimeLabel(time);
        if (_isDraggingTimeline) {
            foreach (var p in VideoGrid.GetActiveSlots())
                p.SwitchToPlayback(GetTimelineTime());
        }
    }

    private void GlobalTimeline_PreviewMouseDown(object sender, MouseButtonEventArgs e) {
        _isDraggingTimeline = true;
        if (_playbackMode == PlaybackMode.Live)
            SwitchToSeekMode(GetTimelineTime());
    }

    private void GlobalTimeline_PreviewMouseUp(object sender, MouseButtonEventArgs e) {
        _isDraggingTimeline = false;
    }

    private DateTime GetTimelineTime() => _timelineDay.AddSeconds(GlobalTimeline.Value);

    private void UpdateTimelineTimeLabel(DateTime time) {
        TimelineTimeLabel.Text = time.ToString("HH:mm:ss");
    }

    // ═══════════════════════════════════════════════════════════
    //  Playback Mode Switching
    // ═══════════════════════════════════════════════════════════

    private void SwitchToSeekMode(DateTime targetTime) {
        _playbackMode = PlaybackMode.CustomSeek;
        _liveTicker?.Stop();
        PerformSeek(targetTime);
        Log.Debug("[LiveView] Switched to CustomSeek at {Time}",
            targetTime.ToString("HH:mm:ss"));
    }

    private void SwitchToLive() {
        _playbackMode = PlaybackMode.Live;
        _fwdSpeedIndex = 0;
        _playbackSpeed = 1.0;
        FwdSpeedLabel.Text = "1\u00d7";
        _liveTicker?.Start();

        foreach (var p in VideoGrid.GetActiveSlots())
            p.SwitchToLive();

        _timelineSyncing = true;
        GlobalTimeline.Value = Math.Clamp(DateTime.Now.TimeOfDay.TotalSeconds, 0, 86400);
        _timelineSyncing = false;
        UpdateTimelineTimeLabel(DateTime.Now);

        Log.Debug("[LiveView] Switched to Live");
    }

    private void PerformSeek(DateTime targetTime) {
        var players = VideoGrid.GetActiveSlots();
        if (players.Count == 0) return;
        foreach (var p in players)
            p.SwitchToPlayback(targetTime);
        Log.Debug("[LiveView] Seek to {Time} for {Count} players",
            targetTime.ToString("HH:mm:ss"), players.Count);
    }

    // ═══════════════════════════════════════════════════════════
    //  Transport Controls
    // ═══════════════════════════════════════════════════════════

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e) {
        if (_playbackMode == PlaybackMode.Live)
            SwitchToSeekMode(DateTime.Now);
        else
            SwitchToLive();
    }

    private void BtnFwd_Click(object sender, RoutedEventArgs e) {
        if (_playbackMode == PlaybackMode.Live)
            SwitchToSeekMode(DateTime.Now);
        _fwdSpeedIndex = (_fwdSpeedIndex + 1) % FwdSpeeds.Length;
        _playbackSpeed = FwdSpeeds[_fwdSpeedIndex];
        FwdSpeedLabel.Text = $"{_playbackSpeed:F0}\u00d7";
    }

    private void BtnLive_Click(object sender, RoutedEventArgs e) => SwitchToLive();

    // ═══════════════════════════════════════════════════════════
    //  Recording Bar Visualization
    // ═══════════════════════════════════════════════════════════

    private void StartRecordingBarTimer() {
        _recordingBarTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(30),
            DispatcherPriority.Background,
            (_, _) => RefreshRecordingBars(),
            Dispatcher);
        _recordingBarTimer.Start();
    }

    public async void RefreshRecordingBars() {
        RecordingBars.Children.Clear();
        try {
            var cameras = VideoGrid.GetSlotCameras()
                .Where(c => c is not null)
                .Cast<Camera>()
                .ToList();
            if (cameras.Count == 0) return;

            var today = DateTime.Today;
            var recordings = await VideoIndex.QuerySegmentsByCamerasAsync(
                cameras.Select(c => c.Id), today, today.AddDays(1));
            if (recordings.Count == 0) return;

            var width = RecordingBars.ActualWidth;
            if (width <= 0) width = 400;
            var height = RecordingBars.ActualHeight > 0 ? RecordingBars.ActualHeight : 6;

            var cameraIds = recordings.Select(r => r.CameraId).Distinct().ToList();
            var colorMap = new Dictionary<string, Color>(cameraIds.Count);
            for (int i = 0; i < cameraIds.Count; i++)
                colorMap[cameraIds[i]] = BarColors[i % BarColors.Length];

            foreach (var seg in recordings) {
                var startFrac = seg.StartTime.TimeOfDay.TotalSeconds / 86400.0;
                var endFrac = seg.EndTime.HasValue
                    ? seg.EndTime.Value.TimeOfDay.TotalSeconds / 86400.0
                    : 1.0;
                var x = startFrac * width;
                var w = Math.Max(1, (endFrac - startFrac) * width);
                var color = colorMap.GetValueOrDefault(seg.CameraId, BarColors[0]);
                var isLive = !seg.EndTime.HasValue;

                var rect = new Rectangle {
                    Fill = new SolidColorBrush(Color.FromArgb((byte)(isLive ? 0xCC : 0x99), color.R, color.G, color.B)),
                    Width = w,
                    Height = isLive ? height * 1.5 : height,
                    RadiusX = 1,
                    RadiusY = 1,
                    ToolTip = $"{seg.CameraId}: {seg.StartTime:HH:mm}\u2013{(isLive ? "錄影中" : seg.EndTime!.Value.ToString("HH:mm"))}"
                };
                Canvas.SetLeft(rect, x);
                RecordingBars.Children.Add(rect);
            }
        } catch (Exception ex) {
            Log.Debug("[LiveView] RefreshRecordingBars error: {Msg}", ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  PTZ
    // ═══════════════════════════════════════════════════════════

    private void PtzUp_Click(object sender, RoutedEventArgs e) => MovePtz("up");
    private void PtzDown_Click(object sender, RoutedEventArgs e) => MovePtz("down");
    private void PtzLeft_Click(object sender, RoutedEventArgs e) => MovePtz("left");
    private void PtzRight_Click(object sender, RoutedEventArgs e) => MovePtz("right");
    private void PtzZoomIn_Click(object sender, RoutedEventArgs e) => MovePtz("zoomin");
    private void PtzZoomOut_Click(object sender, RoutedEventArgs e) => MovePtz("zoomout");

    private void MovePtz(string direction) {
        if (_ptzCamera is null) return;
    }

    // ═══════════════════════════════════════════════════════════
    //  Full Screen
    // ═══════════════════════════════════════════════════════════

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    public void ToggleFullScreen() {
        if (_isFullScreen) ExitFullScreen(); else EnterFullScreen();
    }

    private void EnterFullScreen() {
        if (_isFullScreen) return;
        _isFullScreen = true;

        TimelineContainer.Visibility = Visibility.Collapsed;
        ControlBar.Visibility = Visibility.Collapsed;

        if (Window.GetWindow(this) is MainWindow main) {
            main.SetFullScreenUI(true);
            main.WindowStyle = WindowStyle.None;
            main.WindowState = WindowState.Maximized;
            main.PreviewKeyDown += OnWindowPreviewKeyDown;
        }
    }

    private void ExitFullScreen() {
        if (!_isFullScreen) return;
        _isFullScreen = false;

        if (Window.GetWindow(this) is MainWindow main) {
            main.SetFullScreenUI(false);
            main.WindowState = WindowState.Normal;
            main.WindowStyle = WindowStyle.SingleBorderWindow;
            main.PreviewKeyDown -= OnWindowPreviewKeyDown;
        }

        TimelineContainer.Visibility = Visibility.Visible;
        ControlBar.Visibility = Visibility.Visible;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) ExitFullScreen();
    }

    // ═══════════════════════════════════════════════════════════
    //  Misc
    // ═══════════════════════════════════════════════════════════

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.F5) { ReloadAllCamerasIntoGrid(); e.Handled = true; }
    }
}