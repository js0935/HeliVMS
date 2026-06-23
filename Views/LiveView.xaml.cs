using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private double _playbackSpeed = 1.0;
    private static readonly double[] FwdSpeeds = [1.0, 2.0, 4.0, 8.0, 16.0];
    private int _fwdSpeedIndex;
    private DateTime _timelineDay = DateTime.Today;
    private bool _timelineSyncing;
    private bool _isDraggingTimeline;

    public LiveView() {
        InitializeComponent();
        Loaded += LiveView_Loaded;
    }

    private void LiveView_Loaded(object sender, RoutedEventArgs e) {
        if (_initialized) return;
        _initialized = true;

        VideoGrid.DropCameraRequested += OnDropCameraToGrid;
        VideoGrid.SlotChanged += (cam, _) => {
            _ptzCamera = cam is { HasPTZ: true } ? cam : null;
            PtzBar.Visibility = _ptzCamera is not null ? Visibility.Visible : Visibility.Collapsed;
            if (_ptzCamera is not null)
                PtzLabel.Text = _ptzCamera.Name;
        };

        VideoGrid.SetSlotCount(4);
        ReloadAllCamerasIntoGrid();
        StartLiveTicker();

        Log.Debug("[LiveView] Initialized — grid={GridSize}, cameras={CamCount}",
            4, VideoGrid.GetSlotCameras().Count(c => c is not null));
    }

    private void StartLiveTicker() {
        _liveTicker = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => {
            if (_playbackMode != PlaybackMode.Live) return;
            var elapsed = DateTime.Now.TimeOfDay.TotalSeconds;
            _timelineSyncing = true;
            GlobalTimeline.Value = Math.Clamp(elapsed, 0, 86400);
            _timelineSyncing = false;
            UpdateTimelineTimeLabel(DateTime.Now);
        }, Dispatcher);
        _liveTicker.Start();
    }

    private void GlobalTimeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        if (_timelineSyncing) return;
        var seconds = e.NewValue;
        var time = _timelineDay.AddSeconds(seconds);
        UpdateTimelineTimeLabel(time);
        if (_isDraggingTimeline) {
            var targetTime = GetTimelineTime();
            foreach (var p in VideoGrid.GetActiveSlots())
                p.SwitchToPlayback(targetTime);
        }
    }

    private void GlobalTimeline_PreviewMouseDown(object sender, MouseButtonEventArgs e) {
        _isDraggingTimeline = true;
        if (_playbackMode == PlaybackMode.Live)
            SwitchToSeekMode(GetTimelineTime());
    }

    private DateTime GetTimelineTime() => _timelineDay.AddSeconds(GlobalTimeline.Value);

    private void UpdateTimelineTimeLabel(DateTime time) {
        TimelineTimeLabel.Text = time.ToString("HH:mm:ss");
    }

    private void SwitchToSeekMode(DateTime targetTime) {
        _playbackMode = PlaybackMode.CustomSeek;
        _liveTicker?.Stop();
        PerformSeek(targetTime);
    }

    private void SwitchToLive() {
        _playbackMode = PlaybackMode.Live;
        _fwdSpeedIndex = 0;
        _playbackSpeed = 1.0;
        FwdSpeedLabel.Text = "1×";
        _liveTicker?.Start();

        foreach (var p in VideoGrid.GetActiveSlots())
            p.SwitchToLive();

        _timelineSyncing = true;
        GlobalTimeline.Value = Math.Clamp(DateTime.Now.TimeOfDay.TotalSeconds, 0, 86400);
        _timelineSyncing = false;
        UpdateTimelineTimeLabel(DateTime.Now);
    }

    private void PerformSeek(DateTime targetTime) {
        var players = VideoGrid.GetActiveSlots();
        if (players.Count == 0) return;
        foreach (var p in players)
            p.SwitchToPlayback(targetTime);
    }

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

    private void BtnLive_Click(object sender, RoutedEventArgs e) {
        SwitchToLive();
    }

    public async void RefreshRecordingBars() {
        RecordingBars.Children.Clear();
        try {
            var cameras = VideoGrid.GetSlotCameras().Where(c => c is not null).Cast<Camera>().ToList();
            if (cameras.Count == 0) return;

            var today = DateTime.Today;
            var recordings = await VideoIndex.QuerySegmentsByCamerasAsync(
                cameras.Select(c => c.Id), today, today.AddDays(1));

            if (recordings.Count == 0) return;

            var width = RecordingBars.ActualWidth;
            if (width <= 0) width = 400;

            foreach (var seg in recordings) {
                var startFrac = (seg.StartTime.TimeOfDay.TotalSeconds) / 86400.0;
                var endFrac = seg.EndTime.HasValue
                    ? seg.EndTime.Value.TimeOfDay.TotalSeconds / 86400.0
                    : 1.0;
                var x = startFrac * width;
                var w = Math.Max(1, (endFrac - startFrac) * width);

                var rect = new Rectangle {
                    Fill = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x7A, 0xCC)),
                    Width = w,
                    Height = RecordingBars.ActualHeight > 0 ? RecordingBars.ActualHeight : 4,
                    RadiusX = 1,
                    RadiusY = 1,
                    ToolTip = $"{seg.CameraId}: {seg.StartTime:HH:mm}\u2013{seg.EndTime?.ToString("HH:mm") ?? "now"}"
                };
                Canvas.SetLeft(rect, x);
                RecordingBars.Children.Add(rect);
            }
        } catch (Exception ex) {
            Log.Debug("[LiveView] RefreshRecordingBars error: {Msg}", ex.Message);
        }
    }

    private void OnDropCameraToGrid(string cameraId, int slotIndex) {
        var camera = CameraService.GetCameraById(cameraId);
        if (camera is null) return;

        var existing = VideoGrid.GetCameraAt(slotIndex);
        if (existing is not null) {
            for (int i = 0; i < 64; i++) {
                if (VideoGrid.GetCameraAt(i)?.Id == cameraId && i != slotIndex) {
                    VideoGrid.AssignSlot(i, existing);
                    break;
                }
            }
        }

        VideoGrid.AssignSlot(slotIndex, camera);
    }

    private void Layout_Click(object sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var size))
            VideoGrid.SetSlotCount(size);
    }

    private void PtzUp_Click(object sender, RoutedEventArgs e) => MovePtz("up");
    private void PtzDown_Click(object sender, RoutedEventArgs e) => MovePtz("down");
    private void PtzLeft_Click(object sender, RoutedEventArgs e) => MovePtz("left");
    private void PtzRight_Click(object sender, RoutedEventArgs e) => MovePtz("right");
    private void PtzZoomIn_Click(object sender, RoutedEventArgs e) => MovePtz("zoomin");
    private void PtzZoomOut_Click(object sender, RoutedEventArgs e) => MovePtz("zoomout");

    private void MovePtz(string direction) {
        if (_ptzCamera is null) return;
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    public void ToggleFullScreen() {
        if (_isFullScreen) ExitFullScreen(); else EnterFullScreen();
    }

    private void EnterFullScreen() {
        if (_isFullScreen) return;
        _isFullScreen = true;

        TimelineContainer.Visibility = Visibility.Collapsed;
        ControlBar.Visibility = Visibility.Collapsed;

        if (Window.GetWindow(this) is Window win) {
            win.WindowStyle = WindowStyle.None;
            win.WindowState = WindowState.Maximized;
            win.PreviewKeyDown += OnWindowPreviewKeyDown;
        }
    }

    private void ExitFullScreen() {
        if (!_isFullScreen) return;
        _isFullScreen = false;

        if (Window.GetWindow(this) is Window win) {
            win.WindowState = WindowState.Normal;
            win.WindowStyle = WindowStyle.SingleBorderWindow;
            win.PreviewKeyDown -= OnWindowPreviewKeyDown;
        }

        TimelineContainer.Visibility = Visibility.Visible;
        ControlBar.Visibility = Visibility.Visible;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) ExitFullScreen();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.F5) { ReloadAllCamerasIntoGrid(); e.Handled = true; }
    }

    private void ReloadAllCamerasIntoGrid() {
        var all = CameraService.GetAllCameras();
        var arr = new Camera?[Math.Min(all.Count, 64)];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = all[i];
        VideoGrid.LoadCameras(arr);
        Log.Debug("[LiveView] Loaded {Count} cameras into grid", arr.Length);
    }
}