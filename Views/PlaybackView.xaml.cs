using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HeliVMS.Controls;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Serilog;

namespace HeliVMS.Views;

public partial class PlaybackView : UserControl {
    private readonly ICameraService _cameraService;
    private readonly IRecordingService _recordingService;
    private readonly IVideoIndexService _videoIndexService;
    private readonly IEventService _eventLog;
    private readonly IBookmarkService _bookmarkService;
    private PlaybackCoordinator? _coordinator;

    private readonly List<ChannelCheckBox> _channelItems = [];
    private Dictionary<string, ChannelCheckBox> _cameraIdToChannelItem = [];
    private readonly List<PlaybackPlayer> _activePlayers = [];
    private ConcurrentDictionary<string, int> _cameraToSlotIndex = [];
    private FrameSlot[] _frameSlots = [];
    private CancellationTokenSource? _queryCts;
    private List<CameraRecordingInfo> _currentRecordings = [];
    private List<PlaybackBookmark> _bookmarks = [];
 
    private CancellationTokenSource? _searchCts;
    private List<SearchResultEntry> _searchResults = [];
    private int _selectedSearchIndex = -1;
    private readonly HashSet<int> _selectedSearchIndices = [];

    private DateTime _currentDate = DateTime.Today;
    private bool _isPlaying;
    private long _lastKnownDuration;
    private int _displayColumns = 4;
    private int _displayRows = 4;

    private bool _liveMode;
    private bool _syncEnabled = true;

    private const int MaxPlaybackChannels = 64;
    private readonly DispatcherTimer _clockTimer;
    private DispatcherTimer? _storageTimer;
    private bool _isFullScreen;
    private DispatcherTimer? _fsAutoHideTimer;
    private Border? _fsTopBar;
    private Border? _fsBottomBar;

    private int _frameDisplayCount;
    private DispatcherTimer? _fpsTimer;
    private bool _isPerfPanelVisible;
 
    private sealed class ChannelStats {
        public string CameraId = "";
        public string CameraName = "";
        public int ChannelNumber;
        public int DisplayCount;
        public int DropCount;
        public long LastPtsMicroseconds;
        public bool IsMaster;
        // Computed each second
        public int DisplayFps;
        public int DropFps;
        public int Lag;
        public int MaxLatencyMs;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ChannelStats> _channelStats = new();

    private static readonly string[] DayNames = ["日", "一", "二", "三", "四", "五", "六"];

    public PlaybackView() {
        Log.Information("[DBG] PlaybackView ctor start");
        InitializeComponent();
        Log.Information("[DBG] PlaybackView InitializeComponent done");

        _cameraService = App.Services.GetRequiredService<ICameraService>();
        _recordingService = App.Services.GetRequiredService<IRecordingService>();
        _videoIndexService = App.Services.GetRequiredService<IVideoIndexService>();
        _eventLog = App.Services.GetRequiredService<IEventService>();
        _bookmarkService = App.Services.GetRequiredService<IBookmarkService>();
        Log.Information("[DBG] PlaybackView DI resolved");

        FilterDatePicker.SelectedDate = DateTime.Today;

        _clockTimer = new DispatcherTimer {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += OnClockTick;
        UpdateClock();
        Log.Information("[DBG] PlaybackView ctor timer created");

        Loaded += OnPlaybackLoaded;
        Unloaded += OnPlaybackUnloaded;
    }

    private bool _isPlaybackLoaded;

    private void OnPlaybackLoaded(object sender, RoutedEventArgs e) {
        if (_isPlaybackLoaded) return;
        _isPlaybackLoaded = true;
        Log.Information("[DBG] PlaybackView Loaded START");
        try {
            PopulateChannelList();
            Log.Information("[DBG] PlaybackView PopulateChannelList done");
            UpdateTimelineZoomLabel();
            UpdateTimelineRecordingStats();
            UpdateFilterButtonOpacity();
            UpdateButtonStates();
            var win = Window.GetWindow(this);
            win?.PreviewKeyDown += OnPreviewKeyDown;
            _clockTimer.Start();
            Log.Information("[DBG] PlaybackView clock timer started");

            if (_fpsTimer is null) {
                _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _fpsTimer.Tick += (_, _) => {
                    var active = _activePlayers.Count;
                    if (active > 0 && _frameDisplayCount > 0) {
                        var totalFps = _frameDisplayCount;
                        _frameDisplayCount = 0;
                        var pipeLag = 0;
                        foreach (var slot in _frameSlots)
                            if (slot.Data is not null) pipeLag++;
                        FpsText.Text = active <= 16
                            ? $"Render: {totalFps}/{active}ch  Lag: {pipeLag}"
                            : $"R:{totalFps}/{active}ch L:{pipeLag}";

                        // Compute per-channel stats
                        var now = Stopwatch.GetTimestamp();
                        var freq = Stopwatch.Frequency / 1000.0;
                        var totalDrop = 0;
                        foreach (var kv in _channelStats) {
                            var s = kv.Value;
                            s.DisplayFps = s.DisplayCount;
                            s.DropFps = Interlocked.Exchange(ref s.DropCount, 0);
                            s.DisplayCount = 0;
                            s.Lag = 0;
                            s.MaxLatencyMs = 0;
                            totalDrop += s.DropFps;
                        }
                        // Compute per-channel lag and latency
                        foreach (var kv in _cameraToSlotIndex) {
                            if (_channelStats.TryGetValue(kv.Key, out var s)) {
                                var slot = _frameSlots[kv.Value];
                                s.Lag = slot.Data is null ? 0 : 1;
                                if (slot.ArrivalTimestamp > 0)
                                    s.MaxLatencyMs = (int)((now - slot.ArrivalTimestamp) / freq);
                            }
                        }

                        // Memory stats
                        using var proc = Process.GetCurrentProcess();
                        var workingSetMb = proc.WorkingSet64 / (1024 * 1024);
                        var gcHeapMb = GC.GetTotalMemory(false) / (1024 * 1024);

                        // Update perf panel overlay
                        if (_isPerfPanelVisible)
                            UpdatePerfPanel(totalFps, pipeLag, totalDrop, workingSetMb, gcHeapMb);
                    } else if (active > 0) {
                        FpsText.Text = $"{active}ch idle";
                    }
                };
                _fpsTimer.Start();
            }

            Timeline.PositionChanged += OnTimelinePositionChanged;
            Timeline.SelectionChanged += OnTimelineSelectionChanged;
            Timeline.BookmarkRequested += OnTimelineBookmarkRequested;
            Timeline.GoLiveRequested += OnTimelineGoLiveRequested;

            // Initial storage status update, refresh every 60s
            _ = UpdateStorageStatusAsync();
            if (_storageTimer is null) {
                _storageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
                _storageTimer.Tick += OnStorageTimerTick;
                _storageTimer.Start();
            }

            Log.Information("[DBG] PlaybackView Loaded END");
        } catch (Exception ex) {
            Log.Error(ex, "[DBG] PlaybackView Loaded CRASH: {Msg}", ex.Message);
        }
    }

    private void OnPlaybackUnloaded(object sender, RoutedEventArgs e) {
        if (!_isPlaybackLoaded) return;
        _isPlaybackLoaded = false;
        Log.Information("[DBG] PlaybackView Unloaded START");
        _queryCts?.Cancel();
        _clockTimer.Stop();
        // Cancel decode threads, return pending buffers, unsubscribe
        // CompositionTarget.Rendering to prevent frames from decode thread
        // being unconsumed by OnCompositionRendering causing pinned buffers
        StopAllPlayback();
        UnsubscribeCoordinator();
        Timeline.PositionChanged -= OnTimelinePositionChanged;
        Timeline.SelectionChanged -= OnTimelineSelectionChanged;
        Timeline.BookmarkRequested -= OnTimelineBookmarkRequested;
        Timeline.GoLiveRequested -= OnTimelineGoLiveRequested;
        ReturnPendingFrameBuffers();
        _storageTimer?.Stop();
        _storageTimer = null;
        _coordinator = null;
        var win = Window.GetWindow(this);
        win?.PreviewKeyDown -= OnPreviewKeyDown;
        Log.Information("[DBG] PlaybackView Unloaded END");
    }

    private void OnClockTick(object? sender, EventArgs e) => UpdateClock();

    private void UpdateClock() {
        var now = DateTime.Now;
        ClockDateText.Text = $"{now:yyyy-MM-dd} 週{DayNames[(int)now.DayOfWeek]}";
        ClockTimeText.Text = now.ToString("HH:mm:ss");
        FsClockText.Text = now.ToString("HH:mm:ss");
    }

    private async void OnStorageTimerTick(object? sender, EventArgs e) => await UpdateStorageStatusAsync();

    private async Task UpdateStorageStatusAsync() {
        try {
            var storagePath = _recordingService.GetBasePath();
            var info = await _videoIndexService.GetStorageInfoAsync(storagePath);

            if (info.TotalBytes > 0) {
                var usedGB = (info.TotalBytes - info.FreeBytes) / (1024.0 * 1024.0 * 1024.0);
                var totalGB = info.TotalBytes / (1024.0 * 1024.0 * 1024.0);
                var pct = (double)info.FreeBytes / info.TotalBytes * 100;
                var recordingGB = info.TotalRecordingBytes / (1024.0 * 1024.0 * 1024.0) > 0.1
                    ? $" / 錄影 {info.TotalRecordingBytes / (1024.0 * 1024.0 * 1024.0):F1} GB"
                    : "";

                StorageStatusText.Text = $"儲存 {usedGB:F1} / {totalGB:F0} GB ({pct:F0}%){recordingGB}";

                if (info.IsLowSpace) {
                    StorageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x44));
                } else
                    StorageStatusText.Foreground = FindResource("SecondaryTextBrush") as Brush
                        ?? new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

                StorageStatusText.Visibility = Visibility.Visible;
            } else {
                StorageStatusText.Visibility = Visibility.Collapsed;
            }
        } catch (Exception ex) {
            Log.Warning(ex, "[HeliVMS] UpdateStorageStatusAsync error");
            StorageStatusText.Visibility = Visibility.Collapsed;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Channel list population (64 channels from ChannelManagement)
    // ══════════════════════════════════════════════════════════

    private void PopulateChannelList() {
        Log.Information("[DBG] PopulateChannelList START");
        ChannelListPanel.Children.Clear();
        _channelItems.Clear();

        // Build CameraNumber→CameraId lookup table (for fallback)
        var allCameras = _cameraService.GetAllCameras();
        var cameraByChannel = new Dictionary<int, Camera>(allCameras.Count);
        for (var camIdx = 0; camIdx < allCameras.Count; camIdx++) {
            var c = allCameras[camIdx];
            if (c.ChannelNumber.HasValue) {
                cameraByChannel[c.ChannelNumber!.Value] = c;
            }
        }

        // Prefer getting 64-channel list from ChannelManagementPage
        var channelItems = ChannelManagementPage.CurrentChannels;

        if (channelItems is not null && channelItems.Count == 64) {
            Log.Information("[DBG] PopulateChannelList using ChannelManagementPage channels");
            foreach (var ch in channelItems) {
                var camera = ch.Camera;
                var item = new ChannelCheckBox {
                    CameraId = camera?.Id ?? "",
                    CameraName = ch.DisplayName,
                    ChannelNumber = ch.ChannelNumber,
                    HasCamera = camera is not null,
                    IsChecked = false
                };
                item.UpdateLabel();
                item.CheckChanged += OnChannelCheckChanged;
                _channelItems.Add(item);
                ChannelListPanel.Children.Add(item);
            }
        } else {
            Log.Information("[DBG] PopulateChannelList using CameraService fallback");
            // Fallback: build channel mapping from CameraService
            for (var i = 1; i <= 64; i++) {
                cameraByChannel.TryGetValue(i, out var cam);
                var item = new ChannelCheckBox {
                    CameraId = cam?.Id ?? "",
                    CameraName = cam?.Name ?? $"CH{i:D2}",
                    ChannelNumber = i,
                    HasCamera = cam is not null,
                    IsChecked = false
                };
                item.UpdateLabel();
                item.CheckChanged += OnChannelCheckChanged;
                _channelItems.Add(item);
                ChannelListPanel.Children.Add(item);
            }
        }

        // Build CameraId→ChannelCheckBox dictionary for O(1) lookup
        _cameraIdToChannelItem = new Dictionary<string, ChannelCheckBox>(_channelItems.Count);
        for (var ci = 0; ci < _channelItems.Count; ci++) {
            var ciItem = _channelItems[ci];
            if (!string.IsNullOrEmpty(ciItem.CameraId)) {
                _cameraIdToChannelItem.TryAdd(ciItem.CameraId, ciItem);
            }
        }

        // Auto-select assigned camera channels, prefer lower numbers
        Log.Information("[DBG] PopulateChannelList auto-check START");
        var autoChecked = 0;
        foreach (var item in _channelItems) {
            if (autoChecked >= MaxPlaybackChannels) break;
            if (!string.IsNullOrEmpty(item.CameraId)) {
                item.IsChecked = true;
                autoChecked++;
            }
        }
        Log.Information("[DBG] PopulateChannelList auto-check done, checked={Count}", autoChecked);

        UpdateChannelCount();
        Log.Information("[DBG] PopulateChannelList END");
    }

    private void OnChannelCheckChanged(string cameraId, bool isChecked) {
        UpdateChannelCount();
        UpdateButtonStates();
        ScheduleQuery(debounceMs: 150);
    }

    private void UpdateChannelCount() {
        int selected = 0, assigned = 0;
        for (var i = 0; i < _channelItems.Count; i++) {
            if (_channelItems[i].IsChecked) selected++;
            if (!string.IsNullOrEmpty(_channelItems[i].CameraId)) assigned++;
        }
        ChannelCountText.Text = $"{selected} / {MaxPlaybackChannels} 路";

        if (selected > MaxPlaybackChannels) {
            ChannelCountText.Foreground = Brushes.OrangeRed;
        } else {
            ChannelCountText.Foreground = (Brush)FindResource("SecondaryTextBrush");
        }

        // Update bottom status bar
        var total = _channelItems.Count;
        ChannelStatusText.Text = $"{total} 頻道 · {assigned} 已指派";
    }

    private void ChannelSearchBox_TextChanged(object sender, TextChangedEventArgs e) {
        var filter = ChannelSearchBox.Text?.Trim() ?? "";
        foreach (var item in _channelItems) {
            var chLabel = $"CH{item.ChannelNumber:D2}";
            var match = string.IsNullOrEmpty(filter) ||
                         chLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                         item.CameraName.Contains(filter, StringComparison.OrdinalIgnoreCase);
            item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Search panel
    // ══════════════════════════════════════════════════════════

    private void ShowSearchPanel(bool show) {
        SearchPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SearchBackdrop.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        ShowSearchPanel(false);
    }

    private void SearchToggleBtn_Click(object sender, RoutedEventArgs e) {
        var show = SearchPanel.Visibility != Visibility.Visible;
        ShowSearchPanel(show);

        if (show) {
            SearchFromDate.SelectedDate = FilterDatePicker.SelectedDate ?? DateTime.Today;
            SearchToDate.SelectedDate = FilterDatePicker.SelectedDate ?? DateTime.Today;
            _ = Dispatcher.BeginInvoke(() => {
                SearchFromDate.Focus();
                SearchFromHour.SelectAll();
            });
        }
    }

    private void CloseSearchBtn_Click(object sender, RoutedEventArgs e) {
        ShowSearchPanel(false);
    }

    private void SearchClearFiltersBtn_Click(object sender, RoutedEventArgs e) {
        SearchCameraFilter.Text = "";
        SearchTypeFilter.SelectedIndex = 0;
        SearchFromDate.SelectedDate = DateTime.Today;
        SearchFromHour.Text = "00";
        SearchFromMinute.Text = "00";
        SearchFromSecond.Text = "00";
        SearchToDate.SelectedDate = DateTime.Today;
        SearchToHour.Text = "23";
        SearchToMinute.Text = "59";
        SearchToSecond.Text = "59";
        SearchResultList.Children.Clear();
        SearchResultCount.Text = "0";
        SearchStatusText.Visibility = Visibility.Collapsed;
        _selectedSearchIndex = -1;
    }

    private void SearchPresetToday_Click(object sender, RoutedEventArgs e) {
        var today = DateTime.Today;
        var now = DateTime.Now;
        SearchFromDate.SelectedDate = today;
        SearchFromHour.Text = "00";
        SearchFromMinute.Text = "00";
        SearchFromSecond.Text = "00";
        SearchToDate.SelectedDate = today;
        SearchToHour.Text = now.Hour.ToString("D2");
        SearchToMinute.Text = now.Minute.ToString("D2");
        SearchToSecond.Text = now.Second.ToString("D2");
    }

    private void SearchPreset1H_Click(object sender, RoutedEventArgs e) {
        var now = DateTime.Now;
        var from = now.AddHours(-1);
        SearchFromDate.SelectedDate = from.Date;
        SearchFromHour.Text = from.Hour.ToString("D2");
        SearchFromMinute.Text = from.Minute.ToString("D2");
        SearchFromSecond.Text = from.Second.ToString("D2");
        SearchToDate.SelectedDate = now.Date;
        SearchToHour.Text = now.Hour.ToString("D2");
        SearchToMinute.Text = now.Minute.ToString("D2");
        SearchToSecond.Text = now.Second.ToString("D2");
    }

    private void SearchPreset6H_Click(object sender, RoutedEventArgs e) {
        var now = DateTime.Now;
        var from = now.AddHours(-6);
        SearchFromDate.SelectedDate = from.Date;
        SearchFromHour.Text = from.Hour.ToString("D2");
        SearchFromMinute.Text = from.Minute.ToString("D2");
        SearchFromSecond.Text = from.Second.ToString("D2");
        SearchToDate.SelectedDate = now.Date;
        SearchToHour.Text = now.Hour.ToString("D2");
        SearchToMinute.Text = now.Minute.ToString("D2");
        SearchToSecond.Text = now.Second.ToString("D2");
    }

    private void SearchPreset24H_Click(object sender, RoutedEventArgs e) {
        var now = DateTime.Now;
        var from = now.AddHours(-24);
        SearchFromDate.SelectedDate = from.Date;
        SearchFromHour.Text = from.Hour.ToString("D2");
        SearchFromMinute.Text = from.Minute.ToString("D2");
        SearchFromSecond.Text = from.Second.ToString("D2");
        SearchToDate.SelectedDate = now.Date;
        SearchToHour.Text = now.Hour.ToString("D2");
        SearchToMinute.Text = now.Minute.ToString("D2");
        SearchToSecond.Text = now.Second.ToString("D2");
    }

    private void SearchPresetWeek_Click(object sender, RoutedEventArgs e) {
        var now = DateTime.Now;
        var weekStart = now.Date.AddDays(-(int)now.DayOfWeek);
        SearchFromDate.SelectedDate = weekStart;
        SearchFromHour.Text = "00";
        SearchFromMinute.Text = "00";
        SearchFromSecond.Text = "00";
        SearchToDate.SelectedDate = now.Date;
        SearchToHour.Text = now.Hour.ToString("D2");
        SearchToMinute.Text = now.Minute.ToString("D2");
        SearchToSecond.Text = now.Second.ToString("D2");
    }

    private async void ExecuteSearchBtn_Click(object sender, RoutedEventArgs e) {
        await ExecuteSearchAsync();
    }

    private void SearchInput_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter) {
            e.Handled = true;
            _ = ExecuteSearchAsync();
        }
    }

    private async Task ExecuteSearchAsync() {
        var fromDate = SearchFromDate.SelectedDate;
        var toDate = SearchToDate.SelectedDate;
        if (fromDate is null || toDate is null) {
            SetSearchStatus("請選擇搜尋日期範圍。", true);
            return;
        }

        if (!int.TryParse(SearchFromHour.Text, out var fromH) || fromH < 0 || fromH > 23 ||
            !int.TryParse(SearchFromMinute.Text, out var fromM) || fromM < 0 || fromM > 59 ||
            !int.TryParse(SearchFromSecond.Text, out var fromS) || fromS < 0 || fromS > 59) {
            SetSearchStatus("起始時間格式錯誤 (時 0-23，分/秒 0-59)", true);
            return;
        }
        if (!int.TryParse(SearchToHour.Text, out var toH) || toH < 0 || toH > 23 ||
            !int.TryParse(SearchToMinute.Text, out var toM) || toM < 0 || toM > 59 ||
            !int.TryParse(SearchToSecond.Text, out var toS) || toS < 0 || toS > 59) {
            SetSearchStatus("結束時間格式錯誤 (時 0-23，分/秒 0-59)", true);
            return;
        }

        var startUtc = fromDate.Value.Date.AddHours(fromH).AddMinutes(fromM).AddSeconds(fromS);
        var endUtc = toDate.Value.Date.AddHours(toH).AddMinutes(toM).AddSeconds(toS);
        if (startUtc >= endUtc) {
            SetSearchStatus("起始時間必須早於結束時間。", true);
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        var filterText = SearchCameraFilter.Text?.Trim() ?? "";
        List<string>? cameraFilter = null;
        if (!string.IsNullOrEmpty(filterText)) {
            cameraFilter = [];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _channelItems.Count; i++) {
                var c = _channelItems[i];
                if (string.IsNullOrEmpty(c.CameraId)) continue;
                var chLabel = $"CH{c.ChannelNumber:D2}";
                if (!c.CameraName.Contains(filterText, StringComparison.OrdinalIgnoreCase) &&
                    !chLabel.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (seen.Add(c.CameraId)) {
                    cameraFilter.Add(c.CameraId);
                }
            }
            if (cameraFilter.Count == 0) {
                SetSearchStatus($"未找到符合「{filterText}」的頻道。", true);
                return;
            }
        } else {
            cameraFilter = [];
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _channelItems.Count; i++) {
                var c = _channelItems[i];
                if (c.IsChecked && !string.IsNullOrEmpty(c.CameraId) && seenIds.Add(c.CameraId)) {
                    cameraFilter.Add(c.CameraId);
                }
            }

            if (cameraFilter.Count == 0) {
                SetSearchStatus("請先勾選要搜尋的頻道，或輸入頻道編號/名稱篩選。", true);
                return;
            }
        }

        SetSearchStatus($"正在搜尋 {cameraFilter.Count} 個頻道…", false);
        ShowSearchLoading();
        _selectedSearchIndex = -1;
        _selectedSearchIndices.Clear();
        UpdateBatchExportButton();

        // Map ComboBox index to RecordType filter (-1 = all)
        var typeFilter = SearchTypeFilter.SelectedIndex - 1;

        try {
            List<VideoSegment> segments;

            if (SearchMotionFilter.IsChecked == true) {
                var motionResults = new List<VideoSegment>();
                foreach (var camId in cameraFilter) {
                    var camSegments = await _videoIndexService.QueryMotionSegmentsAsync(
                        camId, startUtc, endUtc, 0.4).ConfigureAwait(false);
                    motionResults.AddRange(camSegments);
                }
                segments = motionResults;
            } else {
                segments = await _videoIndexService
                    .QuerySegmentsByCamerasAsync(cameraFilter, startUtc, endUtc)
                    .ConfigureAwait(false);
            }

            if (token.IsCancellationRequested) return;

            var searchResults = new List<SearchResultEntry>(segments.Count);
            for (var si = 0; si < segments.Count; si++) {
                var s = segments[si];
                if (typeFilter >= 0 && s.RecordType != typeFilter) {
                    continue;
                }
                _cameraIdToChannelItem.TryGetValue(s.CameraId, out var chItem);
                searchResults.Add(new SearchResultEntry {
                    CameraId = s.CameraId,
                    ChannelNumber = chItem?.ChannelNumber ?? 0,
                    CameraName = chItem?.CameraName ?? s.CameraId,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime ?? s.StartTime,
                    RecordType = s.RecordType,
                    FilePath = s.FilePath ?? ""
                });
            }
            searchResults.Sort((a, b) => {
                var cmp = a.StartTime.CompareTo(b.StartTime);
                return cmp != 0 ? cmp : a.ChannelNumber.CompareTo(b.ChannelNumber);
            });
            var takeCount = Math.Min(500, searchResults.Count);
            _searchResults = new List<SearchResultEntry>(takeCount);
            for (var si = 0; si < takeCount; si++) {
                _searchResults.Add(searchResults[si]);
            }

            if (token.IsCancellationRequested) return;

            Dispatcher.Invoke(() => {
                HideSearchLoading();
                DisplaySearchResults();
            });
            _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                $"錄影搜尋完成：{_searchResults.Count} 筆結果",
                $"範圍: {startUtc:yyyy-MM-dd HH:mm} ~ {endUtc:yyyy-MM-dd HH:mm}");
        } catch (OperationCanceledException) { HideSearchLoading(); } catch (Exception ex) {
            HideSearchLoading();
            Log.Debug("[HeliVMS] Search error: {Msg}", ex.Message);
            SetSearchStatus($"搜尋失敗：{ex.Message}", true);
        }
    }

