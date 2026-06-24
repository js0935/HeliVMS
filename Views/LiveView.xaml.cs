using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private ILayoutService? _lazyLayout;
    private ILayoutService LayoutService => _lazyLayout ??= App.Services.GetRequiredService<ILayoutService>();
    private Camera? _ptzCamera;
    private bool _isFullScreen;
    public bool IsFullScreen => _isFullScreen;
    private bool _initialized;

    private PlaybackMode _playbackMode = PlaybackMode.Live;
    private DispatcherTimer? _liveTicker;
    private double _playbackSpeed = 1.0;
    private static readonly double[] FwdSpeeds = [1.0, 2.0, 4.0, 8.0, 16.0];
    private int _fwdSpeedIndex;
    private DateTime _timelineDay = DateTime.Today;

    private int _currentGridSize = 4;
    private const int DefaultGridSize = 4;
    public string? SelectedTabId => TabBar.CurrentTab?.Id;
    private const int MaxSlots = 64;
    private readonly IBookmarkService _bookmarks = App.Services.GetRequiredService<IBookmarkService>();
    private readonly ISettingsService _settings = App.Services.GetRequiredService<ISettingsService>();

    public LiveView() {
        InitializeComponent();
        Loaded += LiveView_Loaded;
        TimelineControl.PositionChanged += OnTimelinePositionChanged;
        TimelineControl.BookmarkRequested += (_, _) => AddBookmark();
        TimelineControl.GoLiveRequested += (_, _) => SwitchToLive();
        RecordingService.RecordingStatusChanged += (camId, isRec) =>
            _ = Dispatcher.InvokeAsync(() => VideoGrid.SetRecordingIndicator(camId, isRec));
        _talkService.AudioLevelChanged += level =>
            _ = Dispatcher.InvokeAsync(() => {
                var w = Math.Clamp(level * 4, 0, 1) * 20;
                TalkLevelBar.Width = w;
                TalkLevelBar.Background = level > 0.5
                    ? System.Windows.Media.Brushes.LimeGreen
                    : System.Windows.Media.Brushes.Gray;
            });
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
        VideoGrid.SlotContextMenuRequested += OnGridContextMenu;
        TabBar.TabSelected += OnTabSelected;
        TabBar.TabAdded += OnTabAdded;
        TabBar.TabRenamed += (_, id) => SaveCurrentTabs();
        TabBar.TabsReordered += (_, _) => SaveCurrentTabs();

        VideoGrid.SetSlotCount(DefaultGridSize);
        Log.Debug("[LiveView] Grid layout set to {Size}x{Size}",
            DefaultGridSize, DefaultGridSize);

        LoadLayoutTabs();
        ReloadAllCamerasIntoGrid();
        StartLiveTicker();
        ReloadTimelineData();

        Log.Debug("[LiveView] Loaded — cameras in grid: {Count}",
            VideoGrid.GetSlotCameras().Count(c => c is not null));
    }

    private void Cleanup() {
        _liveTicker?.Stop();
        _liveTicker = null;
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

            var threshold = _settings.Settings.SubStreamThreshold;
            var useSub = count >= threshold;
            VideoGrid.LoadCameras(arr, useSub);
            foreach (var cam in arr)
                if (cam is not null)
                    VideoGrid.SetRecordingIndicator(cam.Id, RecordingService.IsRecording(cam.Id));
            Log.Debug("[LiveView] Loaded {Count}/{Total} cameras into grid (subStream={Sub}, threshold={Thresh})", count, all.Count, useSub, threshold);
        } catch (Exception ex) {
            Log.Error(ex, "[LiveView] Failed to load cameras");
        }
    }

    private void CameraFilterBox_TextChanged(object sender, TextChangedEventArgs e) {
        try {
            var all = CameraService.GetAllCameras();
            var filter = CameraFilterBox.Text.Trim().ToLowerInvariant();
            IEnumerable<Camera> filtered = all;
            if (!string.IsNullOrEmpty(filter))
                filtered = all.Where(c => c.Name.ToLowerInvariant().Contains(filter)
                    || c.Id.ToLowerInvariant().Contains(filter));

            var count = Math.Min(filtered.Count(), MaxSlots);
            var arr = new Camera?[count];
            var i = 0;
            foreach (var cam in filtered.Take(count))
                arr[i++] = cam;

            VideoGrid.LoadCameras(arr);
            foreach (var cam in arr)
                if (cam is not null)
                    VideoGrid.SetRecordingIndicator(cam.Id, RecordingService.IsRecording(cam.Id));
        } catch (Exception ex) {
            Log.Debug("[LiveView] Filter error: {Msg}", ex.Message);
        }
    }

    private void OnGridContextMenu(string cameraId, string action) {
        var cam = CameraService.GetCameraById(cameraId);
        if (cam is null) return;
        switch (action) {
            case "移除攝影機": {
                var slots = VideoGrid.GetSlotCameras().ToList();
                var idx = slots.FindIndex(c => c?.Id == cameraId);
                if (idx >= 0) VideoGrid.RemoveSlot(idx);
                break;
            }
            case "開始錄影": RecordingService.StartRecording(cam); break;
            case "停止錄影": RecordingService.StopRecording(cam.Id); break;
            case "PTZ 控制": HandleCameraAction(cameraId, "ptz"); break;
        }
    }

    private void OnSlotChanged(Camera? camera, int slotIndex) {
        _ptzCamera = camera is { HasPTZ: true } ? camera : null;
        PtzBar.Visibility = _ptzCamera is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_ptzCamera is not null)
            PtzLabel.Text = _ptzCamera.Name;
        if (TabBar.CurrentTab is not null)
            TabBar.MarkDirty(TabBar.CurrentTab.Id);
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
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var size)) {
            _currentGridSize = size;
            VideoGrid.SetSlotCount(size);
            if (TabBar.CurrentTab is not null)
                TabBar.MarkDirty(TabBar.CurrentTab.Id);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Layout Tabs
    // ═══════════════════════════════════════════════════════════

    private void LoadLayoutTabs() {
        var tabs = LayoutService.GetAllTabs();
        if (tabs.Count == 0) {
            var def = LayoutService.CreateTab("預設佈局");
            tabs = [def];
        }
        TabBar.LoadTabs(tabs);
        if (tabs.Count > 0)
            LoadTabLayout(tabs[0]);
    }

    private void OnTabSelected(object? sender, LayoutTab tab) {
        SaveCurrentTabToService();
        LoadTabLayout(tab);
    }

    private void OnTabAdded(object? sender, LayoutTab tab) {
        LayoutService.SaveTab(tab);
        LoadTabLayout(tab);
    }

    private void LoadTabLayout(LayoutTab tab) {
        _currentGridSize = tab.SlotCount;
        VideoGrid.SetSlotCount(tab.SlotCount);
        var cameras = CameraService.GetAllCameras();
        var camMap = cameras.ToDictionary(c => c.Id, c => c);
        var arr = new Camera?[tab.CameraIds.Count];
        for (int i = 0; i < tab.CameraIds.Count; i++) {
            var camId = tab.CameraIds[i];
            arr[i] = camId is not null && camMap.TryGetValue(camId, out var cam) ? cam : null;
        }
        if (arr.Length > 0)
            VideoGrid.LoadCameras(arr);
        ReloadTimelineData();
    }

    private void SaveCurrentTabToService() {
        var tab = TabBar.CurrentTab;
        if (tab is null) return;
        tab.SlotCount = _currentGridSize;
        tab.CameraIds = VideoGrid.GetSlotCameras().Take(_currentGridSize).Select(c => c?.Id).ToList();
        LayoutService.SaveTab(tab);
    }

    private void SaveCurrentTabs() {
        var tab = TabBar.CurrentTab;
        if (tab is not null) LayoutService.SaveTab(tab);
    }

    private void BtnSaveLayout_Click(object sender, RoutedEventArgs e) {
        var tab = TabBar.CurrentTab;
        if (tab is null) return;
        var dlg = new Dialog.InputDialog("重新命名佈局", "請輸入佈局名稱：", tab.Name) {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value)) {
            tab.Name = dlg.Value.Trim();
            LayoutService.SaveTab(tab);
            TabBar.MarkClean(tab.Id);
        }
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
        TimelineControl.SetPositionSilent(Math.Clamp(elapsed, 0, 86400));
    }

    private void OnTimelinePositionChanged(object? sender, double seconds) {
        if (_playbackMode == PlaybackMode.Live)
            SwitchToSeekMode(_timelineDay.AddSeconds(seconds));
        else {
            var time = _timelineDay.AddSeconds(seconds);
            foreach (var p in VideoGrid.GetActiveSlots())
                p.SwitchToPlayback(time);
            VideoGrid.UpdatePlaybackTime(seconds);
        }
    }

    internal DateTime GetTimelineTime() => _timelineDay.AddSeconds(TimelineControl.PositionSeconds);

    public bool TimelineVisible {
        get => TimelineContainer.Visibility == Visibility.Visible;
        set => TimelineContainer.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    // ═══════════════════════════════════════════════════════════
    //  Playback Mode Switching
    // ═══════════════════════════════════════════════════════════

    private void ShowPlaybackTimeBadge(bool visible) {
        VideoGrid.SetPlaybackTimeVisible(visible);
        if (visible) {
            var dt = _playbackMode == PlaybackMode.CustomSeek
                ? _timelineDay.Date.AddSeconds(TimelineControl.PositionSeconds) : DateTime.Now;
            VideoGrid.UpdatePlaybackTime(dt.TimeOfDay.TotalSeconds);
        }
    }

    private void SwitchToSeekMode(DateTime targetTime) {
        _playbackMode = PlaybackMode.CustomSeek;
        _liveTicker?.Stop();
        PerformSeek(targetTime);
        ShowPlaybackTimeBadge(true);
        Log.Debug("[LiveView] Switched to CustomSeek at {Time}",
            targetTime.ToString("HH:mm:ss"));
    }

    private void SwitchToLive() {
        _playbackMode = PlaybackMode.Live;
        _fwdSpeedIndex = 0;
        _playbackSpeed = 1.0;
        FwdSpeedLabel.Text = "1\u00d7";
        VideoGrid.SetPlaybackSpeed(1.0);
        ShowPlaybackTimeBadge(false);
        _liveTicker?.Start();

        foreach (var p in VideoGrid.GetActiveSlots())
            p.SwitchToLive();

        var elapsed = DateTime.Now.TimeOfDay.TotalSeconds;
        TimelineControl.SetPositionSilent(Math.Clamp(elapsed, 0, 86400));

        Log.Debug("[LiveView] Switched to Live");
    }

    public void NavigateToDate(DateTime date) {
        _timelineDay = date.Date;
        TimelineControl.TimelineDay = date.Date;
        var secs = Math.Clamp(DateTime.Now.TimeOfDay.TotalSeconds, 0, 86400);
        TimelineControl.SetPositionSilent(secs);
        PerformSeek(_timelineDay.AddSeconds(secs));
        ReloadTimelineData();
        Log.Debug("[LiveView] NavigateToDate: {Date}", date.ToString("yyyy-MM-dd"));
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
        VideoGrid.SetPlaybackSpeed(_playbackSpeed);
    }

    private void BtnSkipBack_Click(object sender, RoutedEventArgs e) => SkipTimeline(-30);
    private void BtnSkipFwd_Click(object sender, RoutedEventArgs e) => SkipTimeline(30);

    private void SkipTimeline(int seconds) {
        if (_playbackMode == PlaybackMode.Live)
            SwitchToSeekMode(DateTime.Now);
        var newVal = Math.Clamp(TimelineControl.PositionSeconds + seconds, 0, 86400);
        TimelineControl.PositionSeconds = newVal;
        var targetTime = _timelineDay.AddSeconds(newVal);
        PerformSeek(targetTime);
    }

    private void BtnLive_Click(object sender, RoutedEventArgs e) => SwitchToLive();
    private void BtnNow_Click(object sender, RoutedEventArgs e) {
        if (_playbackMode == PlaybackMode.Live) return;
        var now = DateTime.Now;
        TimelineControl.SetPosition(now);
        PerformSeek(now);
    }
    private bool _filterCont = true, _filterMotion = true, _filterAlarm = true, _filterAi = true;

    private void FilterCont_Click(object sender, RoutedEventArgs e) {
        _filterCont = !_filterCont;
        FilterCont.Background = _filterCont ? new SolidColorBrush(Color.FromArgb(0x33, 0x21, 0x96, 0xF3)) : System.Windows.Media.Brushes.Transparent;
        TimelineControl.SetTypeFilter(_filterCont, _filterMotion, _filterAlarm, _filterAi);
    }

    private void FilterMotion_Click(object sender, RoutedEventArgs e) {
        _filterMotion = !_filterMotion;
        FilterMotion.Background = _filterMotion ? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x57, 0x22)) : System.Windows.Media.Brushes.Transparent;
        TimelineControl.SetTypeFilter(_filterCont, _filterMotion, _filterAlarm, _filterAi);
    }

    private void FilterAlarm_Click(object sender, RoutedEventArgs e) {
        _filterAlarm = !_filterAlarm;
        FilterAlarm.Background = _filterAlarm ? new SolidColorBrush(Color.FromArgb(0x33, 0xF4, 0x43, 0x36)) : System.Windows.Media.Brushes.Transparent;
        TimelineControl.SetTypeFilter(_filterCont, _filterMotion, _filterAlarm, _filterAi);
    }

    private void FilterAi_Click(object sender, RoutedEventArgs e) {
        _filterAi = !_filterAi;
        FilterAi.Background = _filterAi ? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xC1, 0x07)) : System.Windows.Media.Brushes.Transparent;
        TimelineControl.SetTypeFilter(_filterCont, _filterMotion, _filterAlarm, _filterAi);
    }

    private void AddBookmark() {
        var posSecs = TimelineControl.PositionSeconds;
        var bm = new PlaybackBookmark {
            Seconds = posSecs,
            Note = $"標記 {DateTime.Now:HH:mm:ss}"
        };
        _bookmarks.SaveBookmark(bm, _timelineDay);
        TimelineControl.AddBookmark(bm);
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e) {
        var dlg = new Dialog.ExportDialog { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }
    private readonly IAudioTalkService _talkService = App.Services.GetRequiredService<IAudioTalkService>();
    private IRecordingService? _lazyRecording;
    private IRecordingService RecordingService => _lazyRecording ??= App.Services.GetRequiredService<IRecordingService>();
    private void BtnTalk_Click(object sender, RoutedEventArgs e) {
        if (_talkService.IsTalking) {
            _talkService.StopTalking();
            BtnTalk.Background = System.Windows.Media.Brushes.Transparent;
            BtnTalk.Foreground = TryFindResource("TextBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
        } else {
            var cameras = VideoGrid.GetSlotCameras().Where(c => c is not null).Select(c => c!.Id).ToList();
            var targetCamera = cameras.FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(targetCamera)) return;
            var ok = _talkService.StartTalking(targetCamera);
            if (ok) {
                BtnTalk.Background = System.Windows.Media.Brushes.Red;
                BtnTalk.Foreground = System.Windows.Media.Brushes.White;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Recording Bar Visualization
    // ═══════════════════════════════════════════════════════════

    public void ToggleRecordingSelectedCamera() {
        var selected = VideoGrid.GetActiveSlots().FirstOrDefault(p => p.IsSelected);
        var cam = selected?.Camera;
        if (cam is null) return;
        if (RecordingService.IsRecording(cam.Id)) {
            RecordingService.StopRecording(cam.Id);
        } else {
            RecordingService.StartRecording(cam);
        }
    }

    public void HandleCameraAction(string cameraId, string action) {
        var cam = App.Services.GetRequiredService<ICameraService>().GetAllCameras()
            .FirstOrDefault(c => c.Id == cameraId);
        if (cam is null) return;
        switch (action) {
            case "play": {
                var grid = VideoGrid;
                var slots = grid.GetActiveSlots();
                var empty = grid.GetSlotCameras().ToList();
                var freeIndex = empty.FindIndex(c => c is null);
                if (freeIndex >= 0) grid.AssignSlot(freeIndex, cam);
                break;
            }
            case "start_recording":
                RecordingService.StartRecording(cam);
                break;
            case "stop_recording":
                RecordingService.StopRecording(cam.Id);
                break;
            case "ptz": {
                var panel = new Controls.PTZControlPanel { Width = 240 };
                panel.LoadCamera(cam);
                var win = new Window {
                    Title = $"PTZ — {cam.Name}", Content = panel,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Owner = Window.GetWindow(this),
                    Background = System.Windows.Media.Brushes.Transparent,
                    WindowStyle = WindowStyle.ToolWindow, Topmost = true,
                };
                win.Closed += (_, _) => panel.UnloadCamera();
                win.Show();
                break;
            }
        }
    }

    public async void ReloadTimelineData() {
        try {
            var cameras = VideoGrid.GetSlotCameras()
                .Where(c => c is not null)
                .Cast<Camera>()
                .ToList();
            if (cameras.Count == 0) return;

            var day = _timelineDay;
            var recordings = await VideoIndex.QuerySegmentsByCamerasAsync(
                cameras.Select(c => c.Id), day, day.AddDays(1));
            TimelineControl.LoadSegments(cameras.Select(c => c.Id), recordings);
            TimelineControl.LoadBookmarks(_bookmarks.LoadBookmarks(day));
        } catch (Exception ex) {
            Log.Debug("[LiveView] ReloadTimelineData error: {Msg}", ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  PTZ — popup panel with presets + tours
    // ═══════════════════════════════════════════════════════════

    private void BtnPtz_Click(object sender, RoutedEventArgs e) {
        if (_ptzCamera is null) return;
        var panel = new Controls.PTZControlPanel { Width = 240 };
        panel.LoadCamera(_ptzCamera);
        var win = new Window {
            Title = $"PTZ — {_ptzCamera.Name}",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Owner = Window.GetWindow(this),
            Background = System.Windows.Media.Brushes.Transparent,
            WindowStyle = WindowStyle.ToolWindow,
            Topmost = true,
        };
        win.Closed += (_, _) => panel.UnloadCamera();
        win.Show();
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
        else if (e.Key == Key.B) {
            AddBookmark();
            e.Handled = true;
        }
    }
}