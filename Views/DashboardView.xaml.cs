using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HeliVMS.Views;

public partial class DashboardView : UserControl {
    private readonly ICameraHealthService _health;
    private readonly ISystemStatusService _status;
    private readonly IEventService _eventLog;
    private readonly IRecordingService _recording;
    private readonly IVideoIndexService _videoIndex;
    private readonly ICameraService _cameraService;
    private readonly IRecordingWatchdogService _watchdog;
    private readonly IRecordingService _recordingService;
    private readonly IAlertDispatcherService _alertDispatcher;
    private readonly MetricsHistoryService _metrics;
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private bool _loaded;
    private long _lastBytesWritten;
    private DateTime _lastBandwidthMeasure = DateTime.Now;

    public DashboardView() {
        InitializeComponent();
        _health = App.Services.GetRequiredService<ICameraHealthService>();
        _status = App.Services.GetRequiredService<ISystemStatusService>();
        _eventLog = App.Services.GetRequiredService<IEventService>();
        _recording = App.Services.GetRequiredService<IRecordingService>();
        _videoIndex = App.Services.GetRequiredService<IVideoIndexService>();
        _cameraService = App.Services.GetRequiredService<ICameraService>();
        _recordingService = App.Services.GetRequiredService<IRecordingService>();
        _watchdog = App.Services.GetRequiredService<IRecordingWatchdogService>();
        _alertDispatcher = App.Services.GetRequiredService<IAlertDispatcherService>();
        _metrics = App.Services.GetRequiredService<MetricsHistoryService>();

        _metrics.HistoryUpdated += () => {
            if (_loaded) { _ = Dispatcher.InvokeAsync(RefreshCharts); }
        };
        _status.PropertyChanged += (_, e) => // REVIEW: lambda captures 'this' — consider weak event pattern
        {
            if (_loaded) { _ = Dispatcher.InvokeAsync(RefreshStats); }
        };
        _health.HealthChanged += () => // REVIEW: lambda captures 'this' — consider weak event pattern
        {
            if (_loaded) { _ = Dispatcher.InvokeAsync(RefreshCameraHealth); }
        };
        _recording.RecordingStatusChanged += (_, _) => // REVIEW: lambda captures 'this' — consider weak event pattern
        {
            if (_loaded) { _ = Dispatcher.InvokeAsync(RefreshRecordingStats); }
        };
        Loaded += (_, _) => {
            _loaded = true;
            RefreshAll();
            RenderCalendar();
        };
    }

    private void RefreshAll() {
        RefreshCameraHealth();
        RefreshStats();
        RefreshRecordingStats();
        RefreshEvents();
        RefreshCharts();
    }

    private void RefreshCharts() {
        DrawLineChart(BandwidthChartCanvas, _metrics.BandwidthHistory, _metrics.GetMaxBandwidth(),
            "B/s", v => v switch { > 1_000_000 => $"{(v / 1_000_000):F1}M", > 1_000 => $"{(v / 1_000):F0}K", _ => $"{v:F0}" });
        DrawLineChart(StorageChartCanvas, _metrics.StorageHistory, _metrics.GetMaxStorage(),
            "GB", v => $"{v:F1}");
        DrawLineChart(CpuChartCanvas, _metrics.CpuHistory, 100,
            "%", v => $"{v:F0}%");
        DrawLineChart(MemoryChartCanvas, _metrics.MemoryHistory, 100,
            "%", v => $"{v:F0}%");
    }

