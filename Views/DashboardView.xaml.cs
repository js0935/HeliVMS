using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HeliVMS.Views;

public partial class DashboardView : UserControl {
    private readonly ICameraHealthService _health;
    private readonly ISystemStatusService _status;
    private readonly IEventService _eventLog;
    private readonly IRecordingService _recording;
    private readonly IRecordingIntegrityService _integrity;
    private readonly IVideoIndexService _videoIndex;
    private readonly ICameraService _cameraService;
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private bool _loaded;

    public DashboardView() {
        InitializeComponent();
        _health = App.Services.GetRequiredService<ICameraHealthService>();
        _status = App.Services.GetRequiredService<ISystemStatusService>();
        _eventLog = App.Services.GetRequiredService<IEventService>();
        _recording = App.Services.GetRequiredService<IRecordingService>();
        _integrity = App.Services.GetRequiredService<IRecordingIntegrityService>();
        _videoIndex = App.Services.GetRequiredService<IVideoIndexService>();
        _cameraService = App.Services.GetRequiredService<ICameraService>();

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
        _integrity.StatsChanged += () => // REVIEW: lambda captures 'this' — consider weak event pattern
        {
            if (_loaded) { _ = Dispatcher.InvokeAsync(RefreshIntegrityStats); }
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
        RefreshIntegrityStats();
        RefreshEvents();
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
        var diskText = $"{_status.DiskFreeBytes / (1024 * 1024 * 1024)} GB 可用";
        DiskUsageText.Text = diskText;
    }

    private void RefreshRecordingStats() {
        var active = _recording.GetActiveRecordings().Count;
        RecordingCountText.Text = active.ToString();
        StorageUsageText.Text = active > 0 ? $"{active} 路錄影中" : "無進行中錄影";
    }

    private void RefreshIntegrityStats() {
        CheckedSegmentsText.Text = _integrity.CheckedSegments.ToString();
        CorruptedSegmentsText.Text = _integrity.CorruptedSegments > 0
            ? $"{_integrity.CorruptedSegments} 個損毀（共 {_integrity.TotalSegments} 片段）"
            : $"全部正常（共 {_integrity.TotalSegments} 片段）";
    }

    private async void IntegrityCheckBtn_Click(object sender, RoutedEventArgs e) {
        IntegrityCheckBtn.IsEnabled = false;
        IntegrityCheckBtn.Content = "掃描檔案中…";
        try {
            var storagePath = _recording.GetBasePath();
            var (Added, Deleted) = await Task.Run(async () => await _videoIndex.RebuildRecordingIndexAsync(storagePath));
            if (Added > 0 || Deleted > 0) {
                IntegrityCheckBtn.Content = "檢查完整性…";
            }
            await Task.Run(async () => await _integrity.ForceCheck());
        } catch (Exception ex) {
            Serilog.Log.Error(ex, "[Dashboard] Integrity check failed");
        } finally {
            IntegrityCheckBtn.IsEnabled = true;
            IntegrityCheckBtn.Content = "🔍 立即檢查";
            RefreshIntegrityStats();
        }
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