    private void DisplaySearchResults() {
        SearchResultList.Children.Clear();
        SearchResultCount.Text = _searchResults.Count.ToString();

        if (_searchResults.Count == 0) {
            var emptyMsg = string.IsNullOrEmpty(SearchCameraFilter.Text?.Trim())
                ? "無符合條件的錄影資料\n請確認所選時間範圍內有錄影、或調整類型篩選條件"
                : $"頻道「{SearchCameraFilter.Text}」在該時段無錄影資料";
            SearchResultList.Children.Add(new TextBlock {
                Text = emptyMsg,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                FontSize = 11,
                Margin = new Thickness(8, 12, 8, 12),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        var i = 0;
        foreach (var result in _searchResults) {
            var item = CreateSearchResultItem(result, i);
            SearchResultList.Children.Add(item);
            i++;
        }

        // Build type distribution summary
        int cont = 0, motion = 0, alarm = 0, manual = 0;
        for (var j = 0; j < _searchResults.Count; j++) {
            switch (_searchResults[j].RecordType) {
                case 0: cont++; break;
                case 1: motion++; break;
                case 2: alarm++; break;
                case 3: manual++; break;
            }
        }
        var parts = new List<string>();
        if (cont > 0) parts.Add($"連續 {cont}");
        if (motion > 0) parts.Add($"動態 {motion}");
        if (alarm > 0) parts.Add($"警報 {alarm}");
        if (manual > 0) parts.Add($"手動 {manual}");

        var typeInfo = parts.Count > 0 ? $"（{string.Join(" / ", parts)}）" : "";
        SetSearchStatus($"找到 {_searchResults.Count} 筆錄影段落{typeInfo}" +
            (_searchResults.Count >= 500 ? " (顯示前 500 筆，請縮小搜尋範圍)" : ""), false);
    }

    private Border CreateSearchResultItem(SearchResultEntry entry, int index) {
        var isSelected = index == _selectedSearchIndex;
        var isMultiSelected = _selectedSearchIndices.Contains(index);
        var border = new Border {
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(2, 1, 2, 1),
            CornerRadius = new CornerRadius(3),
            Cursor = Cursors.Hand,
            Tag = index,
            ToolTip = $"{entry.CameraName} · CH{entry.ChannelNumber:D2}\n{entry.TimeLabel} ~ {entry.EndTime:HH:mm:ss}\n{entry.DurationLabel}"
        };

        if (isMultiSelected) {
            border.Background = new SolidColorBrush(Color.FromArgb(40, 0x21, 0x96, 0xF3));
        } else if (isSelected) {
            border.Background = new SolidColorBrush(Color.FromArgb(60, 0x21, 0x96, 0xF3));
        }

        border.MouseEnter += (_, _) => {
            var idx = (int)border.Tag;
            if (!_selectedSearchIndices.Contains(idx) && idx != _selectedSearchIndex) {
                border.Background = (Brush)FindResource("SecondarySurfaceBrush");
            }
        };
        border.MouseLeave += (_, _) => {
            var idx = (int)border.Tag;
            if (_selectedSearchIndices.Contains(idx)) {
                border.Background = new SolidColorBrush(Color.FromArgb(40, 0x21, 0x96, 0xF3));
            } else if (idx == _selectedSearchIndex) {
                border.Background = new SolidColorBrush(Color.FromArgb(60, 0x21, 0x96, 0xF3));
            } else
                border.Background = Brushes.Transparent;
        };

        border.MouseLeftButtonUp += (_, _) =>
            OnSearchResultClicked(entry);

        var ctxMenu = new ContextMenu();

        if (_selectedSearchIndices.Contains(index)) {
            var deselectItem = new MenuItem { Header = "取消選取" };
            deselectItem.Click += (_, _) => ToggleSearchSelection(index);
            ctxMenu.Items.Add(deselectItem);
        } else {
            var selectItem = new MenuItem { Header = "選取此項" };
            selectItem.Click += (_, _) => ToggleSearchSelection(index);
            ctxMenu.Items.Add(selectItem);
        }
        ctxMenu.Items.Add(new Separator());

        var navItem = new MenuItem { Header = "跳轉至此時間" };
        navItem.Click += (_, _) => OnSearchResultClicked(entry);
        ctxMenu.Items.Add(navItem);

        var copyItem = new MenuItem { Header = "複製時間資訊" };
        copyItem.Click += (_, _) => {
            try {
                Clipboard.SetText(
                    $"{entry.CameraName}  CH{entry.ChannelNumber:D2}  {entry.StartTime:yyyy-MM-dd HH:mm:ss} ~ {entry.EndTime:HH:mm:ss}");
            } catch { }
        };
        ctxMenu.Items.Add(copyItem);

        var exportItem = new MenuItem { Header = "匯出此片段" };
        exportItem.Click += async (_, _) => await ExportSingleSegmentAsync(entry);
        ctxMenu.Items.Add(exportItem);
        border.ContextMenu = ctxMenu;

        var dotColor = entry.RecordType switch {
            1 => "RecordingAiBrush",    // Motion → Amber
            2 => "RecordingAlarmBrush", // Alarm → Red
            3 => "SuccessBrush",        // Manual → Green
            _ => "RecordingContinuousBrush" // Continuous → Blue
        };

        var innerGrid = new Grid();
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Selection checkbox indicator
        innerGrid.Children.Add(new Viewbox {
            Width = 12, Height = 12,
            Margin = new Thickness(0, 0, 4, 0),
            Child = new System.Windows.Shapes.Path {
                Data = isMultiSelected
                    ? Geometry.Parse("M3,6 L6,9 L11,3")   // check mark
                    : Geometry.Parse("M2,2 L10,2 L10,10 L2,10 Z"), // empty square
                Stroke = isMultiSelected
                    ? (Brush)FindResource("PrimaryBrush")
                    : (Brush)FindResource("SecondaryTextBrush"),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = isMultiSelected ? (Brush)FindResource("PrimaryBrush") : Brushes.Transparent
            }
        });

        innerGrid.Children.Add(new Border {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = TryFindResource(dotColor) as Brush ?? Application.Current?.TryFindResource(dotColor) as Brush ?? Brushes.Gray,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = entry.RecordType
        });

        innerGrid.Children.Add(new TextBlock {
            Text = $"CH{entry.ChannelNumber:D2}",
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        innerGrid.Children.Add(new TextBlock {
            Text = entry.TimeLabel,
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 10,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center
        });

        innerGrid.Children.Add(new TextBlock {
            Text = entry.DurationLabel,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        });

        innerGrid.Children[0].SetValue(Grid.ColumnProperty, 0);
        innerGrid.Children[1].SetValue(Grid.ColumnProperty, 1);
        innerGrid.Children[2].SetValue(Grid.ColumnProperty, 2);
        innerGrid.Children[3].SetValue(Grid.ColumnProperty, 3);
        innerGrid.Children.Add(new TextBlock {
            Text = entry.CameraName,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        });
        innerGrid.Children[4].SetValue(Grid.ColumnProperty, 4);

        border.Child = innerGrid;
        return border;
    }

    private void ToggleSearchSelection(int index) {
        if (!_selectedSearchIndices.Remove(index)) {
            _selectedSearchIndices.Add(index);
        }
        RefreshSearchResultsUI();
        UpdateBatchExportButton();
    }

    private void RefreshSearchResultsUI() {
        for (var i = 0; i < SearchResultList.Children.Count; i++) {
            if (SearchResultList.Children[i] is Border b && b.Tag is int idx && idx < _searchResults.Count) {
                var replacement = CreateSearchResultItem(_searchResults[idx], idx);
                SearchResultList.Children[i] = replacement;
            }
        }
    }

    private void UpdateBatchExportButton() {
        BatchExportBtn.Visibility = _selectedSearchIndices.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_selectedSearchIndices.Count > 0) {
            BatchExportBtn.Content = $"匯出選取 ({_selectedSearchIndices.Count})";
        }
    }

    private void SelectAllSearchResults() {
        for (var i = 0; i < _searchResults.Count; i++) {
            _selectedSearchIndices.Add(i);
        }
        RefreshSearchResultsUI();
        UpdateBatchExportButton();
    }

    private async void BatchExportBtn_Click(object sender, RoutedEventArgs e) {
        var selectedEntries = new List<SearchResultEntry>(_selectedSearchIndices.Count);
        foreach (var idx in _selectedSearchIndices) {
            if (idx >= 0 && idx < _searchResults.Count) {
                selectedEntries.Add(_searchResults[idx]);
            }
        }
        selectedEntries.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

        if (selectedEntries.Count == 0) return;

        if (selectedEntries.Count == 1) {
            await ExportSingleSegmentAsync(selectedEntries[0]);
            return;
        }

        // Multiple segments — use existing multi-export path
        var outputPath = ExportMultipleDialogHelper(selectedEntries);
        if (outputPath is not null) {
            await ExportMultipleSegmentsAsync(selectedEntries, outputPath);
        }
    }

    private static string? ExportMultipleDialogHelper(List<SearchResultEntry> entries) {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "HeliVMS_Export",
            $"Batch_{entries[0].StartTime:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task ExportMultipleSegmentsAsync(List<SearchResultEntry> entries, string outputDir) {
        var total = entries.Count;
        var success = 0;
        var fail = 0;

        for (var i = 0; i < total; i++) {
            var entry = entries[i];
            try {
                var srcPath = entry.FilePath;
                if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath)) {
                    var dir = Path.GetDirectoryName(entry.FilePath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) {
                        var files = Directory.GetFiles(dir, "*.ts");
                        if (files.Length > 0) srcPath = files[0];
                    }
                }

                if (!string.IsNullOrEmpty(srcPath) && File.Exists(srcPath)) {
                    var dstName = $"CH{entry.ChannelNumber:D2}_{entry.StartTime:yyyyMMdd_HHmmss}.ts";
                    var dstPath = Path.Combine(outputDir, dstName);
                    await Task.Run(() => File.Copy(srcPath, dstPath, overwrite: true));
                    success++;
                } else {
                    fail++;
                }
            } catch {
                fail++;
            }
        }

        _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
            $"批次匯出完成：{success}/{total} 個片段", $"路徑: {outputDir}");

        MessageBox.Show(
            $"批次匯出完成！\n\n成功：{success} / {total}\n失敗：{fail}\n\n儲存位置：{outputDir}",
            "批次匯出", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnSearchResultClicked(SearchResultEntry entry) {
        // Ctrl+Click toggles multi-select
        if (Keyboard.Modifiers == ModifierKeys.Control) {
            var idx = _searchResults.IndexOf(entry);
            if (idx >= 0) ToggleSearchSelection(idx);
            return;
        }

        var date = entry.StartTime.Date;
        FilterDatePicker.SelectedDate = date;
        _currentDate = date;

        foreach (var ch in _channelItems) {
            ch.IsChecked = ch.CameraId == entry.CameraId;
        }

        var clickedIndex = _searchResults.IndexOf(entry);
        if (clickedIndex >= 0 && clickedIndex != _selectedSearchIndex) {
            _selectedSearchIndex = clickedIndex;
            Dispatcher.Invoke(() => HighlightSearchSelection());
        }

        _ = OnSearchResultNavigateAsync(entry, date);
    }

    private void HighlightSearchSelection() {
        for (var i = 0; i < SearchResultList.Children.Count; i++) {
            if (SearchResultList.Children[i] is Border b) {
                if (b.Tag is int idx) {
                    if (idx == _selectedSearchIndex) {
                        b.Background = new SolidColorBrush(Color.FromArgb(40, 0x21, 0x96, 0xF3));
                    } else {
                        b.Background = Brushes.Transparent;
                    }
                }
            }
        }
    }

    private void UpdateSearchSelection(int newIndex) {
        if (newIndex < 0 || newIndex >= _searchResults.Count) return;
        _selectedSearchIndex = newIndex;
        HighlightSearchSelection();
    }

    private void ScrollSearchSelectionIntoView() {
        if (_selectedSearchIndex < 0) return;
        Border? element = null;
        for (var i = 0; i < SearchResultList.Children.Count; i++) {
            if (SearchResultList.Children[i] is Border b && b.Tag is int idx && idx == _selectedSearchIndex) { element = b; break; }
        }
        element?.BringIntoView();
    }

    private async Task OnSearchResultNavigateAsync(SearchResultEntry entry, DateTime _) {
        await QueryAndDisplayRecordings(showMessages: false, targetTime: entry.StartTime);

        _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
            $"搜尋結果跳轉：{entry.CameraName} {entry.StartTime:HH:mm:ss}");
    }

    private void SetSearchStatus(string message, bool isError) {
        SearchStatusText.Text = message;
        SearchStatusText.Foreground = isError
            ? (Brush)FindResource("ErrorBrush")
            : (Brush)FindResource("SecondaryTextBrush");
        SearchStatusText.Visibility = Visibility.Visible;
    }

    private void ShowSearchLoading(string text = "搜尋中…") {
        SearchLoadingText.Text = text;
        SearchLoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideSearchLoading() {
        SearchLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowVideoLoading(string text = "載入錄影中…") {
        VideoLoadingText.Text = text;
        VideoLoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideVideoLoading() {
        VideoLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private static async Task ExportSingleSegmentAsync(SearchResultEntry entry) {
        var saveDialog = new SaveFileDialog {
            Title = $"匯出片段 — {entry.CameraName} {entry.StartTime:HH:mm:ss}",
            FileName = $"Export_{entry.CameraName}_CH{entry.ChannelNumber:D2}_{entry.StartTime:yyyyMMdd_HHmmss}.ts",
            DefaultExt = ".ts",
            Filter = "TS 檔案 (*.ts)|*.ts|所有檔案 (*.*)|*.*"
        };
        if (saveDialog.ShowDialog() != true) return;

        try {
            if (!string.IsNullOrEmpty(entry.FilePath) && File.Exists(entry.FilePath)) {
                await Task.Run(() => File.Copy(entry.FilePath, saveDialog.FileName, overwrite: true));
                MessageBox.Show($"匯出完成！\n{entry.FilePath}\n→\n{saveDialog.FileName}",
                    "匯出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            } else {
                var dir = Path.GetDirectoryName(entry.FilePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) {
                    var tsFiles = Directory.GetFiles(dir, "*.ts");
                    if (tsFiles.Length > 0) {
                        await Task.Run(() => File.Copy(tsFiles[0], saveDialog.FileName, overwrite: true));
                        MessageBox.Show($"匯出完成！\n{tsFiles[0]}\n→\n{saveDialog.FileName}",
                            "匯出完成", MessageBoxButton.OK, MessageBoxImage.Information);
                    } else {
                        MessageBox.Show("找不到原始錄影檔案。", "錯誤",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                } else {
                    MessageBox.Show("找不到原始錄影檔案。", "錯誤",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        } catch (Exception ex) {
            MessageBox.Show($"匯出失敗：{ex.Message}", "錯誤",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Query recordings
    // ══════════════════════════════════════════════════════════

    private void FilterDatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e) {
        if (!FilterDatePicker.SelectedDate.HasValue) return;

        _currentDate = FilterDatePicker.SelectedDate.Value;

        if (_channelItems.Count == 0) return;

        ScheduleQuery(debounceMs: 100);
    }

    private void PrevDayBtn_Click(object sender, RoutedEventArgs e) {
        if (!FilterDatePicker.SelectedDate.HasValue) {
            FilterDatePicker.SelectedDate = DateTime.Today.AddDays(-1);
        } else {
            FilterDatePicker.SelectedDate = FilterDatePicker.SelectedDate.Value.AddDays(-1);
        }
        LoadTimeRangeBtn_Click(sender, e);
    }

    private void NextDayBtn_Click(object sender, RoutedEventArgs e) {
        if (!FilterDatePicker.SelectedDate.HasValue) {
            FilterDatePicker.SelectedDate = DateTime.Today.AddDays(1);
        } else {
            FilterDatePicker.SelectedDate = FilterDatePicker.SelectedDate.Value.AddDays(1);
        }
        if (FilterDatePicker.SelectedDate > DateTime.Today) {
            FilterDatePicker.SelectedDate = DateTime.Today;
        }
        LoadTimeRangeBtn_Click(sender, e);
    }

    private void LoadTimeRangeBtn_Click(object sender, RoutedEventArgs e) {
        if (!FilterDatePicker.SelectedDate.HasValue) {
            FilterDatePicker.SelectedDate = DateTime.Today;
            return;
        }

        _currentDate = FilterDatePicker.SelectedDate.Value;
        var hour = PlaybackHourCombo.SelectedIndex;
        if (hour < 0) hour = 0;

        var startTarget = _currentDate.Date.AddHours(hour);

        ShowVideoLoading("載入錄影中…");
        _ = LoadAndJumpToHourAsync(startTarget);
    }

    private async Task LoadAndJumpToHourAsync(DateTime targetTime) {
        try {
            await QueryAndDisplayRecordings(showMessages: false, targetTime: targetTime);
        } finally {
            HideVideoLoading();
        }
    }

    private void ScheduleQuery(int debounceMs = 150) {
        _queryCts?.Cancel();
        _queryCts = new CancellationTokenSource();
        var token = _queryCts.Token;
        _ = Task.Delay(debounceMs, token).ContinueWith(t => {
            if (t.IsCanceled || token.IsCancellationRequested) return;
            Dispatcher.InvokeAsync(() => QueryAndDisplayRecordings(showMessages: false));
        }, token, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
    }

    private async Task QueryAndDisplayRecordings(bool showMessages = true, DateTime? targetTime = null) {
        StopAllPlayback();

        if (!FilterDatePicker.SelectedDate.HasValue) {
            if (showMessages) {
                MessageBox.Show("請先選擇日期。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        var date = FilterDatePicker.SelectedDate.Value;
        _currentDate = date;

        // Get valid channels that are checked and have cameras assigned
        var checkedChannels = new List<ChannelCheckBox>();
        for (var i = 0; i < _channelItems.Count; i++) {
            if (_channelItems[i].IsChecked) {
                checkedChannels.Add(_channelItems[i]);
            }
        }
        if (checkedChannels.Count == 0) {
            if (showMessages) {
                MessageBox.Show("請先選擇至少一個頻道。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            ShowEmptyState();
            return;
        }

        var validChannels = new List<ChannelCheckBox>(checkedChannels.Count);
        for (var i = 0; i < checkedChannels.Count; i++) {
            if (!string.IsNullOrEmpty(checkedChannels[i].CameraId)) {
                validChannels.Add(checkedChannels[i]);
            }
        }
        if (validChannels.Count == 0) {
            if (showMessages) {
                MessageBox.Show(
                    "選取的頻道皆尚未指派攝影機。\n" +
                    "請先至「設備管理→頻道管理」將攝影機指派給頻道後再查詢。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            ShowEmptyState();
            return;
        }

        if (validChannels.Count < checkedChannels.Count) {
            var diff = checkedChannels.Count - validChannels.Count;
            _eventLog.LogWarning(EventCategories.Playback, "PlaybackView",
                $"查詢跳過 {diff} 個未指派頻道");
        }

        var cameras = new List<Camera>(Math.Min(validChannels.Count, MaxPlaybackChannels));
        for (var i = 0; i < validChannels.Count && cameras.Count < MaxPlaybackChannels; i++) {
            var cam = _cameraService.GetCameraById(validChannels[i].CameraId);
            if (cam is not null) {
                cameras.Add(cam);
            }
        }

        if (cameras.Count == 0) {
            if (showMessages) {
                MessageBox.Show(
                    "選取的頻道雖有攝影機資料，但無法載入。\n" +
                    "請確認系統資料庫狀態後重試。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            ShowEmptyState();
            return;
        }

        try {
            ShowVideoLoading("正在查詢錄影資料…");

            // Cleanup orphan segments: remove stale DB records from ffmpeg crash
            await _videoIndexService.CleanupOrphanSegmentsAsync();

            if (_coordinator is null) {
                _coordinator = new PlaybackCoordinator(_videoIndexService, _recordingService);
                SubscribeCoordinator();
            }

            _currentRecordings = await _coordinator.QueryAvailableRecordingsAsync(cameras, date);

            if (_currentRecordings.Count == 0) {
                HideVideoLoading();
                if (showMessages) {
                    MessageBox.Show($"指定日期 ({date:yyyy-MM-dd}) 無錄影資料。", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                ShowEmptyState();
                return;
            }

            // Build timeline data
            var cameraIds = new List<string>(_currentRecordings.Count);
            var allSegments = new List<VideoSegment>();
            var cameraNamesDict = new Dictionary<string, string>(_currentRecordings.Count);
            for (var ri = 0; ri < _currentRecordings.Count; ri++) {
                var r = _currentRecordings[ri];
                cameraIds.Add(r.CameraId);
                cameraNamesDict[r.CameraId] = r.CameraName;
                allSegments.AddRange(r.Segments);
            }

            Timeline.LoadSegments(cameraIds, allSegments, cameraNamesDict);
            Timeline.TimelineDay = date;
            UpdateTimelineRecordingStats();

            var savedBookmarks = _bookmarkService.LoadBookmarks(date);
            _bookmarks = savedBookmarks;
            Timeline.LoadBookmarks(_bookmarks);

            // Auto-select first recording time point
            VideoSegment? firstSegment = null;
            for (var ri = 0; ri < _currentRecordings.Count; ri++) {
                var segs = _currentRecordings[ri].Segments;
                for (var si = 0; si < segs.Count; si++) {
                    var s = segs[si];
                    if (s.StartTime.Date != date) continue;
                    if (firstSegment is null || s.StartTime < firstSegment.StartTime) {
                        firstSegment = s;
                    }
                }
            }

            if (firstSegment is not null) {
                var startOfDay = date;
                var fraction = (firstSegment.StartTime - startOfDay).TotalSeconds / 86400.0;
                Timeline.PositionSeconds = Math.Clamp(fraction * 86400, 0, 86400);
            }

            // Start playback (seek to targetTime if specified)
            var playTime = targetTime ?? firstSegment?.StartTime ?? date;
            await StartPlayback(date, playTime);
            HideVideoLoading();
            if (targetTime.HasValue) {
                var fraction = (targetTime.Value - date).TotalSeconds / 86400.0;
                Timeline.PositionSeconds = Math.Clamp(fraction * 86400, 0, 86400);
            }

            _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                $"查詢錄影：{_currentRecordings.Count} 頻道", $"日期: {date:yyyy-MM-dd}");
        } catch (Exception ex) {
            HideVideoLoading();
            Log.Debug("[HeliVMS] PlaybackView query error: {Msg}", ex.Message);
            if (showMessages) {
                MessageBox.Show($"查詢錄影資料失敗：{ex.Message}", "錯誤",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private static double[] CalculateHourlyActivity(List<VideoSegment> segments) {
        var hourly = new double[24];
        if (segments.Count == 0) return hourly;
        var dayStart = segments[0].StartTime.Date.Ticks;
        foreach (var seg in segments) {
            var endTime = seg.EndTime;
            if (endTime is null) continue;

            var startTicks = seg.StartTime.Ticks;
            var endTicks = endTime.Value.Ticks;
            if (endTicks <= startTicks) continue;

            var startHour = (int)((startTicks - dayStart) / TimeSpan.TicksPerHour);
            var endHour = (int)((endTicks - dayStart) / TimeSpan.TicksPerHour);
            startHour = Math.Clamp(startHour, 0, 23);
            endHour = Math.Clamp(endHour, 0, 23);

            for (var h = startHour; h <= endHour; h++) {
                var hourStartTicks = dayStart + h * TimeSpan.TicksPerHour;
                var hourEndTicks = hourStartTicks + TimeSpan.TicksPerHour;
                var overlapStart = startTicks > hourStartTicks ? startTicks : hourStartTicks;
                var overlapEnd = endTicks < hourEndTicks ? endTicks : hourEndTicks;
                if (overlapEnd > overlapStart) {
                    var minutes = (overlapEnd - overlapStart) / (double)TimeSpan.TicksPerMinute;
                    hourly[h] = Math.Min(1, hourly[h] + minutes / 60.0);
                }
            }
        }
        return hourly;
    }

    // ══════════════════════════════════════════════════════════
    //  Playback logic
    // ══════════════════════════════════════════════════════════

    private async Task StartPlayback(DateTime _, DateTime targetTime) {
        StopAllPlayback();

        if (_coordinator is null) return;
        ClearVideoGrid();

        var slots = new List<CameraPlaybackSlot>();
        var players = new List<PlaybackPlayer>();

        foreach (var recording in _currentRecordings) {
            // Find segment closest to target time
            VideoSegment? bestSegment = null;
            var segs = recording.Segments;
            for (var si = 0; si < segs.Count; si++) {
                var s = segs[si];
                if (s.StartTime <= targetTime && (!s.EndTime.HasValue || s.EndTime > targetTime)) {
                    if (bestSegment is null || s.StartTime < bestSegment.StartTime) {
                        bestSegment = s;
                    }
                }
            }

            if (bestSegment is null) {
                // Find nearest segment
                var bestDist = double.MaxValue;
                for (var si = 0; si < segs.Count; si++) {
                    var s = segs[si];
                    var dist = Math.Abs((s.StartTime - targetTime).TotalSeconds);
                    if (dist < bestDist) { bestDist = dist; bestSegment = s; }
                }
            }

            if (bestSegment is not null) {
                var camera = _cameraService.GetCameraById(recording.CameraId);
                if (camera is not null) {
                    slots.Add(new CameraPlaybackSlot {
                        Camera = camera,
                        Segment = bestSegment
                    });
                }
            }
        }

        if (slots.Count == 0) return;

        // Calculate grid layout
        var total = slots.Count;
        (_displayRows, _displayColumns) = CalculateGrid(total);
        SyncLayoutLabel();

        // Create PlaybackPlayer
        PlaybackGrid.RowDefinitions.Clear();
        PlaybackGrid.ColumnDefinitions.Clear();

        for (var r = 0; r < _displayRows; r++) {
            PlaybackGrid.RowDefinitions.Add(new RowDefinition());
        }
        for (var c = 0; c < _displayColumns; c++) {
            PlaybackGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        var compact = total >= 16;
        for (var i = 0; i < total; i++) {
            var slot = slots[i];
            var player = new PlaybackPlayer();
            player.SetChannelInfo(slot.Camera?.ChannelNumber ?? i + 1,
                slot.Camera?.Name ?? "", slot.Camera?.Id ?? "");
            if (compact) player.SetCompactMode(true);
            if (slot.Segment is not null) {
                player.SetRecordingType(slot.Segment.RecordType);
            }
            // First channel is sync master
            if (i == 0) {
                player.SetMaster(true);
            }
            player.MaximizeRequested += OnPlayerMaximizeRequested;
            player.PlayPauseRequested += OnPlayerPlayPauseRequested;
            player.Selected += OnPlayerSelected;
            player.SetAsMasterRequested += OnPlayerSetAsMasterRequested;
            player.Margin = new Thickness(1);
            player.SetLoading(true);

            Grid.SetRow(player, i / _displayColumns);
            Grid.SetColumn(player, i % _displayColumns);
            PlaybackGrid.Children.Add(player);
            _activePlayers.Add(player);
        }

        // Build slot index mapping (for lock-free frame exchange)
        _cameraToSlotIndex = new ConcurrentDictionary<string, int>(Environment.ProcessorCount, total);
        _frameSlots = new FrameSlot[total];
        _channelStats.Clear();
        for (var i = 0; i < total; i++) {
            var camId = slots[i].Camera?.Id;
            if (!string.IsNullOrEmpty(camId)) {
                _cameraToSlotIndex.TryAdd(camId, i);
                var cam = slots[i].Camera;
                _channelStats[camId] = new ChannelStats {
                    CameraId = camId,
                    CameraName = cam?.Name ?? camId,
                    ChannelNumber = cam?.ChannelNumber ?? i + 1,
                    IsMaster = i == 0
                };
            }
            _frameSlots[i] = new FrameSlot();
        }

        _isPlaying = true;

        // Set initial speed badge
        var currentRate = SliderValueToSpeed(SpeedSlider.Value);
        UpdatePlayerSpeedBadges(currentRate);

        EmptyPrompt.Visibility = Visibility.Collapsed;
        PlaybackGrid.Visibility = Visibility.Visible;

        // Load to coordinator
        _coordinator.LoadCameras(slots, targetTime);
        SubscribeCoordinator();
        UpdateButtonStates();
        UpdatePlayingCount();
        _frameDisplayCount = 0;
        FpsBadge.Visibility = Visibility.Visible;
        _fpsTimer?.Start();

        _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
            $"開始回放：{slots.Count} 路", $"時間: {targetTime:HH:mm:ss}");
    }

    private void SubscribeCoordinator() {
        if (_coordinator is null) return;

        _coordinator.ChannelFrameReady += OnCoordinatorChannelFrameReady;
        _coordinator.ChannelStatusChanged += OnCoordinatorChannelStatusChanged;
        _coordinator.MasterPositionChanged += OnCoordinatorMasterPositionChanged;
        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _coordinator.MasterEOFReached += OnCoordinatorMasterEOFReached;

        CompositionTarget.Rendering -= OnCompositionRendering;
        CompositionTarget.Rendering += OnCompositionRendering;
    }

    private void UnsubscribeCoordinator() {
        if (_coordinator is null) return;

        _coordinator.ChannelFrameReady -= OnCoordinatorChannelFrameReady;
        _coordinator.ChannelStatusChanged -= OnCoordinatorChannelStatusChanged;
        _coordinator.MasterPositionChanged -= OnCoordinatorMasterPositionChanged;
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _coordinator.MasterEOFReached -= OnCoordinatorMasterEOFReached;

        CompositionTarget.Rendering -= OnCompositionRendering;
    }

    private void OnCoordinatorChannelFrameReady(string cameraId, PooledBuffer frame) {
        if (!_cameraToSlotIndex.TryGetValue(cameraId, out var idx)) return;
        var slot = _frameSlots[idx];
        // Skip if previous frame not yet consumed by UI (no accumulation)
        var prev = Interlocked.CompareExchange(ref slot.Data, frame, null);
        if (prev is not null) {
            if (_channelStats.TryGetValue(cameraId, out var s))
                Interlocked.Increment(ref s.DropCount);
            frame.Dispose(); // slot busy: return this buffer to pool
            return;
        }
        slot.ArrivalTimestamp = Stopwatch.GetTimestamp();
    }

    private void OnCoordinatorChannelStatusChanged(string cameraId, bool playing) {
        _ = Dispatcher.BeginInvoke(() => {
            // ChannelStatusChanged fires from decode thread; _activePlayers (List)
            // is not thread-safe, must dispatch to UI thread
            PlaybackPlayer? player = null;
            for (var i = 0; i < _activePlayers.Count; i++) {
                if (_activePlayers[i].CameraId == cameraId) { player = _activePlayers[i]; break; }
            }
            if (player is not null) {
                if (playing) {
                    player.SetLoading(false);
                } else {
                    player.ShowNoSignal("連線失敗");
                }
            }
        });
    }

    private void OnCoordinatorMasterPositionChanged(long pts, long duration) {
        Log.Debug("[PlaybackView] MasterPositionChanged pts={Pts}us dur={Dur}us",
            pts, duration);
        _ = Dispatcher.BeginInvoke(() => {
            Log.Debug("[PlaybackView] Dispatch position update pts={Pts}us", pts);

            // Cache first valid duration, keep computing fraction even if 0
            if (duration > 0) {
                _lastKnownDuration = duration;
            }
            var effDuration = duration > 0 ? duration : _lastKnownDuration;
            if (effDuration > 0) {
                var fraction = (double)pts / effDuration;
                Timeline.SetPositionSilent(fraction * 86400);
            }
            UpdateTimeDisplay(pts, duration);
            UpdatePlayerTimestamps(pts);
            UpdatePlayerProgress(pts, effDuration);
        });
    }

    private void OnCoordinatorStateChanged(PlaybackState state) {
        _ = Dispatcher.BeginInvoke(() => {
            _isPlaying = state == PlaybackState.Playing;
            UpdateButtonStates();
        });
    }

    private void OnCoordinatorMasterEOFReached() {
        _ = Dispatcher.BeginInvoke(async () => await HandleMasterEOF());
    }

    private void OnCompositionRendering(object? sender, EventArgs e) {
        // Lock-free per-slot consume: atomically exchange pending frame per slot
        var count = _frameSlots.Length;
        var displayed = 0;
        for (var i = 0; i < count; i++) {
            var slot = _frameSlots[i];
            // Exchange slot.Data to null, retrieve frame if present
            var frame = Interlocked.Exchange(ref slot.Data, null);
            if (frame is null) continue;

            if (i < _activePlayers.Count) {
                var player = _activePlayers[i];
                player?.ShowFrame(frame);
                displayed++;
                if (player is not null && _channelStats.TryGetValue(player.CameraId, out var s)) {
                    s.DisplayCount++;
                    s.LastPtsMicroseconds = frame.PtsMicroseconds;
                }
            }
            frame.Dispose(); // return buffer to pool after frame consumed
        }
        if (displayed > 0) {
            _frameDisplayCount += displayed;
        }
    }

    private async Task HandleMasterEOF() {
        try {
            if (_currentRecordings.Count == 0 || _coordinator is null) return;

            var masterId = _coordinator.GetMasterId();
            if (masterId is null) return;

            var segmentEndTime = _coordinator.GetMasterSegmentEndTime();
            if (segmentEndTime >= DateTime.MaxValue) return;

            // Find next segment for each active channel
            var nextSegments = new Dictionary<string, VideoSegment>();
            var activeCameraIds = new HashSet<string>();
            foreach (var p in _activePlayers) activeCameraIds.Add(p.CameraId);

            foreach (var rec in _currentRecordings) {
                if (!activeCameraIds.Contains(rec.CameraId)) continue;

                VideoSegment? best = null;
                foreach (var s in rec.Segments) {
                    if (s.StartTime > segmentEndTime && s.EndTime is not null) {
                        if (best is null || s.StartTime < best.StartTime)
                            best = s;
                    }
                }
                if (best is not null)
                    nextSegments[rec.CameraId] = best;
            }

            if (nextSegments.Count > 0) {
                var firstSeg = nextSegments.Values.First();
                _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                    $"跨片段同步載入 {nextSegments.Count} 頻道",
                    $"時間: {firstSeg.StartTime:HH:mm:ss}");

                _coordinator.ReloadAllCameras(nextSegments, firstSeg.StartTime);
                return;
            }

            // No more segments today → try cross-day continuous playback
            await TryLoadNextDay();
        } catch (Exception ex) {
            Log.Error(ex, "[HeliVMS] HandleMasterEOF error");
        }
    }

    private async Task TryLoadNextDay() {
        var nextDate = _currentDate.AddDays(1);
        if (nextDate > DateTime.Today) {
            _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                "跨日播放：已達今日，無後續日期");

            _ = Dispatcher.BeginInvoke(() => PlaybackTimeText.Text = "播放完成");
            return;
        }

        // Use currently selected channels
        var checkedChannels = new List<ChannelCheckBox>();
        for (var i = 0; i < _channelItems.Count; i++) {
            var c = _channelItems[i];
            if (c.IsChecked && !string.IsNullOrEmpty(c.CameraId)) {
                checkedChannels.Add(c);
            }
        }
        if (checkedChannels.Count == 0) return;

        var cameras = new List<Camera>(Math.Min(checkedChannels.Count, MaxPlaybackChannels));
        for (var i = 0; i < checkedChannels.Count && cameras.Count < MaxPlaybackChannels; i++) {
            var cam = _cameraService.GetCameraById(checkedChannels[i].CameraId);
            if (cam is not null) {
                cameras.Add(cam);
            }
        }

        if (cameras.Count == 0) return;

        _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
            $"跨日播放：查詢 {nextDate:yyyy-MM-dd} 錄影");

        try {
            var nextRecordings = await _coordinator!
                .QueryAvailableRecordingsAsync(cameras, nextDate);

            if (nextRecordings.Count == 0) {
                _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                    $"跨日播放：{nextDate:yyyy-MM-dd} 無錄影");

                _ = Dispatcher.BeginInvoke(() => PlaybackTimeText.Text = "播放完成");
                return;
            }

            VideoSegment? firstSeg = null;
            for (var ri = 0; ri < nextRecordings.Count; ri++) {
                var riSegs = nextRecordings[ri].Segments;
                for (var si = 0; si < riSegs.Count; si++) {
                    var s = riSegs[si];
                    if (firstSeg is null || s.StartTime < firstSeg.StartTime) {
                        firstSeg = s;
                    }
                }
            }

            if (firstSeg is null) return;

            _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                $"跨日播放：繼續至 {nextDate:yyyy-MM-dd}", $"時間: {firstSeg.StartTime:HH:mm:ss}");

            await Dispatcher.InvokeAsync(async () => {
                _currentDate = nextDate;
                _currentRecordings = nextRecordings;

                // Update date picker (temporarily remove event to avoid query loop)
                FilterDatePicker.SelectedDateChanged -= FilterDatePicker_SelectedDateChanged;
                FilterDatePicker.SelectedDate = nextDate;
                FilterDatePicker.SelectedDateChanged += FilterDatePicker_SelectedDateChanged;

                // Update timeline
                var cameraIds = new List<string>(nextRecordings.Count);
                var allSegments = new List<VideoSegment>();
                var cameraNamesDict = new Dictionary<string, string>(nextRecordings.Count);
                for (var i = 0; i < nextRecordings.Count; i++) {
                    var r = nextRecordings[i];
                    cameraIds.Add(r.CameraId);
                    cameraNamesDict[r.CameraId] = r.CameraName;
                    allSegments.AddRange(r.Segments);
                }
                Timeline.LoadSegments(cameraIds, allSegments, cameraNamesDict);
                Timeline.TimelineDay = nextDate;
                UpdateTimelineRecordingStats();

                var nextBookmarks = _bookmarkService.LoadBookmarks(nextDate);
                _bookmarks = nextBookmarks;
                Timeline.LoadBookmarks(_bookmarks);

                var fraction = (firstSeg.StartTime - nextDate.Date).TotalSeconds / 86400.0;
                Timeline.PositionSeconds = Math.Clamp(fraction * 86400, 0, 86400);

                // Reload all channels for new date and continue playback
                StopAllPlayback();
                await StartPlayback(nextDate, firstSeg.StartTime);
            });
        } catch (Exception ex) {
            _eventLog.LogError(EventCategories.Playback, "PlaybackView",
                $"跨日播放查詢失敗", ex.ToString());

            _ = Dispatcher.BeginInvoke(() => PlaybackTimeText.Text = "跨日播放失敗");
        }
    }

    private void ReturnPendingFrameBuffers() {
        foreach (var slot in _frameSlots) {
            var frame = Interlocked.Exchange(ref slot.Data, null);
            frame?.Dispose(); // return pooled buffer
        }
    }

    private void StopAllPlayback() {
        _coordinator?.StopAll();
        UnsubscribeCoordinator();
        ReturnPendingFrameBuffers();
        ClearVideoGrid();
        _cameraToSlotIndex.Clear();
        _frameSlots = [];
        _isPlaying = false;
        PlayingCountBadge.Visibility = Visibility.Collapsed;
        FpsBadge.Visibility = Visibility.Collapsed;
        _fpsTimer?.Stop();
        _frameDisplayCount = 0;
        _channelStats.Clear();
        HidePerfPanel();
        UpdateButtonStates();
    }

    private void ShowPerfPanel() {
        _isPerfPanelVisible = true;
        PerfPanel.Visibility = Visibility.Visible;
        PerfToggleBtn.Foreground = FindResource("WarningBrush") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Orange;
    }

    private void HidePerfPanel() {
        _isPerfPanelVisible = false;
        PerfPanel.Visibility = Visibility.Collapsed;
        PerfToggleBtn.Foreground = FindResource("SecondaryTextBrush") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Gray;
    }

    private void UpdatePerfPanel(int totalFps, int pipeLag, int totalDrop, long workingSetMb, long gcHeapMb) {
        PerfSummaryText.Text = $"{totalFps}fps L:{pipeLag} D:{totalDrop}  MEM:{workingSetMb}MB GC:{gcHeapMb}MB";

        // Build stats rows
        PerfListPanel.Children.Clear();

        // Always include active channels
        foreach (var s in _channelStats.Values.OrderBy(s => s.ChannelNumber)) {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            // Indicator dot for master
            var chText = s.IsMaster ? "M" : $"{s.ChannelNumber:D2}";
            var chColor = s.IsMaster ? "WarningBrush" : "TextBrush";

            AddPerfCell(row, 0, chText, 9, chColor, HorizontalAlignment.Left);
            AddPerfCell(row, 1, s.CameraName, 9, s.DisplayFps > 0 ? "TextBrush" : "SecondaryTextBrush", HorizontalAlignment.Left);
            AddPerfCell(row, 2, s.DisplayFps.ToString(), 9,
                s.DisplayFps >= 10 ? "SuccessBrush" : s.DisplayFps > 0 ? "WarningBrush" : "SecondaryTextBrush",
                HorizontalAlignment.Right);
            AddPerfCell(row, 3, s.DropFps.ToString(), 9,
                s.DropFps > 10 ? "ErrorBrush" : s.DropFps > 0 ? "WarningBrush" : "SecondaryTextBrush",
                HorizontalAlignment.Right);
            AddPerfCell(row, 4, s.Lag.ToString(), 9,
                s.Lag > 0 ? "ErrorBrush" : "SecondaryTextBrush",
                HorizontalAlignment.Right);

            // Latency (ms)
            var latStr = s.MaxLatencyMs >= 100 ? ">99" : s.MaxLatencyMs.ToString();
            AddPerfCell(row, 5, latStr, 9,
                s.MaxLatencyMs > 50 ? "ErrorBrush" : s.MaxLatencyMs > 20 ? "WarningBrush" : "SecondaryTextBrush",
                HorizontalAlignment.Right);

            // PTS display
            var ptsStr = s.LastPtsMicroseconds > 0
                ? TimeSpan.FromMicroseconds(s.LastPtsMicroseconds).ToString(@"mm\:ss\.fff")
                : "--:--.---";
            AddPerfCell(row, 6, ptsStr, 8, "SecondaryTextBrush", HorizontalAlignment.Right);

            PerfListPanel.Children.Add(row);
        }
    }

    private void AddPerfCell(Grid grid, int col, string text, int fontSize, string keyOrColor, HorizontalAlignment align) {
        var brush = TryFindResource(keyOrColor) as Brush ?? Application.Current?.TryFindResource(keyOrColor) as Brush;
        if (brush is null && keyOrColor.StartsWith("#")) {
            brush = new SolidColorBrush(
                (System.Windows.Media.Color)ColorConverter.ConvertFromString(keyOrColor));
        }
        brush ??= System.Windows.Media.Brushes.Gray;
        var tb = new TextBlock {
            Text = text,
            FontSize = fontSize,
            FontFamily = new FontFamily("Consolas"),
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = align,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
    }

    private void PerfToggleBtn_Click(object sender, RoutedEventArgs e) {
        if (_isPerfPanelVisible)
            HidePerfPanel();
        else
            ShowPerfPanel();
    }

    private void ClosePerfBtn_Click(object sender, RoutedEventArgs e) {
        HidePerfPanel();
    }

    private void ExportPerfBtn_Click(object sender, RoutedEventArgs e) {
        var dialog = new SaveFileDialog {
            Title = "匯出效能統計",
            Filter = "CSV 檔案 (*.csv)|*.csv",
            FileName = $"perf_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        try {
            using var sw = new StreamWriter(dialog.FileName, false, System.Text.Encoding.UTF8);
            sw.WriteLine("ChannelNumber,CameraId,CameraName,IsMaster,DisplayFps,DropFps,Lag,MaxLatencyMs,LastPtsMicroseconds");
            foreach (var s in _channelStats.Values.OrderBy(s => s.ChannelNumber)) {
                sw.WriteLine($"{s.ChannelNumber},{EscapeCsv(s.CameraId)},{EscapeCsv(s.CameraName)},{s.IsMaster},{s.DisplayFps},{s.DropFps},{s.Lag},{s.MaxLatencyMs},{s.LastPtsMicroseconds}");
            }
        } catch (Exception ex) {
            Log.Error(ex, "[PerfPanel] CSV export failed");
        }
    }

    private static string EscapeCsv(string value) {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private void UpdatePlayingCount() {
        var count = _activePlayers.Count;
        if (count > 0) {
            PlayingCountText.Text = $"▶ {count} 路";
            PlayingCountBadge.Visibility = Visibility.Visible;

            // Sync indicator: show master channel + total count
            var masterId = _coordinator?.GetMasterId();
            if (masterId is not null && count > 1) {
                var masterPlayer = _activePlayers.FirstOrDefault(p => p.CameraId == masterId);
                var masterCh = masterPlayer?.ChannelNumber ?? 0;
                SyncIndicatorText.Text = $"> CH{masterCh:D2} + {count - 1} 路";
                SyncIndicator.Visibility = Visibility.Visible;
            } else {
                SyncIndicator.Visibility = Visibility.Collapsed;
            }
        } else {
            PlayingCountBadge.Visibility = Visibility.Collapsed;
            SyncIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void ClearVideoGrid() {
        foreach (var player in _activePlayers) {
            player.ClearDisplay();
        }
        _activePlayers.Clear();
        PlaybackGrid.Children.Clear();
        PlaybackGrid.Visibility = Visibility.Collapsed;
        EmptyPrompt.Visibility = Visibility.Visible;
    }

    // ══════════════════════════════════════════════════════════
    //  Playback control buttons
    // ══════════════════════════════════════════════════════════

    private void PlayPauseBtn_Click(object sender, RoutedEventArgs e) {
        if (_isPlaying) {
            _coordinator?.Pause();
            _isPlaying = false;
        } else {
            _coordinator?.Play();
            _isPlaying = true;
        }
        UpdateButtonStates();
    }

    private void LiveBtn_Checked(object sender, RoutedEventArgs e) {
        ToggleLiveMode();
    }

    private void LiveBtn_Unchecked(object sender, RoutedEventArgs e) {
        _liveMode = false;
    }

    private void ToggleLiveMode() {
        _liveMode = true;
        LiveBtn.IsChecked = true;
        var nowSecs = (DateTime.Now - DateTime.Today).TotalSeconds;
        Timeline.PositionSeconds = Math.Clamp(nowSecs, 0, 86400);
        Timeline_SeekRequested(Math.Clamp(nowSecs, 0, 86400));
        UpdateButtonStates();
    }

    private void SyncBtn_Checked(object sender, RoutedEventArgs e) {
        if (_syncEnabled) return;
        _syncEnabled = true;
        if (_coordinator is not null) {
            var masterId = _coordinator.GetMasterId();
            if (masterId is not null) {
                foreach (var player in _activePlayers) {
                    if (player.CameraId != masterId) {
                        player.SetMaster(false);
                    }
                }
            }
        }
    }

    private void SyncBtn_Unchecked(object sender, RoutedEventArgs e) {
        _syncEnabled = false;
    }

    private void ClndBtn_Click(object sender, RoutedEventArgs e) {
        ClndCalendar.DisplayDate = FilterDatePicker.SelectedDate ?? DateTime.Today;
        CalendarPopup.IsOpen = true;
    }

    private void ClndCalendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e) {
        if (ClndCalendar.SelectedDate.HasValue) {
            FilterDatePicker.SelectedDate = ClndCalendar.SelectedDate.Value;
            CalendarPopup.IsOpen = false;
            FilterDatePicker_SelectedDateChanged(FilterDatePicker, null!);
        }
    }

    private void ClndCalendar_Today_Click(object sender, RoutedEventArgs e) {
        FilterDatePicker.SelectedDate = DateTime.Today;
        CalendarPopup.IsOpen = false;
        FilterDatePicker_SelectedDateChanged(FilterDatePicker, null!);
    }

    private void ThmbBtn_Checked(object sender, RoutedEventArgs e) {
        Timeline.ToggleThumbnails(true);
    }

    private void ThmbBtn_Unchecked(object sender, RoutedEventArgs e) {
        Timeline.ToggleThumbnails(false);
    }

    private void OnTimelineGoLiveRequested(object? sender, EventArgs e) {
        ToggleLiveMode();
    }

    private void StopAllBtn_Click(object sender, RoutedEventArgs e) {
        StopAllPlayback();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        var speed = SliderValueToSpeed(SpeedSlider.Value);
        _coordinator?.SetPlaybackRate(speed);
        UpdatePlayerSpeedBadges(speed);
        ShowSpeedOSDOnPlayers(speed);
        SpeedLabel.Text = SpeedToDisplayText(speed);
        UpdateFsSpeedDisplay();
    }

    private double SliderValueToSpeed(double sliderVal) {
        if (!_isPlaying) return SliderValueToSpeedPaused(sliderVal);
        if (sliderVal == 50.0) return 1.0;
        var offset = (sliderVal - 50.0) / 12.5;
        return Math.Round(Math.Pow(2, offset), 2);
    }

    private static double SliderValueToSpeedPaused(double sliderVal) {
        if (sliderVal == 50.0) return 0.0;
        var sign = sliderVal > 50.0 ? 1.0 : -1.0;
        var offset = Math.Abs(sliderVal - 50.0) / 16.6667;
        return Math.Round(sign * 0.25 * Math.Pow(2, offset), 2);
    }

    private static string SpeedToDisplayText(double speed) {
        if (speed == 0.0) return "0x";
        var prefix = speed < 0 ? "-" : "";
        var abs = Math.Abs(speed);
        return abs >= 1.0
            ? $"{prefix}{(int)abs}x"
            : $"{prefix}{abs:F2}".TrimEnd('0').TrimEnd('.') + "x";
    }

    private void ShowSpeedOSDOnPlayers(double rate) {
        var text = SpeedToDisplayText(rate);
        foreach (var player in _activePlayers) {
            player.ShowSpeedOSD(text);
        }
    }

    private void UpdatePlayerSpeedBadges(double rate) {
        var text = SpeedToDisplayText(rate);
        foreach (var player in _activePlayers) {
            player.SetSpeedText(text);
        }
    }



    private async void JumpPrevRecBtn_Click(object sender, RoutedEventArgs e) {
        try { await JumpToAdjacentSegment(direction: -1); } catch (Exception ex) { Log.Debug("[HeliVMS] JumpPrevRec error: {Msg}", ex.Message); }
    }

    private async void JumpNextRecBtn_Click(object sender, RoutedEventArgs e) {
        try { await JumpToAdjacentSegment(direction: 1); } catch (Exception ex) { Log.Debug("[HeliVMS] JumpNextRec error: {Msg}", ex.Message); }
    }

    private async Task JumpToAdjacentSegment(int direction) {
        if (_coordinator is null || _currentRecordings.Count == 0) return;

        var masterId = _coordinator.GetMasterId();
        if (masterId is null) return;

        CameraRecordingInfo? masterRec = null;
        for (var ri = 0; ri < _currentRecordings.Count; ri++) {
            if (_currentRecordings[ri].CameraId == masterId) { masterRec = _currentRecordings[ri]; break; }
        }
        if (masterRec?.Segments is null || masterRec.Segments.Count == 0) return;

        // Calculate current time (from master position + segment start offset)
        var segmentStart = _coordinator.GetMasterSegmentStartTime();
        var posUs = _coordinator.GetMasterPosition();
        var currentTime = segmentStart.AddMicroseconds(posUs);

        var sorted = new List<VideoSegment>(masterRec.Segments.Count);
        for (var si = 0; si < masterRec.Segments.Count; si++) {
            var s = masterRec.Segments[si];
            if (s.StartTime.Date == _currentDate.Date) {
                sorted.Add(s);
            }
        }
        sorted.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

        if (sorted.Count == 0) return;

        // Find current segment index
        var curIdx = -1;
        for (var i = 0; i < sorted.Count; i++) {
            var seg = sorted[i];
            if (seg.StartTime <= currentTime &&
                (!seg.EndTime.HasValue || seg.EndTime > currentTime)) {
                curIdx = i;
                break;
            }
        }

        if (curIdx < 0) {
            // Not in any segment, find nearest one
            var bestDist = double.MaxValue;
            for (var i = 0; i < sorted.Count; i++) {
                var dist = Math.Abs((sorted[i].StartTime - currentTime).TotalSeconds);
                if (dist < bestDist) { bestDist = dist; curIdx = i; }
            }
        }

        var targetIdx = curIdx + direction;
        if (targetIdx < 0 || targetIdx >= sorted.Count) return;

        var targetSeg = sorted[targetIdx];
        var targetTime = direction < 0 ? targetSeg.StartTime
            : (targetSeg.StartTime > currentTime ? targetSeg.StartTime : targetSeg.StartTime);

        // Jump to target segment
        var fraction = (targetSeg.StartTime - _currentDate.Date).TotalSeconds / 86400.0;
        Timeline.PositionSeconds = Math.Clamp(fraction * 86400, 0, 86400);
        _coordinator.ReloadMasterCamera(targetSeg, targetSeg.StartTime);
        await ReloadIfMasterSegmentExpired(targetSeg.StartTime);

        var dirLabel = direction > 0 ? "Next" : "Prev";
        _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
            $"跳轉片段 ({dirLabel}): CH{masterRec.ChannelNumber:D2} {targetSeg.StartTime:HH:mm:ss}");
    }

    private void StepBackBtn_Click(object sender, RoutedEventArgs e) {
        _coordinator?.StepBackward();
    }

    private void StepFwdBtn_Click(object sender, RoutedEventArgs e) {
        _coordinator?.StepForward();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) {
        // Play All shortcut: Ctrl+P (works even without active players)
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control) {
            PlayAllBtn_Click(sender, e);
            e.Handled = true;
            return;
        }

        // Search panel shortcuts (work even without active players)
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) {
            ShowSearchPanel(SearchPanel.Visibility != Visibility.Visible);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && SearchPanel.Visibility == Visibility.Visible) {
            ShowSearchPanel(false);
            e.Handled = true;
            return;
        }

        // Performance monitor toggle: Ctrl+M
        if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control) {
            if (_isPerfPanelVisible)
                HidePerfPanel();
            else
                ShowPerfPanel();
            e.Handled = true;
            return;
        }

        // Search result keyboard navigation
        if (SearchPanel.Visibility == Visibility.Visible && _searchResults.Count > 0) {
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) {
                SelectAllSearchResults();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down && _selectedSearchIndex < _searchResults.Count - 1) {
                UpdateSearchSelection(_selectedSearchIndex + 1);
                ScrollSearchSelectionIntoView();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Up && _selectedSearchIndex > 0) {
                UpdateSearchSelection(_selectedSearchIndex - 1);
                ScrollSearchSelectionIntoView();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && _selectedSearchIndex >= 0 && _selectedSearchIndex < _searchResults.Count) {
                OnSearchResultClicked(_searchResults[_selectedSearchIndex]);
                e.Handled = true;
                return;
            }
        }

        if (_activePlayers.Count == 0) return;

        switch (e.Key) {
            case Key.Space:
                PlayPauseBtn_Click(sender, e);
                e.Handled = true;
                break;

            case Key.Left when Keyboard.Modifiers == ModifierKeys.Control:
                _coordinator?.SeekToStart();
                Timeline.PositionSeconds = 0;
                e.Handled = true;
                break;

            case Key.Right when Keyboard.Modifiers == ModifierKeys.Control:
                _coordinator?.SeekToEnd();
                Timeline.PositionSeconds = 86400;
                e.Handled = true;
                break;

            case Key.Left when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                _ = JumpToAdjacentSegment(direction: -1);
                e.Handled = true;
                break;

            case Key.Right when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                _ = JumpToAdjacentSegment(direction: 1);
                e.Handled = true;
                break;

            case Key.Left:
                _coordinator?.StepBackward();
                e.Handled = true;
                break;

            case Key.Right:
                _coordinator?.StepForward();
                e.Handled = true;
                break;

            case Key.OemComma:
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    _coordinator?.StepBackward(10);
                else
                    _coordinator?.StepBackward();
                e.Handled = true;
                break;

            case Key.OemPeriod:
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    _coordinator?.StepForward(10);
                else
                    _coordinator?.StepForward();
                e.Handled = true;
                break;



            case Key.B when Keyboard.Modifiers == ModifierKeys.None:
                AddBookmarkAtCurrentPosition();
                e.Handled = true;
                break;

            case Key.G when Keyboard.Modifiers == ModifierKeys.Control:
                ShowGoToTimeDialog();
                e.Handled = true;
                break;

            case Key.H:
            case Key.OemQuestion:
                ToggleShortcutsOverlay();
                e.Handled = true;
                break;

            case Key.Escape when ShortcutsOverlay.Visibility == Visibility.Visible:
                ToggleShortcutsOverlay();
                e.Handled = true;
                break;

            case Key.Up:
                SpeedSlider.Value = Math.Min(SpeedSlider.Value + 12.5, 100);
                e.Handled = true;
                break;

            case Key.Down:
            case Key.OemMinus:
            case Key.Subtract:
                SpeedSlider.Value = Math.Max(SpeedSlider.Value - 12.5, 0);
                e.Handled = true;
                break;

            case Key.OemPlus:
            case Key.Add:
                SpeedSlider.Value = Math.Min(SpeedSlider.Value + 12.5, 100);
                e.Handled = true;
                break;

            // Direct speed selection via number keys (1-4 = 1x, 2x, 4x, 8x)
            case Key.D1:
            case Key.NumPad1:
                SpeedSlider.Value = 50; // 1x
                e.Handled = true;
                break;
            case Key.D2:
            case Key.NumPad2:
                SpeedSlider.Value = 75; // 2x
                e.Handled = true;
                break;
            case Key.D3:
            case Key.NumPad3:
                SpeedSlider.Value = 87; // 4x
                e.Handled = true;
                break;
            case Key.D4:
            case Key.NumPad4:
                SpeedSlider.Value = 94; // 8x
                e.Handled = true;
                break;

            // ── Full-screen ──
            case Key.F11:
                ToggleFullScreen();
                e.Handled = true;
                break;

            case Key.Escape when _isFullScreen:
                ToggleFullScreen();
                e.Handled = true;
                break;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Timeline interaction
    // ══════════════════════════════════════════════════════════

    private void OnTimelinePositionChanged(object? sender, double seconds) {
        if (_liveMode) {
            _liveMode = false;
            LiveBtn.IsChecked = false;
        }
        var totalSecs = (int)seconds;
        var h = totalSecs / 3600;
        var m = (totalSecs % 3600) / 60;
        var s = totalSecs % 60;
        PlaybackTimeText.Text = $"{h:D2}:{m:D2}:{s:D2} / --:--:--";
        FsTimeText.Text = $"{h:D2}:{m:D2}:{s:D2}";
    }

    private void OnTimelineSelectionChanged(object? sender, (DateTime start, DateTime end)? selection) {
        // ExportClipBtn was removed in Task 2
    }

    private void OnTimelineBookmarkRequested(object? sender, EventArgs e) {
        AddBookmarkAtCurrentPosition();
    }

    private void ToggleTypeFilter(object sender, RoutedEventArgs e) {
        if (sender is ToggleButton btn && btn.Tag is string tagStr && int.TryParse(tagStr, out var typeIndex)) {
            var cont = ToggleContinuous.IsChecked ?? true;
            var mot = ToggleMotion.IsChecked ?? true;
            var alarm = ToggleAlarm.IsChecked ?? true;
            var ai = ToggleAi.IsChecked ?? true;
            Timeline.SetTypeFilter(cont, mot, alarm, ai);
        }
        UpdateFilterButtonOpacity();
    }

    private void UpdateFilterButtonOpacity() {
        SetBtnOpacity(ToggleContinuous, ToggleContinuous.IsChecked ?? true);
        SetBtnOpacity(ToggleMotion, ToggleMotion.IsChecked ?? true);
        SetBtnOpacity(ToggleAlarm, ToggleAlarm.IsChecked ?? true);
        SetBtnOpacity(ToggleAi, ToggleAi.IsChecked ?? true);
    }

    private static void SetBtnOpacity(ToggleButton btn, bool enabled) {
        btn.Opacity = enabled ? 1.0 : 0.25;
    }

    private void UpdateTimelineZoomLabel() {
        var field = typeof(NxTimeline).GetField("_zoomIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field?.GetValue(Timeline) is int idx) {
            var levelsField = typeof(NxTimeline).GetField("ZoomLevels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (levelsField?.GetValue(null) is double[] levels && idx < levels.Length) {
                var hours = levels[idx];
                TimelineZoomLabel.Text = hours >= 1 ? $"{hours:F0}h" : $"{hours * 60:F0}m";
            }
        }
    }

    private void TimelineZoomInBtn_Click(object sender, RoutedEventArgs e) {
        Timeline.ZoomIn();
        UpdateTimelineZoomLabel();
    }

    private void TimelineZoomOutBtn_Click(object sender, RoutedEventArgs e) {
        Timeline.ZoomOut();
        UpdateTimelineZoomLabel();
    }

    private void TimelineResetZoomBtn_Click(object sender, RoutedEventArgs e) {
        var field = typeof(NxTimeline).GetField("_zoomIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field is not null) {
            field.SetValue(Timeline, 0);
        }
        var viewStartField = typeof(NxTimeline).GetField("_viewStartSeconds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (viewStartField is not null) {
            viewStartField.SetValue(Timeline, 0.0);
        }
        TimelineZoomLabel.Text = "24h";
        Timeline.Refresh();
    }

    private void Timeline_SeekRequested(double seconds) {
        if (_coordinator is null) return;

        var targetTime = _currentDate.Date.AddSeconds(seconds);
        var microseconds = (long)(seconds * 1_000_000);
        _coordinator.SeekAbsolute(microseconds);

        _ = ReloadIfMasterSegmentExpired(targetTime);
    }

    private void UpdateTimelineRecordingStats() {
        if (_currentRecordings is null || _currentRecordings.Count == 0) {
            TimelineStatsText.Text = "";
            return;
        }
        var totalSegs = 0;
        var contSegs = 0;
        var motSegs = 0;
        var alarmSegs = 0;
        var aiSegs = 0;
        foreach (var rec in _currentRecordings) {
            foreach (var seg in rec.Segments) {
                totalSegs++;
                if (seg.RecordType == 0) contSegs++;
                else if (seg.RecordType == 1) motSegs++;
                else if (seg.RecordType == 2) alarmSegs++;
                else if (seg.RecordType == 3) aiSegs++;
            }
        }
        if (totalSegs == 0) { TimelineStatsText.Text = ""; return; }
        var parts = new List<string>();
        if (contSegs > 0) parts.Add($"連續{contSegs}");
        if (motSegs > 0) parts.Add($"位移{motSegs}");
        if (alarmSegs > 0) parts.Add($"警報{alarmSegs}");
        if (aiSegs > 0) parts.Add($"AI{aiSegs}");
        TimelineStatsText.Text = $"{totalSegs} 區段 ({string.Join(" / ", parts)})";
    }

    private void Timeline_PositionChanged(double seconds) {
        // Real-time time display update (while dragging)
        var totalSecs = (int)seconds;
        var h = totalSecs / 3600;
        var m = (totalSecs % 3600) / 60;
        var s = totalSecs % 60;
        PlaybackTimeText.Text = $"{h:D2}:{m:D2}:{s:D2} / --:--:--";
        FsTimeText.Text = $"{h:D2}:{m:D2}:{s:D2}";
    }

    // ══════════════════════════════════════════════════════════
    //  Bookmarks
    // ══════════════════════════════════════════════════════════

    private void OnBookmarkJumpRequested(double seconds) {
        if (_coordinator is null) return;
        var targetTime = _currentDate.Date.AddSeconds(seconds);
        var microseconds = (long)(seconds * 1_000_000);
        _coordinator.SeekAbsolute(microseconds);
        _ = ReloadIfMasterSegmentExpired(targetTime);
    }

    private void BookmarkBtn_Click(object sender, RoutedEventArgs e) {
        AddBookmarkAtCurrentPosition();
    }

    private void ShowBookmarkFeedback(string symbol) {
        BookmarkIcon.Visibility = Visibility.Collapsed;
        BookmarkFeedback.Text = symbol;
        BookmarkFeedback.Visibility = Visibility.Visible;
        var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        resetTimer.Tick += (_, _) => { resetTimer.Stop(); ResetBookmarkIcon(); };
        resetTimer.Start();
    }

    private void ResetBookmarkIcon() {
        BookmarkIcon.Visibility = Visibility.Visible;
        BookmarkFeedback.Visibility = Visibility.Collapsed;
    }

    private void AddBookmarkAtCurrentPosition() {
        if (_coordinator is null) return;

        var seconds = Timeline.PositionSeconds;
        var totalSec = (int)seconds;
        var h = totalSec / 3600;
        var m = (totalSec % 3600) / 60;
        var s_val = totalSec % 60;

        var exists = false;
        for (var bi = 0; bi < _bookmarks.Count; bi++) {
            if (Math.Abs(_bookmarks[bi].Seconds - seconds) < 5) { exists = true; break; }
        }
        if (exists) {
            ShowBookmarkFeedback("\u2713");
            return;
        }

        var note = $"CH{_coordinator.GetMasterId()?[..4] ?? "----"} {h:D2}:{m:D2}:{s_val:D2}";
        var newBm = new PlaybackBookmark { Seconds = seconds, Note = note };
        _bookmarks.Add(newBm);
        Timeline.AddBookmark(newBm);
        RefreshBookmarkList();

        var lastBm = _bookmarks.Count > 0 ? _bookmarks[^1] : null;
        if (lastBm is not null) {
            _bookmarkService.SaveBookmark(lastBm, _currentDate);
        }

        ShowBookmarkFeedback("\u2605");

        _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
            $"新增書籤", $"時間: {h:D2}:{m:D2}:{s_val:D2}");
    }

    private void ClearBookmarksBtn_Click(object sender, RoutedEventArgs e) {
        _bookmarks.Clear();
        Timeline.LoadBookmarks(_bookmarks);
        _bookmarkService.ClearBookmarks(_currentDate);
        RefreshBookmarkList();
    }

    private void RefreshBookmarkList() {
        BookmarkListPanel.Children.Clear();
        BookmarkCountBadge.Text = _bookmarks.Count.ToString();

        if (_bookmarks.Count == 0) {
            BookmarkListPanel.Children.Add(new TextBlock {
                Text = "暫無書籤",
                FontSize = 10,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                Margin = new Thickness(6, 8, 0, 0)
            });
            return;
        }

        var sortedBookmarks = new List<PlaybackBookmark>(_bookmarks.Count);
        for (var bi = 0; bi < _bookmarks.Count; bi++) {
            sortedBookmarks.Add(_bookmarks[bi]);
        }
        sortedBookmarks.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
        foreach (var bm in sortedBookmarks) {
            var totalSec = (int)bm.Seconds;
            var h = totalSec / 3600;
            var m = (totalSec % 3600) / 60;
            var s = totalSec % 60;

            var itemBorder = new Border {
                Margin = new Thickness(2, 1, 2, 0),
                Padding = new Thickness(4, 3, 4, 3),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            var itemGrid = new Grid();
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var timeText = new TextBlock {
                Text = $"{h:D2}:{m:D2}:{s:D2}",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Brushes.Gold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var delBtn = new Button {
                Content = "X",
                FontSize = 7,
                Width = 14,
                Height = 14,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = bm.Id
            };
            delBtn.Click += (s, _) => {
                if (s is Button btn) {
                    var id = (string)btn.Tag;
                    _bookmarks.RemoveAll(b => b.Id == id);
                    Timeline.LoadBookmarks(_bookmarks);
                    _bookmarkService.RemoveBookmark(id, _currentDate);
                }
                RefreshBookmarkList();
                BookmarkCountBadge.Text = _bookmarks.Count.ToString();
            };

            itemGrid.Children.Add(timeText);
            itemGrid.Children.Add(delBtn);
            Grid.SetColumn(delBtn, 1);

            itemBorder.Child = itemGrid;

            itemBorder.MouseDown += (_, _) => {
                OnBookmarkJumpRequested(bm.Seconds);
            };

            itemBorder.MouseEnter += (_, _) => {
                itemBorder.Background = new SolidColorBrush(Color.FromArgb(30, 0xFF, 0xD7, 0x00));
            };
            itemBorder.MouseLeave += (_, _) => {
                itemBorder.Background = Brushes.Transparent;
            };

            BookmarkListPanel.Children.Add(itemBorder);
        }
    }

    private async Task ReloadIfMasterSegmentExpired(DateTime targetTime) {
        if (_currentRecordings.Count == 0 || _coordinator is null) return;

        // Only check master channel segments
        var masterId = _coordinator.GetMasterId();
        if (masterId is null) return;

        CameraRecordingInfo? masterRec = null;
        for (var ri = 0; ri < _currentRecordings.Count; ri++) {
            if (_currentRecordings[ri].CameraId == masterId) { masterRec = _currentRecordings[ri]; break; }
        }
        if (masterRec is null) return;

        var needsReload = true;
        for (var i = 0; i < masterRec.Segments.Count; i++) {
            var s = masterRec.Segments[i];
            if (s.StartTime <= targetTime && (!s.EndTime.HasValue || s.EndTime > targetTime)) { needsReload = false; break; }
        }

        if (!needsReload) return;

        var newSegments = await _videoIndexService.QuerySegmentsAsync(
            masterRec.CameraId,
            targetTime.AddMinutes(-5),
            targetTime.AddMinutes(5));

        VideoSegment? best = null;
        for (var ni = 0; ni < newSegments.Count; ni++) {
            var s = newSegments[ni];
            if (s.StartTime <= targetTime && (!s.EndTime.HasValue || s.EndTime > targetTime)) {
                if (best is null || s.StartTime < best.StartTime) {
                    best = s;
                }
            }
        }

        if (best is null) return;

        _coordinator.ReloadMasterCamera(best, targetTime);

        // Sync update View segment data, retain other segments for EOF nav
        var found = false;
        for (var i = 0; i < masterRec.Segments.Count; i++) {
            if (masterRec.Segments[i].Id == best.Id) { found = true; break; }
        }
        if (!found) {
            masterRec.Segments.Add(best);
        }
    }

    private void UpdateTimeDisplay(long pts, long duration) {
        var ptsSecs = pts / 1_000_000.0;
        var ph = (int)(ptsSecs / 3600);
        var pm = (int)((ptsSecs % 3600) / 60);
        var ps = ptsSecs % 60;
        var pSec = (int)ps;
        var pMs = (int)((ps - pSec) * 1000);

        if (duration > 0) {
            var durSecs = duration / 1_000_000.0;
            var dh = (int)(durSecs / 3600);
            var dm = (int)((durSecs % 3600) / 60);
            var ds = (int)(durSecs % 60);
            PlaybackTimeText.Text = $"{ph:D2}:{pm:D2}:{pSec:D2}.{pMs:D3} / {dh:D2}:{dm:D2}:{ds:D2}";
        } else {
            PlaybackTimeText.Text = $"{ph:D2}:{pm:D2}:{pSec:D2}.{pMs:D3} / --:--:--";
        }

        // Mirror to full-screen overlay
        FsTimeText.Text = $"{ph:D2}:{pm:D2}:{pSec:D2}";
    }

    private void UpdatePlayerTimestamps(long pts) {
        var ptsSecs = pts / 1_000_000.0;
        var ph = (int)(ptsSecs / 3600);
        var pm = (int)((ptsSecs % 3600) / 60);
        var psSec = ptsSecs % 60;
        var ps = (int)psSec;
        var pMs = (int)((psSec - ps) * 1000);
        var ts = $"{ph:D2}:{pm:D2}:{ps:D2}.{pMs:D3}";
        foreach (var player in _activePlayers) {
            player.UpdateTimestamp(ts);
        }
    }

    private void UpdatePlayerProgress(long pts, long duration) {
        if (duration <= 0) return;
        var fraction = (double)pts / duration;
        foreach (var player in _activePlayers) {
            player.UpdateProgress(fraction);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Backup
    // ══════════════════════════════════════════════════════════

    private async void BackupBtn_Click(object sender, RoutedEventArgs e) {
        if (_currentRecordings.Count == 0) {
            MessageBox.Show("無錄影資料可備份，請先查詢。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var backupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"HeliVMS_Backup_{_currentDate:yyyyMMdd}");

        var inputDialog = new TextInputDialog("備份路徑", "請輸入備份資料夾路徑：", backupPath);
        if (inputDialog.ShowDialog() == true) {
            backupPath = inputDialog.InputText;
        } else {
            return;
        }

        BackupBtn.IsEnabled = false;
        BackupBtn.Content = "備份中...";

        try {
            Directory.CreateDirectory(backupPath);
            long totalCopied = 0;
            var fileCount = 0;

            foreach (var recording in _currentRecordings) {
                var cameraDir = Path.Combine(backupPath,
                    $"CH{recording.ChannelNumber:D2}_{SanitizeFileName(recording.CameraName)}");
                Directory.CreateDirectory(cameraDir);

                foreach (var segment in recording.Segments) {
                    if (File.Exists(segment.FilePath)) {
                        var destFile = Path.Combine(cameraDir, Path.GetFileName(segment.FilePath));
                        if (!File.Exists(destFile)) {
                            File.Copy(segment.FilePath, destFile);
                            totalCopied += segment.FileSize;
                            fileCount++;
                        }
                    }
                }
            }

            _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                $"備份完成：{fileCount} 檔案，共 {FormatSize(totalCopied)}",
                $"路徑: {backupPath}");

            MessageBox.Show($"備份完成！\n\n" +
                $"路徑：{backupPath}\n" +
                $"檔案數：{fileCount}\n" +
                $"總大小：{FormatSize(totalCopied)}",
                "備份完成", MessageBoxButton.OK, MessageBoxImage.Information);
        } catch (Exception ex) {
            MessageBox.Show($"備份失敗：{ex.Message}", "錯誤",
                MessageBoxButton.OK, MessageBoxImage.Error);
        } finally {
            BackupBtn.IsEnabled = true;
            BackupBtn.Content = "備份錄影";
        }
    }

    private static string SanitizeFileName(string name) {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++) {
            for (var j = 0; j < invalid.Length; j++) {
                if (chars[i] == invalid[j]) { chars[i] = '_'; break; }
            }
        }
        return new string(chars);
    }

    private static string FormatSize(long bytes) {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    // ══════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════

    private bool _isMaximized;
    private int _savedRows;
    private int _savedCols;
    private string _forcedLayout = "auto"; // "auto", "1x1", "2x2", "3x3", "4x4"

    private void OnPlayerPlayPauseRequested(PlaybackPlayer player) {
        PlayPauseBtn_Click(player, new RoutedEventArgs());
    }

    private void OnPlayerSelected(PlaybackPlayer player) {
        foreach (var p in _activePlayers)
            p.IsSelected = false;
        player.IsSelected = true;
    }

    private void OnPlayerSetAsMasterRequested(PlaybackPlayer player) {
        if (_coordinator is null) return;
        if (player.CameraId == _coordinator.GetMasterId()) return;

        _coordinator.SetMasterCamera(player.CameraId);
        // Update UI: clear all master badges, set new one
        foreach (var p in _activePlayers)
            p.SetMaster(false);
        player.SetMaster(true);
        UpdatePlayingCount();
        _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
            $"同步主機變更", $"新主機: CH{player.ChannelNumber:D2} {player.CameraName}");
    }

    private void OnPlayerMaximizeRequested(PlaybackPlayer player) {
        if (_activePlayers.Count <= 1) return;

        if (_isMaximized) {
            RestoreGridLayout();
        } else {
            _savedRows = _displayRows;
            _savedCols = _displayColumns;
            MaximizePlayer(player);
        }
    }

    private void MaximizePlayer(PlaybackPlayer target) {
        _isMaximized = true;

        PlaybackGrid.RowDefinitions.Clear();
        PlaybackGrid.ColumnDefinitions.Clear();
        PlaybackGrid.RowDefinitions.Add(new RowDefinition());
        PlaybackGrid.ColumnDefinitions.Add(new ColumnDefinition());

        foreach (var p in _activePlayers) {
            p.Visibility = Visibility.Collapsed;
        }

        target.Visibility = Visibility.Visible;
        Grid.SetRow(target, 0);
        Grid.SetColumn(target, 0);

        _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
            $"放大頻道 CH{target.ChannelNumber:D2}");
    }

    private void RestoreGridLayout() {
        _isMaximized = false;
        (_displayRows, _displayColumns) = CalculateGrid(_activePlayers.Count);
        RebuildGridLayout();
    }

    // ══════════════════════════════════════════════════════════
    //  Grid Layout Switching
    // ══════════════════════════════════════════════════════════

    private void LayoutBtn_Click(object sender, RoutedEventArgs e) {
        var menu = new ContextMenu();

        string[][] layouts = [
            ["auto", "▦  Auto"],
                ["1x1", "▦  1×1"],
                ["3x3", "▦  3×3"],
                ["4x4", "▦  4×4"],
                ["5x5", "▦  5×5"],
                ["6x6", "▦  6×6"],
                ["7x7", "▦  7×7"],
                ["8x8", "▦  8×8"]
        ];

        foreach (var layout in layouts) {
            var key = layout[0];
            var label = layout[1];
            var item = new MenuItem {
                Header = label,
                IsChecked = _forcedLayout == key,
                IsCheckable = true,
                FontSize = 12,
                Foreground = FindResource("TextBrush") as Brush ?? Brushes.White,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            item.Click += (_, _) => {
                if (_forcedLayout == key) return;
                _forcedLayout = key;
                LayoutBtn.Content = label;
                ApplyForcedLayout();
            };
            menu.Items.Add(item);
        }

        if (sender is Button btn) {
            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void SyncLayoutLabel() {
        var label = _forcedLayout switch {
            "1x1" => "▦  1×1",
            "3x3" => "▦  3×3",
            "4x4" => "▦  4×4",
            "5x5" => "▦  5×5",
            "6x6" => "▦  6×6",
            "7x7" => "▦  7×7",
            "8x8" => "▦  8×8",
            _ => "▦  Auto"
        };
        LayoutBtn.Content = label;
    }

    private void ApplyForcedLayout() {
        if (_activePlayers.Count == 0) return;

        // Exit maximized state if active
        if (_isMaximized) {
            _isMaximized = false;
        }

        (_displayRows, _displayColumns) = CalculateGrid(_activePlayers.Count);

        // Check if forced layout can fit all players
        var capacity = _displayRows * _displayColumns;
        if (capacity < _activePlayers.Count) {
            // Fall back to auto for this count
            _forcedLayout = "auto";
            (_displayRows, _displayColumns) = CalculateAutoGrid(_activePlayers.Count);
            LayoutBtn.Content = "▦  Auto";
        }

        RebuildGridLayout();
        _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
            $"切換版面: {_forcedLayout} ({_displayRows}x{_displayColumns})");
    }

    private void RebuildGridLayout() {
        var total = _activePlayers.Count;

        PlaybackGrid.RowDefinitions.Clear();
        PlaybackGrid.ColumnDefinitions.Clear();
        for (var r = 0; r < _displayRows; r++) {
            PlaybackGrid.RowDefinitions.Add(new RowDefinition());
        }
        for (var c = 0; c < _displayColumns; c++) {
            PlaybackGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        for (var i = 0; i < total; i++) {
            _activePlayers[i].Visibility = Visibility.Visible;
            Grid.SetRow(_activePlayers[i], i / _displayColumns);
            Grid.SetColumn(_activePlayers[i], i % _displayColumns);
        }
    }

    private void CollapseChannelBtn_Click(object sender, RoutedEventArgs e) {
        LeftSidebarColumn.Width = new GridLength(0);
        SearchPanel.Visibility = Visibility.Collapsed;
        ExpandChannelBtn.Visibility = Visibility.Visible;
        CollapseChannelBtn.Content = "▶";
        CollapseChannelBtn.ToolTip = "顯示頻道面板";
    }

    private void ExpandChannelBtn_Click(object sender, RoutedEventArgs e) {
        LeftSidebarColumn.Width = new GridLength(200);
        SearchPanel.Visibility = Visibility.Visible;
        ExpandChannelBtn.Visibility = Visibility.Collapsed;
        CollapseChannelBtn.Content = "◀";
        CollapseChannelBtn.ToolTip = "隱藏頻道面板";
    }

    private void SelectAllBtn_Click(object sender, RoutedEventArgs e) {
        foreach (var item in _channelItems) {
            item.IsChecked = true;
        }
        UpdateChannelCount();
        UpdateButtonStates();
    }

    private async void PlayAllBtn_Click(object sender, RoutedEventArgs e) {
        var checkedCount = 0;
        foreach (var item in _channelItems) {
            if (!string.IsNullOrEmpty(item.CameraId) && checkedCount < MaxPlaybackChannels) {
                item.IsChecked = true;
                checkedCount++;
            } else {
                item.IsChecked = false;
            }
        }
        UpdateChannelCount();
        UpdateButtonStates();
        if (!FilterDatePicker.SelectedDate.HasValue) {
            FilterDatePicker.SelectedDate = DateTime.Today;
        }
        _currentDate = FilterDatePicker.SelectedDate.Value;
        var hour = PlaybackHourCombo.SelectedIndex;
        if (hour < 0) hour = 0;
        var startTarget = _currentDate.Date.AddHours(hour);
        ShowVideoLoading("全部播放載入中…");
        try {
            await LoadAndJumpToHourAsync(startTarget);
        } finally {
            HideVideoLoading();
        }
    }

    private void ClearAllBtn_Click(object sender, RoutedEventArgs e) {
        foreach (var item in _channelItems) {
            item.IsChecked = false;
        }
        UpdateChannelCount();
        UpdateButtonStates();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) {
        PopulateChannelList();
    }

    private async Task RawExportAsync(
        List<(CameraRecordingInfo Rec, VideoSegment Seg, double Offset, double Duration)> exportSegments,
        string outputDir) {
        var total = exportSegments.Count;
        var successCount = 0;
        var wasCancelled = false;

        var progressDialog = new ExportProgressDialog(total);
        progressDialog.Show();

        try {
            for (var i = 0; i < total; i++) {
                if (progressDialog.Token.IsCancellationRequested) {
                    wasCancelled = true;
                    break;
                }

                var (rec, seg, _, _) = exportSegments[i];
                var cameraName = SanitizeFileName(rec.CameraName);

                // Determine source file — seg.FilePath may point to the .ts file or directory
                var srcFile = seg.FilePath;
                if (string.IsNullOrEmpty(srcFile) || !File.Exists(srcFile) && Directory.Exists(srcFile)) {
                    var tsFiles = Directory.GetFiles(srcFile, "*.ts");
                    srcFile = tsFiles.Length > 0 ? tsFiles[0] : "";
                }

                if (string.IsNullOrEmpty(srcFile) || !File.Exists(srcFile)) {
                    _eventLog.LogWarning(EventCategories.Playback, "PlaybackView",
                        $"原始檔不存在: {seg.FilePath} (CH{rec.ChannelNumber})");
                    continue;
                }

                var destFileName = total > 1
                    ? $"{rec.ChannelNumber:D2}_{cameraName}_{Path.GetFileName(srcFile)}"
                    : Path.GetFileName(srcFile);
                var destPath = Path.Combine(outputDir, destFileName);

                progressDialog.UpdateProgress(i, total,
                    $"CH{rec.ChannelNumber:D2} {cameraName} ({i + 1}/{total})");

                try {
                    await Task.Run(() => File.Copy(srcFile, destPath, overwrite: true));
                    successCount++;
                    _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                        $"原始檔複製完成: {srcFile} -> {destPath}");
                } catch (Exception ex) {
                    _eventLog.LogWarning(EventCategories.Playback, "PlaybackView",
                        $"原始檔複製失敗: {srcFile}: {ex.Message}");
                }
            }
        } finally {
            progressDialog.Close();
        }

        if (wasCancelled) {
            _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                $"原始檔匯出已取消：{successCount}/{total} 路");
        } else {
            _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                $"原始檔匯出完成：{successCount}/{total} 路", $"路徑: {outputDir}");

            MessageBox.Show(
                $"原始檔匯出完成！\n\n成功複製：{successCount} / {total} 個片段\n" +
                $"路徑：{outputDir}",
                "匯出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task FfmpegExportAsync(
        List<(CameraRecordingInfo Rec, VideoSegment Seg, double Offset, double Duration)> exportSegments,
        bool burnTimestamp,
        string outputPath) {
        var ffmpegExe = FFmpegBinariesHelper.FindFfmpegExecutable();
        var ext = Path.GetExtension(outputPath).ToLowerInvariant();

        var progressDialog = new ExportProgressDialog(exportSegments.Count);
        progressDialog.Show();

        var successCount = 0;
        var totalSegments = exportSegments.Count;
        var wasCancelled = false;

        try {
            for (var i = 0; i < totalSegments; i++) {
                if (progressDialog.Token.IsCancellationRequested) {
                    wasCancelled = true;
                    break;
                }

                var (rec, seg, offset, duration) = exportSegments[i];
                var cameraName = SanitizeFileName(rec.CameraName);
                var cameraOutput = totalSegments > 1
                    ? Path.Combine(
                        Path.GetDirectoryName(outputPath)!,
                        $"{Path.GetFileNameWithoutExtension(outputPath)}_{cameraName}_CH{rec.ChannelNumber:D2}{ext}")
                    : outputPath;

                progressDialog.UpdateProgress(i, totalSegments,
                    $"CH{rec.ChannelNumber:D2} {cameraName} ({i + 1}/{totalSegments})");

                string args;
                if (burnTimestamp) {
                    var dateStr = seg.StartTime.ToString("yyyy-MM-dd");
                    args = $"-ss {offset:F3} -t {duration:F3} -i \"{seg.FilePath}\" " +
                           $"-vf \"drawtext=text='{dateStr} ':fontsize=18:fontcolor=white:x=10:y=10:box=1:boxcolor=black@0.5,drawtext=text='%{{pts\\:hms}}':fontsize=18:fontcolor=white:x=10+text_w:y=10:box=1:boxcolor=black@0.5\" " +
                           $"-c:v libx264 -preset ultrafast -crf 28 " +
                           $"-c:a aac -b:a 64k -y \"{cameraOutput}\"";
                } else {
                    args = $"-ss {offset:F3} -t {duration:F3} -i \"{seg.FilePath}\" " +
                           $"-c copy -avoid_negative_ts 1 -y \"{cameraOutput}\"";
                }

                var psi = new ProcessStartInfo(ffmpegExe, args) {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc is null) continue;

                _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                    $"匯出中: CH{rec.ChannelNumber:D2} {seg.FilePath} -> {cameraOutput}" +
                    (burnTimestamp ? " (含時間戳)" : ""));

                try {
                    await proc.WaitForExitAsync(progressDialog.Token);
                    if (proc.ExitCode == 0) {
                        successCount++;
                    } else
                        _eventLog.LogWarning(EventCategories.Playback, "PlaybackView",
                            $"匯出失敗 (exit={proc.ExitCode}): {cameraOutput}");
                } catch (OperationCanceledException) {
                    wasCancelled = true;
                    try { proc.Kill(); } catch { }
                    break;
                }
            }
        } finally {
            progressDialog.Close();
        }

        if (wasCancelled) {
            _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                $"匯出已取消：{successCount}/{totalSegments} 路");
        } else {
            _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
                $"匯出完成：{successCount}/{totalSegments} 路",
                $"路徑: {outputPath}");

            MessageBox.Show(
                $"匯出完成！\n\n成功：{successCount} / {totalSegments} 路\n" +
                (successCount > 0 ? $"路徑：{(totalSegments > 1 ? Path.GetDirectoryName(outputPath) : outputPath)}" : ""),
                "匯出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SnapshotBtn_Click(object sender, RoutedEventArgs e) {
        if (_activePlayers.Count == 0) return;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "HeliVMS_Screenshots");

        var success = 0;
        foreach (var player in _activePlayers) {
            if (player.SaveSnapshot(dir) is not null) {
                success++;
            }
        }

        _eventLog.LogInfo(EventCategories.Playback, "PlaybackView",
            $"拍照完成：{success}/{_activePlayers.Count} 路",
            $"路徑: {dir}");

        MessageBox.Show($"拍照完成！\n\n儲存位置：{dir}\n成功：{success} / {_activePlayers.Count} 路",
            "拍照", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowEmptyState() {
        ClearVideoGrid();
        EmptyPrompt.Visibility = Visibility.Visible;
    }

    // ══════════════════════════════════════════════════════════
    //  Recording calendar
    // ══════════════════════════════════════════════════════════



    private (int rows, int cols) CalculateGrid(int count) {
        return _forcedLayout switch {
            "1x1" => (1, 1),
            "3x3" => (3, 3),
            "4x4" => (4, 4),
            "5x5" => (5, 5),
            "6x6" => (6, 6),
            "7x7" => (7, 7),
            "8x8" => (8, 8),
            _ => CalculateAutoGrid(count)
        };
    }

    private static (int rows, int cols) CalculateAutoGrid(int count) {
        return count switch {
            1 => (1, 1),
            2 => (2, 1),
            3 => (1, 3),
            4 => (2, 2),
            5 => (3, 3),
            6 => (3, 3),
            7 => (3, 3),
            8 => (3, 3),
            9 => (3, 3),
            10 => (3, 4),
            11 => (3, 4),
            12 => (3, 4),
            13 => (4, 4),
            14 => (4, 4),
            15 => (4, 4),
            16 => (4, 4),
            17 => (4, 5),
            18 => (4, 5),
            19 => (4, 5),
            20 => (4, 5),
            21 => (5, 5),
            22 => (5, 5),
            23 => (5, 5),
            24 => (5, 5),
            25 => (5, 5),
            26 => (5, 6),
            27 => (5, 6),
            28 => (5, 6),
            29 => (5, 6),
            30 => (5, 6),
            31 => (6, 6),
            32 => (6, 6),
            33 => (6, 6),
            34 => (6, 6),
            35 => (6, 6),
            36 => (6, 6),
            37 => (6, 7),
            38 => (6, 7),
            39 => (6, 7),
            40 => (6, 7),
            41 => (6, 7),
            42 => (6, 7),
            43 => (7, 7),
            44 => (7, 7),
            45 => (7, 7),
            46 => (7, 7),
            47 => (7, 7),
            48 => (7, 7),
            49 => (7, 7),
            50 => (7, 8),
            51 => (7, 8),
            52 => (7, 8),
            53 => (7, 8),
            54 => (7, 8),
            55 => (7, 8),
            56 => (7, 8),
            57 => (8, 8),
            58 => (8, 8),
            59 => (8, 8),
            60 => (8, 8),
            61 => (8, 8),
            62 => (8, 8),
            63 => (8, 8),
            _ => (8, 8)
        };
    }

    private void UpdateButtonStates() {
        var hasContent = _activePlayers.Count > 0;
        PlayPauseBtn.IsEnabled = hasContent;
        StepBackBtn.IsEnabled = hasContent;
        StepFwdBtn.IsEnabled = hasContent;
        JumpPrevRecBtn.IsEnabled = hasContent;
        JumpNextRecBtn.IsEnabled = hasContent;
        StopAllBtn.IsEnabled = hasContent;
        BookmarkBtn.IsEnabled = hasContent;
        LayoutBtn.IsEnabled = hasContent;

        // Update icon based on play state
        PlayPauseIcon.Data = (_isPlaying
            ? TryFindResource("IconPause") ?? Application.Current?.TryFindResource("IconPause")
            : TryFindResource("IconPlay") ?? Application.Current?.TryFindResource("IconPlay")) as Geometry
            ?? PlayPauseIcon.Data;
        PlayPauseBtn.ToolTip = _isPlaying ? "暫停 (Space)" : "播放 (Space)";

        UpdateFsTransportState();
    }

    // ══════════════════════════════════════════════════════════
    //  GoTo Time
    // ══════════════════════════════════════════════════════════

    private void ShowGoToTimeDialog() {
        if (_activePlayers.Count == 0 || _coordinator is null) return;

        var dialog = new GoToTimeDialog();
        if (dialog.ShowDialog() == true && dialog.ResultSeconds >= 0) {
            var seconds = dialog.ResultSeconds;
            seconds = Math.Clamp(seconds, 0, 86400);
            Timeline_SeekRequested(seconds);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Shortcuts overlay
    // ══════════════════════════════════════════════════════════

    private void ToggleShortcutsOverlay() {
        ShortcutsOverlay.Visibility = ShortcutsOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ShortcutsOverlay_MouseDown(object sender, MouseButtonEventArgs e) {
        ToggleShortcutsOverlay();
    }

    // ══════════════════════════════════════════════════════════
    //  Full-screen mode
    // ══════════════════════════════════════════════════════════

    private void InitFsAutoHide() {
        if (_fsAutoHideTimer is not null) return;
        _fsTopBar = FullScreenOverlay.FindName("FsTopBar") as Border;
        _fsBottomBar = FullScreenOverlay.FindName("FsBottomBar") as Border;
        _fsAutoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _fsAutoHideTimer.Tick += (_, _) => {
            _fsTopBar?.Visibility = Visibility.Collapsed;
            _fsBottomBar?.Visibility = Visibility.Collapsed;
            _fsAutoHideTimer?.Stop();
        };
        FullScreenOverlay.MouseMove += (_, _) => {
            _fsTopBar?.Visibility = Visibility.Visible;
            _fsBottomBar?.Visibility = Visibility.Visible;
            _fsAutoHideTimer?.Stop();
            _fsAutoHideTimer?.Start();
        };
    }

    private void ToggleFullScreen() {
        _isFullScreen = !_isFullScreen;

        if (_isFullScreen) {
            InitFsAutoHide();
            _fsTopBar?.Visibility = Visibility.Visible;
            _fsBottomBar?.Visibility = Visibility.Visible;
            _fsAutoHideTimer?.Start();
            // Hide chrome
            ToolbarRow.Visibility = Visibility.Collapsed;
            SearchPanel.Visibility = Visibility.Collapsed;
            TimelineArea.Visibility = Visibility.Collapsed;
            ControlsRow.Visibility = Visibility.Collapsed;
            FullScreenOverlay.Visibility = Visibility.Visible;

            // Sync overlay state
            UpdateFsTransportState();
            UpdateFsSpeedDisplay();
            SyncFsTimeDisplay();
        } else {
            _fsAutoHideTimer?.Stop();
            // Restore chrome
            ToolbarRow.Visibility = Visibility.Visible;
            SearchPanel.Visibility = Visibility.Visible;
            TimelineArea.Visibility = Visibility.Visible;
            ControlsRow.Visibility = Visibility.Visible;
            FullScreenOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncFsTimeDisplay() {
        // Mirror main playback time to overlay top-left
        FsTimeText.Text = PlaybackTimeText.Text.Split('/')[0].Trim();
    }

    private void UpdateFsTransportState() {
        var hasContent = _activePlayers.Count > 0;
        FsPlayPauseBtn.IsEnabled = hasContent;
        FsStepBackBtn.IsEnabled = hasContent;
        FsStepFwdBtn.IsEnabled = hasContent;
        FsSegPrevBtn.IsEnabled = hasContent;
        FsSegNextBtn.IsEnabled = hasContent;
        FsSpeedBtn.IsEnabled = hasContent;

        var fsIcon = _isPlaying ? "IconPause" : "IconPlay";
        FsPlayPauseIcon.Data = (Geometry)FindResource(fsIcon);
        FsPlayPauseBtn.Tag = _isPlaying ? "pause" : "play";
    }

    private void UpdateFsSpeedDisplay() {
        FsSpeedBtn.Content = SpeedLabel.Text;
    }

    private void FsExitBtn_Click(object sender, RoutedEventArgs e) {
        ToggleFullScreen();
    }

    private void FsBookmarkBtn_Click(object sender, RoutedEventArgs e) {
        AddBookmarkAtCurrentPosition();
    }

    private void FsSnapshotBtn_Click(object sender, RoutedEventArgs e) {
        SnapshotBtn_Click(sender, e);
    }

    private void FsTransportBtn_Click(object sender, RoutedEventArgs e) {
        if (sender is Button btn && btn.Tag is string action) {
            switch (action) {
                case "play":
                case "pause":
                    PlayPauseBtn_Click(sender, e);
                    break;
                case "segPrev":
                    JumpPrevRecBtn_Click(sender, e);
                    break;
                case "segNext":
                    JumpNextRecBtn_Click(sender, e);
                    break;
                case "stepBack":
                    StepBackBtn_Click(sender, e);
                    break;
                case "stepFwd":
                    StepFwdBtn_Click(sender, e);
                    break;
                case "speed":
                    var currentSpeed = SliderValueToSpeed(SpeedSlider.Value);
                    var nextSpeed = currentSpeed >= 16 ? 1.0 : currentSpeed * 2;
                    var sliderVal = nextSpeed switch {
                        0.25 => 0, 0.5 => 25, 1.0 => 50, 2.0 => 75, 4.0 => 87, 8.0 => 94, 16.0 => 100,
                        _ => 50
                    };
                    SpeedSlider.Value = sliderVal;
                    break;
            }
        }
    }
}

// ══════════════════════════════════════════════════════════════
//  Search result entry
// ══════════════════════════════════════════════════════════════

public class SearchResultEntry {
    public string CameraId { get; set; } = "";
    public int ChannelNumber { get; set; }
    public string CameraName { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int RecordType { get; set; }
    public string FilePath { get; set; } = "";
    public string DurationLabel => $"{(EndTime - StartTime).TotalMinutes:F1} 分";
    public string TimeLabel => $"{StartTime:HH:mm:ss} - {EndTime:HH:mm:ss}";
}

// ══════════════════════════════════════════════════════════════
//  Channel checkbox item
// ══════════════════════════════════════════════════════════════

public class ChannelCheckBox : Border {
    private readonly CheckBox _checkBox;
    private readonly Border _statusDot;
    private readonly TextBlock _label;

    public string CameraId { get; set; } = "";
    public string CameraName { get; set; } = "";
    public int ChannelNumber { get; set; }

    private bool _hasCamera;
    public bool HasCamera {
        get => _hasCamera;
        set {
            _hasCamera = value;
            _statusDot.Background = value
                ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
                : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            _statusDot.ToolTip = value ? "已指派攝影機" : "未指派攝影機";
            Opacity = value ? 1.0 : 0.6;
        }
    }

    public bool IsChecked {
        get => _checkBox.IsChecked ?? false;
        set => _checkBox.IsChecked = value;
    }

    public event Action<string, bool>? CheckChanged;

    public ChannelCheckBox() {
        Background = Brushes.Transparent;
        Padding = new Thickness(4, 2, 4, 2);
        Cursor = System.Windows.Input.Cursors.Hand;
        MouseLeftButtonUp += (_, _) => {
            _checkBox?.IsChecked = !_checkBox.IsChecked;
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _statusDot = new Border {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(6, 0, 4, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "未指派攝影機"
        };

        _checkBox = new CheckBox {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Foreground = (Brush?)Application.Current?.FindResource("TextBrush") ?? Brushes.White,
        };
        _checkBox.Checked += (_, _) => CheckChanged?.Invoke(CameraId, true);
        _checkBox.Unchecked += (_, _) => CheckChanged?.Invoke(CameraId, false);

        _label = new TextBlock {
            FontSize = 12,
            Foreground = Application.Current?.FindResource("TextBrush") as Brush ?? Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        Grid.SetColumn(_statusDot, 0);
        Grid.SetColumn(_checkBox, 1);
        Grid.SetColumn(_label, 2);
        grid.Children.Add(_statusDot);
        grid.Children.Add(_checkBox);
        grid.Children.Add(_label);
        Child = grid;
    }

    public void UpdateLabel() {
        _label.Text = string.IsNullOrEmpty(CameraName)
            ? $"{ChannelNumber}"
            : $"{ChannelNumber} {CameraName}";
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e) {
        base.OnPropertyChanged(e);
        if (e.Property == DataContextProperty) {
            UpdateLabel();
        }
    }
}

// ══════════════════════════════════════════════════════════════
//  Simple text input dialog (for backup path)
// ══════════════════════════════════════════════════════════════

/// <summary>Export options dialog — choose whether to burn timestamp</summary>
public class ExportOptionsDialog : Window {
    private readonly RadioButton _encodeRb;
    private readonly RadioButton _fastRb;
    private readonly RadioButton _rawRb;
    private readonly List<(int Channel, string Name, CheckBox Cb)> _channelChecks = [];

    public bool BurnTimestamp => _fastRb.IsChecked == true;
    public bool IsRawCopy => _rawRb.IsChecked == true;
    public HashSet<int> SelectedChannels { get; } = [];

    public ExportOptionsDialog(IEnumerable<(int Channel, string Name)> channels) {
        var channelList = channels.ToList();
        Title = "匯出選項";
        Width = 440;
        Height = Math.Min(480, 240 + channelList.Count * 28);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("BackgroundBrush") ?? Brushes.Black;
        Foreground = (Brush)Application.Current.FindResource("TextBrush") ?? Brushes.White;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;

        var grid = new Grid { Margin = new Thickness(16) };

        for (var i = 0; i < 6; i++) {
            grid.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        }

        var headerText = new TextBlock {
            Text = "匯出錄影片段",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Foreground,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // Export mode radio buttons
        var modeBorder = new Border {
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            Background = (Brush)Application.Current.FindResource("InputBackgroundBrush") ?? Brushes.Black
        };
        var modeStack = new StackPanel();
        var modeLabel = new TextBlock {
            Text = "匯出模式",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Foreground,
            Margin = new Thickness(0, 0, 0, 6)
        };
        modeStack.Children.Add(modeLabel);

        _rawRb = new RadioButton {
            Content = " 原始檔複製（不經編碼，最快）",
            IsChecked = true,
            FontSize = 13,
            Foreground = Foreground,
            Margin = new Thickness(0, 0, 0, 2),
            GroupName = "ExportMode"
        };
        var rawInfo = new TextBlock {
            Text = "直接複製原始 .ts 片段檔案，無需 ffmpeg，保有原始畫質。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("SecondaryTextBrush") ?? Brushes.Gray,
            Margin = new Thickness(24, 0, 0, 6)
        };
        modeStack.Children.Add(_rawRb);
        modeStack.Children.Add(rawInfo);

        _fastRb = new RadioButton {
            Content = " 快速匯出（ffmpeg stream copy，無重新編碼）",
            IsChecked = false,
            FontSize = 13,
            Foreground = Foreground,
            Margin = new Thickness(0, 0, 0, 2),
            GroupName = "ExportMode"
        };
        var fastInfo = new TextBlock {
            Text = "使用 ffmpeg 快速裁切，不重新編碼，可指定起訖時間精準度較高。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("SecondaryTextBrush") ?? Brushes.Gray,
            Margin = new Thickness(24, 0, 0, 6)
        };
        modeStack.Children.Add(_fastRb);
        modeStack.Children.Add(fastInfo);

        _encodeRb = new RadioButton {
            Content = " 標準匯出（ffmpeg 重新編碼，含時間戳疊加）",
            IsChecked = false,
            FontSize = 13,
            Foreground = Foreground,
            Margin = new Thickness(0, 0, 0, 2),
            GroupName = "ExportMode"
        };
        var encodeInfo = new TextBlock {
            Text = "使用 ffmpeg 重新編碼，可在影片上疊加時間戳記，花費時間較長。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("SecondaryTextBrush") ?? Brushes.Gray,
            Margin = new Thickness(24, 0, 0, 0)
        };
        modeStack.Children.Add(_encodeRb);
        modeStack.Children.Add(encodeInfo);
        modeBorder.Child = modeStack;

        // Channel selection header
        var channelHeaderPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var channelLabel = new TextBlock {
            Text = "選擇要匯出的頻道：",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center
        };
        var selectAllBtn = new Button {
            Content = "全選",
            Height = 20,
            FontSize = 10,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(8, 0, 2, 0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = Foreground,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1)
        };
        var clearBtn = new Button {
            Content = "清除",
            Height = 20,
            FontSize = 10,
            Padding = new Thickness(6, 0, 6, 0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = Foreground,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1)
        };
        channelHeaderPanel.Children.Add(channelLabel);
        channelHeaderPanel.Children.Add(selectAllBtn);
        channelHeaderPanel.Children.Add(clearBtn);

        // Scrollable channel list
        var channelContainer = new ScrollViewer {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = (Brush)Application.Current.FindResource("InputBackgroundBrush") ?? Brushes.Black,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var channelStack = new StackPanel();
        foreach (var (ch, name) in channelList) {
            var cb = new CheckBox {
                Content = $"CH{ch:D2} {name}",
                IsChecked = true,
                FontSize = 12,
                Foreground = Foreground,
                Margin = new Thickness(6, 2, 4, 2),
                Tag = ch
            };
            _channelChecks.Add((ch, name, cb));
            channelStack.Children.Add(cb);
        }
        channelContainer.Content = channelStack;

        selectAllBtn.Click += (_, _) => {
            foreach (var (_, _, cb) in _channelChecks) cb.IsChecked = true;
        };
        clearBtn.Click += (_, _) => {
            foreach (var (_, _, cb) in _channelChecks) cb.IsChecked = false;
        };

        // Button panel
        var btnPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var okBtn = new Button {
            Content = "下一步",
            Width = 80,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Background = (Brush)Application.Current.FindResource("PrimaryBrush") ?? Brushes.DodgerBlue,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        okBtn.Click += (_, _) => {
            foreach (var (ch, _, cb) in _channelChecks) {
                if (cb.IsChecked == true) {
                    SelectedChannels.Add(ch);
                }
            }
            if (SelectedChannels.Count == 0) {
                MessageBox.Show("請至少選擇一個頻道。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
            Close();
        };

        var cancelBtn = new Button {
            Content = "取消",
            Width = 80,
            Height = 30,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = Foreground,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1)
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);

        // Assemble
        grid.Children.Add(headerText);
        grid.Children.Add(modeBorder);
        grid.Children.Add(channelHeaderPanel);
        grid.Children.Add(channelContainer);
        grid.Children.Add(btnPanel);
        Grid.SetRow(modeBorder, 1);
        Grid.SetRow(channelHeaderPanel, 2);
        Grid.SetRow(channelContainer, 3);
        Grid.SetRow(btnPanel, 4);

        Content = new Border {
            Background = (Brush)Application.Current.FindResource("SurfaceBrush") ?? Brushes.DarkGray,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
    }
}

public class TextInputDialog : Window {
    private readonly TextBox _inputBox;
    private readonly Button _okBtn;
    private readonly Button _cancelBtn;

    public string InputText => _inputBox.Text;

    public TextInputDialog(string title, string prompt, string defaultText) {
        Title = title;
        Width = 450;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("BackgroundBrush") ?? Brushes.Black;
        Foreground = (Brush)Application.Current.FindResource("TextBrush") ?? Brushes.White;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var promptText = new TextBlock {
            Text = prompt,
            FontSize = 13,
            Foreground = Foreground,
            Margin = new Thickness(0, 0, 0, 8)
        };

        _inputBox = new TextBox {
            Text = defaultText,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12),
            Background = (Brush)Application.Current.FindResource("InputBackgroundBrush") ?? Brushes.Gray,
            Foreground = Foreground,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            Padding = new Thickness(8, 6, 8, 6)
        };

        var btnPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        _okBtn = new Button {
            Content = "確定",
            Width = 80,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = (Brush)Application.Current.FindResource("PrimaryBrush") ?? Brushes.DodgerBlue,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        _okBtn.Click += (_, _) => { DialogResult = true; Close(); };

        _cancelBtn = new Button {
            Content = "取消",
            Width = 80,
            Height = 30,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = Foreground,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1)
        };
        _cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        btnPanel.Children.Add(_okBtn);
        btnPanel.Children.Add(_cancelBtn);

        grid.Children.Add(promptText);
        grid.Children.Add(_inputBox);
        grid.Children.Add(btnPanel);

        Grid.SetRow(_inputBox, 1);
        Grid.SetRow(btnPanel, 2);

        Content = new Border {
            Background = (Brush)Application.Current.FindResource("SurfaceBrush") ?? Brushes.DarkGray,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid
        };

        _inputBox.Focus();
        _inputBox.SelectAll();
    }
}

// ══════════════════════════════════════════════════════════════
//  GoTo Time dialog (Ctrl+G)
// ══════════════════════════════════════════════════════════════

public class GoToTimeDialog : Window {
    private readonly TextBox _hoursBox;
    private readonly TextBox _minutesBox;
    private readonly TextBox _secondsBox;
    private readonly Button _okBtn;
    private readonly Button _cancelBtn;

    public double ResultSeconds { get; private set; } = -1;

    public GoToTimeDialog() {
        Title = "跳轉時間";
        Width = 320;
        Height = 190;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("BackgroundBrush") ?? Brushes.Black;
        Foreground = (Brush)Application.Current.FindResource("TextBrush") ?? Brushes.White;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock {
            Text = "輸入時間跳轉",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Foreground,
            Margin = new Thickness(0, 0, 0, 12)
        };

        // Time input: HH : MM : SS
        var inputPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _hoursBox = MakeTimeField("00", 60);
        var sep1 = new TextBlock { Text = " : ", FontSize = 18, Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center };
        _minutesBox = MakeTimeField("00", 50);
        var sep2 = new TextBlock { Text = " : ", FontSize = 18, Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center };
        _secondsBox = MakeTimeField("00", 50);

        inputPanel.Children.Add(_hoursBox);
        inputPanel.Children.Add(sep1);
        inputPanel.Children.Add(_minutesBox);
        inputPanel.Children.Add(sep2);
        inputPanel.Children.Add(_secondsBox);

        var btnPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        _okBtn = new Button {
            Content = "跳轉",
            Width = 80,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Background = (Brush)Application.Current.FindResource("PrimaryBrush") ?? Brushes.DodgerBlue,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        _okBtn.Click += Ok_Click;

        _cancelBtn = new Button {
            Content = "取消",
            Width = 80,
            Height = 30,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = Foreground,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1)
        };
        _cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        btnPanel.Children.Add(_okBtn);
        btnPanel.Children.Add(_cancelBtn);

        root.Children.Add(header);
        root.Children.Add(inputPanel);
        root.Children.Add(btnPanel);
        Grid.SetRow(inputPanel, 1);
        Grid.SetRow(btnPanel, 2);

        Content = new Border {
            Background = (Brush)Application.Current.FindResource("SurfaceBrush") ?? Brushes.DarkGray,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = root
        };

        Loaded += (_, _) => _hoursBox.Focus();
        KeyDown += (_, e) => {
            if (e.Key == Key.Enter) Ok_Click(_okBtn, e);
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };
    }

    private static TextBox MakeTimeField(string placeholder, double width) {
        return new TextBox {
            Text = placeholder,
            Width = width,
            Height = 32,
            FontSize = 20,
            FontFamily = new FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxLength = 2,
            Background = (Brush)Application.Current.FindResource("InputBackgroundBrush") ?? Brushes.Gray,
            Foreground = (Brush)Application.Current.FindResource("TextBrush") ?? Brushes.White,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2)
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) {
        var h = TryParseInt(_hoursBox.Text);
        var m = TryParseInt(_minutesBox.Text);
        var s = TryParseInt(_secondsBox.Text);

        h = Math.Clamp(h, 0, 23);
        m = Math.Clamp(m, 0, 59);
        s = Math.Clamp(s, 0, 59);

        ResultSeconds = h * 3600 + m * 60 + s;
        DialogResult = true;
        Close();
    }

    private static int TryParseInt(string text) {
        if (int.TryParse(text?.Trim(), out var val)) return val;
        return 0;
    }
}

// ══════════════════════════════════════════════════════════════
//  ExportProgressDialog — modeless progress + cancel for export
// ══════════════════════════════════════════════════════════════

public class ExportProgressDialog : Window {
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _statusText;
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    public ExportProgressDialog(int totalSegments) {
        Title = "匯出錄影";
        Width = 420;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("BackgroundBrush") ?? Brushes.Black;
        Foreground = (Brush)Application.Current.FindResource("TextBrush") ?? Brushes.White;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Topmost = true;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock {
            Text = "正在匯出錄影…",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Foreground,
            Margin = new Thickness(0, 0, 0, 12)
        };

        _progressBar = new ProgressBar {
            Minimum = 0,
            Maximum = totalSegments,
            Value = 0,
            Height = 18,
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)Application.Current.FindResource("PrimaryBrush") ?? Brushes.DodgerBlue,
            Background = (Brush)Application.Current.FindResource("SecondarySurfaceBrush") ?? Brushes.Gray
        };

        _statusText = new TextBlock {
            Text = $"0 / {totalSegments}",
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("SecondaryTextBrush") ?? Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var cancelBtn = new Button {
            Content = "取消匯出",
            Width = 90,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = Foreground,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1)
        };
        cancelBtn.Click += (_, _) => {
            cancelBtn.IsEnabled = false;
            cancelBtn.Content = "正在取消…";
            _cts.Cancel();
        };

        root.Children.Add(header);
        root.Children.Add(_progressBar);
        root.Children.Add(_statusText);
        root.Children.Add(cancelBtn);
        Grid.SetRow(_progressBar, 1);
        Grid.SetRow(_statusText, 2);
        Grid.SetRow(cancelBtn, 3);

        Content = new Border {
            Background = (Brush)Application.Current.FindResource("SurfaceBrush") ?? Brushes.DarkGray,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = root
        };
    }

    public void UpdateProgress(int completed, int total, string status) {
        Dispatcher.BeginInvoke(() => {
            _progressBar.Value = completed;
            _progressBar.Maximum = total;
            _statusText.Text = $"{status}\n{completed} / {total}";
        });
    }

    protected override void OnClosed(EventArgs e) {
        if (!_cts.IsCancellationRequested) {
            _cts.Cancel();
        }
        _cts.Dispose();
        base.OnClosed(e);
    }
}

// ══════════════════════════════════════════════════════════════
//  ShortcutRow — key/description pair for shortcuts overlay
// ══════════════════════════════════════════════════════════════

public class ShortcutRow : Border {
    private readonly TextBlock _keyText;
    private readonly TextBlock _descText;

    public string Key_ {
        get => (string)GetValue(Key_Property);
        set => SetValue(Key_Property, value);
    }
    public static readonly DependencyProperty Key_Property =
        DependencyProperty.Register(nameof(Key_), typeof(string), typeof(ShortcutRow),
            new PropertyMetadata("", OnKeyChanged));

    public string Desc {
        get => (string)GetValue(DescProperty);
        set => SetValue(DescProperty, value);
    }
    public static readonly DependencyProperty DescProperty =
        DependencyProperty.Register(nameof(Desc), typeof(string), typeof(ShortcutRow),
            new PropertyMetadata("", OnDescChanged));

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ShortcutRow)d)._keyText.Text = e.NewValue as string ?? "";

    private static void OnDescChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ShortcutRow)d)._descText.Text = e.NewValue as string ?? "";

    public ShortcutRow() {
        Margin = new Thickness(0, 2, 0, 2);
        Padding = new Thickness(6, 3, 6, 3);
        Background = Brushes.Transparent;
        CornerRadius = new CornerRadius(3);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _keyText = new TextBlock {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Consolas"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x82, 0xAA, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 20, 0)
        };

        _descText = new TextBlock {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(_keyText, 0);
        Grid.SetColumn(_descText, 1);
        grid.Children.Add(_keyText);
        grid.Children.Add(_descText);
        Child = grid;
    }
}

/// <summary>Per-camera frame slot for lock-free producer-consumer frame exchange.</summary>
internal sealed class FrameSlot {
    public PooledBuffer? Data;
    public long ArrivalTimestamp;
}