    private static void DrawLineChart(Canvas canvas, List<MetricsHistoryService.DataPoint> data, double maxVal,
        string unitLabel, Func<double, string> formatValue) {
        canvas.Children.Clear();
        if (data.Count < 2) return;

        var w = canvas.ActualWidth > 10 ? canvas.ActualWidth : 340;
        var h = canvas.ActualHeight > 10 ? canvas.ActualHeight : 120;
        var padL = 40.0;
        var padR = 8.0;
        var padT = 4.0;
        var padB = 16.0;
        var plotW = w - padL - padR;
        var plotH = h - padT - padB;

        // Draw horizontal grid lines (4 lines)
        for (var i = 0; i <= 4; i++) {
            var y = padT + plotH * (1 - i / 4.0);
            var line = new Line {
                X1 = padL, Y1 = y, X2 = w - padR, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                StrokeThickness = 0.5
            };
            canvas.Children.Add(line);

            var label = new TextBlock {
                Text = formatValue(maxVal * i / 4.0),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255))
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 7);
            canvas.Children.Add(label);
        }

        // Polyline for data
        var points = new PointCollection();
        for (var i = 0; i < data.Count; i++) {
            var x = padL + plotW * i / (data.Count - 1);
            var y = padT + plotH * (1 - data[i].Value / maxVal);
            points.Add(new Point(x, y));
        }

        var polyline = new Polyline {
            Points = points,
            Stroke = new SolidColorBrush(Color.FromArgb(200, 0, 150, 255)),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round
        };
        canvas.Children.Add(polyline);

        // Fill
        var fillPoints = new PointCollection(points);
        fillPoints.Add(new Point(padL + plotW, padT + plotH));
        fillPoints.Add(new Point(padL, padT + plotH));
        var polygon = new Polygon {
            Points = fillPoints,
            Fill = new SolidColorBrush(Color.FromArgb(30, 0, 150, 255))
        };
        canvas.Children.Insert(0, polygon);
    }

    private void RefreshCameraHealth() {
        CameraHealthList.ItemsSource = _health.HealthItems;
        CameraOnlineText.Text = _health.OnlineCount.ToString();
        CameraOfflineText.Text = _health.OfflineCount > 0
            ? $"{_health.OfflineCount} 台離線"
            : "全部上線";
    }

    private void RefreshStats() {
        CpuUsageText.Text = $"{_status.CpuUsagePercent:F0}%";
        var memText = $"{_status.MemoryUsedBytes / (1024 * 1024)} / {_status.MemoryTotalBytes / (1024 * 1024)} MB";
        MemoryUsageText.Text = memText;
        var diskFreeGB = _status.DiskFreeBytes / (1024.0 * 1024 * 1024);
        var diskTotalGB = _status.DiskTotalBytes / (1024.0 * 1024 * 1024);
        DiskUsageText.Text = $"{diskFreeGB:F1} GB / {diskTotalGB:F1} GB 可用";

        var activeCount = _recording.GetActiveRecordings().Count;

        // Estimate bandwidth from recording bytes written
        var recordings = _recording.GetActiveRecordings();
        var nowBytes = recordings.Sum(r => r.BytesWritten);
        var elapsed = (DateTime.Now - _lastBandwidthMeasure).TotalSeconds;
        if (elapsed > 1) {
            var bps = (nowBytes - _lastBytesWritten) / elapsed;
            BandwidthText.Text = bps switch {
                > 1_000_000 => $"{(bps / 1_000_000):F1} MB/s 頻寬",
                > 1_000 => $"{(bps / 1_000):F0} KB/s 頻寬",
                _ => $"{bps:F0} B/s 頻寬",
            };
            _lastBytesWritten = nowBytes;
            _lastBandwidthMeasure = DateTime.Now;
        }

        // Health score: 0-100
        var onlineRatio = _health.TotalCount > 0 ? (double)_health.OnlineCount / _health.TotalCount : 1;
        var recordingRatio = activeCount > 0 ? Math.Min(1.0, (double)activeCount / Math.Max(1, _health.OnlineCount)) : 0;
        var storageRatio = _status.DiskTotalBytes > 0
            ? Math.Clamp((double)(_status.DiskTotalBytes - _status.DiskFreeBytes) / _status.DiskTotalBytes, 0, 1)
            : 0;
        var healthScore = (int)(onlineRatio * 40 + recordingRatio * 30 + (1 - storageRatio) * 30);
        HealthScoreText.Text = healthScore.ToString();
        HealthScoreText.Foreground = healthScore switch {
            >= 80 => Brushes.LimeGreen,
            >= 50 => Brushes.Orange,
            _ => Brushes.OrangeRed,
        };
        HealthDetailOnline.Text = $"{_health.OnlineCount}/{_health.TotalCount} 上線";
        HealthDetailRecording.Text = $"{activeCount} 路錄影";

        // Uptime
        var uptime = DateTime.Now - _metrics.AppStartTime;
        var uptimeStr = uptime.Days > 0
            ? $"{uptime.Days}天 {uptime.Hours}小時 {uptime.Minutes}分鐘"
            : $"{uptime.Hours}小時 {uptime.Minutes}分鐘";
        BandwidthText.Text = $"已執行 {uptimeStr}";
        HealthDetailStorage.Text = $"儲存 {(1 - storageRatio) * 100:F0}%";

        // Storage bar
        StorageTotalText.Text = $"{diskTotalGB:F0} GB 總容量";
        var usedPct = (int)(storageRatio * 100);
        StorageUsedPercentText.Text = $"已使用 {usedPct}%（{diskTotalGB - diskFreeGB:F1} GB）";
        StorageBarFill.Width = Math.Clamp(storageRatio * 200, 0, 200);
        StorageBarFill.Background = usedPct switch {
            >= 90 => Brushes.OrangeRed,
            >= 75 => Brushes.Orange,
            _ => TryFindResource("PrimaryBrush") as Brush ?? Brushes.DodgerBlue,
        };
        StorageBreakdownText.Text = $"{diskFreeGB:F1} GB 可用 ｜ {diskTotalGB - diskFreeGB:F1} GB 已使用";

        // Alert stats for today
        try {
            var today = DateTime.Today;
            var allEvents = _eventLog.QueryEvents(null, null, null, 5000);
            var todayEvents = allEvents.Where(e => e.Timestamp.Date == today.Date).ToList();
            AlertCountText.Text = todayEvents.Count(e => e.Severity == "ERROR").ToString();
            WarningCountText.Text = todayEvents.Count(e => e.Severity == "WARN").ToString();
            InfoCountText.Text = todayEvents.Count(e => e.Severity == "INFO").ToString();
            AlertStatsText.Text = $"共 {todayEvents.Count} 筆事件 ｜ {today:MM/dd}";
        } catch {
            AlertCountText.Text = "—";
            WarningCountText.Text = "—";
            InfoCountText.Text = "—";
        }
    }

    private void RefreshRecordingStats() {
        var active = _recording.GetActiveRecordings().Count;
        RecordingCountText.Text = active.ToString();
        StorageUsageText.Text = active > 0 ? $"{active} 路錄影中" : "無進行中錄影";

        // Recording success rate
        var watchdogRestarts = _watchdog.RestartedCount;
        var estimatedTotal = Math.Max(1, active + watchdogRestarts);
        var successRate = Math.Min(100, (double)active / estimatedTotal * 100);
        RecordingSuccessText.Text = $"{successRate:F0}%";
        RecordingSuccessText.Foreground = successRate switch {
            >= 95 => Brushes.LimeGreen,
            >= 80 => Brushes.Orange,
            _ => Brushes.OrangeRed,
        };
        RecordingSuccessDetail.Text = $"{active} 路正常 ｜ {watchdogRestarts} 次恢復";
        RecordingFailoverText.Text = $"緩衝：{App.Services.GetRequiredService<IDisconnectBufferService>().BufferedCount} 次 ｜ 已寫入：{App.Services.GetRequiredService<IDisconnectBufferService>().FlushedCount} 片段";
    }

    private void RefreshEvents() {
        var events = _eventLog.GetRecentEvents(50);
        RecentEventsGrid.ItemsSource = events;
    }

    private void RenderCalendar() {
        CalendarLoadingText.Visibility = Visibility.Visible;
        _ = RefreshCalendarAsync();
    }

    private async Task RefreshCalendarAsync() {
        try {
            CalendarMonthText.Text = $"{_calendarMonth:yyyy年M月}";

            var monthEnd = _calendarMonth.AddMonths(1);
            var cameraList = _cameraService.GetAllCameras();
            var allCams = new List<string>(cameraList.Count);
            foreach (var cam in cameraList) {
                if (cam.IsEnabled) { allCams.Add(cam.Id); }
            }

            var dailyData = new Dictionary<DateTime, (int SegmentCount, long TotalBytes)>();
            try {
                dailyData = await _videoIndex.GetDailyRecordingStatsAsync(_calendarMonth, monthEnd, null);
            } catch { }

            var daysInMonth = DateTime.DaysInMonth(_calendarMonth.Year, _calendarMonth.Month);
            var firstDayOfWeek = (int)_calendarMonth.DayOfWeek;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (var c = 0; c < 7; c++) {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            // Header row
            var headerRow = new RowDefinition { Height = GridLength.Auto };
            grid.RowDefinitions.Add(headerRow);
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            string[] dayNames = ["週日", "週一", "週二", "週三", "週四", "週五", "週六"];
            var weekLabel = new TextBlock {
                Text = "",
                FontSize = 10,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                Margin = new Thickness(0, 0, 4, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(weekLabel, 0);
            Grid.SetColumn(weekLabel, 0);
            grid.Children.Add(weekLabel);

            for (var d = 0; d < 7; d++) {
                var dayHeader = new TextBlock {
                    Text = dayNames[d],
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("SecondaryTextBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(1, 0, 1, 4)
                };
                Grid.SetRow(dayHeader, 0);
                Grid.SetColumn(dayHeader, d + 1);
                grid.Children.Add(dayHeader);
            }

            // Calendar rows
            var totalCells = firstDayOfWeek + daysInMonth;
            var totalRows = (totalCells + 6) / 7;
            var cellIdx = 0;

            for (var r = 0; r < totalRows; r++) {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }

            for (var r = 0; r < totalRows; r++) {
                // Week number
                var weekNum = GetWeekOfMonth(_calendarMonth.Year, _calendarMonth.Month, r, firstDayOfWeek);
                var weekText = new TextBlock {
                    Text = $"{weekNum}",
                    FontSize = 9,
                    Foreground = (Brush)FindResource("SecondaryTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 2, 0)
                };
                Grid.SetRow(weekText, 2 + r);
                Grid.SetColumn(weekText, 0);
                grid.Children.Add(weekText);

                for (var c = 0; c < 7; c++) {
                    var dayNum = cellIdx - firstDayOfWeek + 1;

                    var cellBorder = new Border {
                        Margin = new Thickness(1),
                        Padding = new Thickness(2),
                        CornerRadius = new CornerRadius(3),
                        MinHeight = 36
                    };

                    if (dayNum >= 1 && dayNum <= daysInMonth) {
                        var dayDate = new DateTime(_calendarMonth.Year, _calendarMonth.Month, dayNum);

                        var isToday = dayDate.Date == DateTime.Today;
                        var hasRecording = dailyData.TryGetValue(dayDate, out var dayStats);
                        var hasAllCameras = allCams.Count > 0;

                        // Background: recording presence
                        if (hasRecording) {
                            var density = Math.Min(1.0, dayStats.SegmentCount / (double)(allCams.Count * 24));
                            var green = (byte)(80 + (int)(density * 120));
                            cellBorder.Background = new SolidColorBrush(Color.FromArgb(60, 0, green, 0));
                            cellBorder.BorderThickness = new Thickness(1);
                            cellBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 0, green, 0));
                        } else {
                            cellBorder.Background = new SolidColorBrush(Color.FromArgb(10, 0xFF, 0xFF, 0xFF));
                        }

                        if (isToday) {
                            cellBorder.BorderThickness = new Thickness(2);
                            cellBorder.BorderBrush = (Brush)FindResource("PrimaryBrush") ?? Brushes.DodgerBlue;
                        }

                        var dayStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                        dayStack.Children.Add(new TextBlock {
                            Text = dayNum.ToString(),
                            FontSize = 11,
                            FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                            Foreground = isToday
                                ? (Brush)FindResource("PrimaryBrush")
                                : (Brush)FindResource("TextBrush"),
                            HorizontalAlignment = HorizontalAlignment.Center
                        });

                        if (hasRecording) {
                            var (SegmentCount, TotalBytes) = dayStats;
                            var infoText = new TextBlock {
                                Text = $"{SegmentCount}",
                                FontSize = 8,
                                Foreground = (Brush)FindResource("SuccessBrush"),
                                HorizontalAlignment = HorizontalAlignment.Center
                            };
                            dayStack.Children.Add(infoText);
                        }

                        cellBorder.Child = dayStack;
                        var clickedDate = dayDate;
                        cellBorder.MouseDown += (_, _) => {
                            try {
                                if (Window.GetWindow(this) is MainWindow mw)
                                    mw.SwitchToLive(clickedDate);
                            } catch { }
                        };
                        cellBorder.Cursor = System.Windows.Input.Cursors.Hand;
                    } else {
                        cellBorder.Background = Brushes.Transparent;
                    }

                    Grid.SetRow(cellBorder, 2 + r);
                    Grid.SetColumn(cellBorder, c + 1);
                    grid.Children.Add(cellBorder);

                    cellIdx++;
                }
            }

            CalendarGridContainer.Child = grid;
        } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"[HeliVMS] Calendar render error: {ex.Message}");
        } finally {
            CalendarLoadingText.Visibility = Visibility.Collapsed;
        }
    }

    private static int GetWeekOfMonth(int year, int month, int row, int firstDayOfWeek) {
        // Calculate the actual ISO week number of the first date in this row
        _ = new DateTime(year, month, 1);
        var firstDateOfRow = row * 7 - firstDayOfWeek + 1;
        if (firstDateOfRow < 1) { return 0; } // padding row
        if (firstDateOfRow > DateTime.DaysInMonth(year, month)) { return 0; }
        var dt = new DateTime(year, month, firstDateOfRow);
        return System.Globalization.CultureInfo.CurrentCulture.Calendar.GetWeekOfYear(
            dt, System.Globalization.CalendarWeekRule.FirstDay, DayOfWeek.Sunday);
    }

    private void CalendarPrevBtn_Click(object sender, RoutedEventArgs e) {
        _calendarMonth = _calendarMonth.AddMonths(-1);
        RenderCalendar();
    }

    private void CalendarNextBtn_Click(object sender, RoutedEventArgs e) {
        _calendarMonth = _calendarMonth.AddMonths(1);
        RenderCalendar();
    }

    private void CalendarTodayBtn_Click(object sender, RoutedEventArgs e) {
        _calendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RenderCalendar();
    }
}
