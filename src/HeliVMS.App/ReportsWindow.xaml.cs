using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HeliVMS.Alarms;
using HeliVMS.Storage;
using Path = System.IO.Path;

namespace HeliVMS.App;

/// <summary>統圖報表視窗（M60，§14.7 #9）：錄影時數／斷線次數／容量趨勢／AI 事件統計，可匯出或寄送 CSV（含排程）。</summary>
public partial class ReportsWindow : Window
{
    private static readonly string[] Periods = { "近24小時", "近7日", "近30日", "全部" };
    private static readonly ReportCadence[] Cadences = { ReportCadence.Daily, ReportCadence.Weekly };
    private static readonly DayOfWeek[] WeekDays =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };

    private readonly SqliteStore _store;
    private readonly string _dataRoot;
    private readonly ReportRepository _reports;
    private readonly SettingsRepository _settings;
    private string _period = "近7日";
    private IReadOnlyList<CapacityTrendRow> _lastTrend = [];

    public ReportsWindow(SqliteStore store, string dataRoot)
    {
        _store = store;
        _dataRoot = dataRoot;
        _reports = new ReportRepository(store);
        _settings = new SettingsRepository(store);
        InitializeComponent();

        ReportPeriodCombo.ItemsSource = Periods;
        ReportPeriodCombo.SelectedIndex = 1;
        ReportPeriodCombo.SelectionChanged += (_, _) =>
        {
            _period = ReportPeriodCombo.SelectedItem?.ToString() ?? _period;
            Refresh();
        };

        ReportCadenceCombo.ItemsSource = Cadences;
        ReportCadenceCombo.SelectedIndex = 0;
        ReportWeeklyDayCombo.ItemsSource = WeekDays;
        ReportScheduleCheck.Checked += (_, _) => UpdateScheduleStatus();
        ReportScheduleCheck.Unchecked += (_, _) => UpdateScheduleStatus();
        ReportCadenceCombo.SelectionChanged += (_, _) => UpdateScheduleStatus();
        ReportWeeklyDayCombo.SelectionChanged += (_, _) => UpdateScheduleStatus();

        // 版面上 Canvas 的 ActualWidth 在載入前為 0；尺寸確定後補畫一次。
        ReportTrendCanvas.SizeChanged += (_, _) => DrawTrendChart();

        LoadSchedule();
        Refresh();
    }

    private (DateTime From, DateTime To) Range()
    {
        var to = DateTime.UtcNow;
        return _period switch
        {
            "近24小時" => (to.AddHours(-24), to),
            "近30日" => (to.AddDays(-30), to),
            "全部" => (new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.MaxValue),
            _ => (to.AddDays(-7), to),
        };
    }

    private void Refresh()
    {
        var (from, to) = Range();
        var recording = _reports.ListRecordingSummary(from, to);
        var disconnect = _reports.GetDisconnectCount(from, to);
        var trend = _reports.ListCapacityTrend(from, to);
        var ai = _reports.ListAiEventSummary(from, to);

        var totalHours = recording.Sum(r => r.Hours);
        var totalBytes = recording.Sum(r => r.Bytes);
        ReportRecordingText.Text = $"總計：{totalHours:F2} 小時 / {FormatBytes(totalBytes)}（{recording.Count} 頻道）\n" +
            string.Join("\n", recording.Select(r => $"{r.ChannelName}: {r.Hours:F2} 小時 / {FormatBytes(r.Bytes)}"));
        ReportDisconnectText.Text = $"{disconnect} 次斷線";
        ReportTrendText.Text = trend.Count == 0
            ? "（無數據）"
            : string.Join("\n", trend.Select(r => $"{r.Day}: {FormatBytes(r.Bytes)} / {r.Hours:F2} 小時"));
        ReportAiText.Text = ai.Count == 0
            ? "（無事件）"
            : string.Join("\n", ai.Select(r => $"{r.EventType}: {r.Count} 次"));
        ReportStatusText.Text = $"已產生報表（{_period}）。";

        _lastTrend = trend;
        DrawTrendChart();
    }

    /// <summary>容量趨勢長條圖（§14.1 #9）：以逐日位元組相對最大值繪製，滑鼠移入顯示當日明細。</summary>
    private void DrawTrendChart()
    {
        ReportTrendCanvas.Children.Clear();
        if (_lastTrend.Count == 0)
        {
            return;
        }

        var width = ReportTrendCanvas.ActualWidth > 1 ? ReportTrendCanvas.ActualWidth : 640;
        var height = ReportTrendCanvas.ActualHeight > 1 ? ReportTrendCanvas.ActualHeight : 140;
        const double padding = 6;
        const double labelSpace = 4;

        var max = _lastTrend.Max(r => r.Bytes);
        if (max <= 0)
        {
            max = 1;
        }

        var slot = (width - padding * 2) / _lastTrend.Count;
        var barWidth = Math.Max(2, slot * 0.7);
        for (var i = 0; i < _lastTrend.Count; i++)
        {
            var row = _lastTrend[i];
            var barHeight = Math.Max(1, (height - padding * 2 - labelSpace) * (row.Bytes / (double)max));
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                Fill = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
                ToolTip = $"{row.Day}: {FormatBytes(row.Bytes)} / {row.Hours:F2} 小時",
            };
            Canvas.SetLeft(bar, padding + slot * i + (slot - barWidth) / 2);
            Canvas.SetTop(bar, height - padding - barHeight);
            ReportTrendCanvas.Children.Add(bar);
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => Refresh();

    private void OnExportClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = WriteReportCsv();
            ReportStatusText.Text = $"已匯出：{path}";
        }
        catch (Exception ex)
        {
            ReportStatusText.Text = $"匯出失敗：{ex.Message}";
        }
    }

    /// <summary>
    /// 以目前的期間產生 CSV 並回傳落地路徑（匯出、手動寄送與背景排程共用同一個 <see cref="ReportCsv"/>）。
    /// </summary>
    private string WriteReportCsv()
    {
        var (from, to) = Range();
        var csv = ReportCsv.Build(
            _reports.ListRecordingSummary(from, to),
            _reports.GetDisconnectCount(from, to),
            _reports.ListCapacityTrend(from, to),
            _reports.ListAiEventSummary(from, to));

        var dir = Path.Combine(_dataRoot, "reports");
        Directory.CreateDirectory(dir);
        var name = $"report-{_period}-{DateTime.UtcNow:yyyyMMddHHmmss}.csv";
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, csv, new UTF8Encoding(true)); // UTF-8 BOM（Excel 相容）
        return path;
    }

    /// <summary>
    /// 以設定中心的 SMTP 組態（notify.smtp.*）寄送目前報表（§14.1 #9 手動寄送）。
    /// 沿用事件通知的帳密與收件者；未設定或寄送失敗都只更新狀態列，不讓 UI 崩潰。
    /// </summary>
    private async void OnEmailClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = NotificationSettings.Load(_settings);
            if (!cfg.SmtpEnabled)
            {
                ReportStatusText.Text = "未啟用 SMTP（設定中心 → 通知）。";
                return;
            }

            var (from, to) = Range();
            var path = WriteReportCsv();
            var subject = $"HeliVMS 報表（{_period}，{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}）";
            var body = $"HeliVMS 統圖報表（{_period}）\n" +
                $"期間：{from:yyyy-MM-dd HH:mm:ss} ~ {to:yyyy-MM-dd HH:mm:ss} UTC\n" +
                "附件為 CSV。\n";

            var ok = await new SmtpNotifier().SendReportAsync(cfg, subject, body, path);
            ReportStatusText.Text = ok
                ? $"已寄送報表至 {string.Join(", ", cfg.SmtpTo)}"
                : "寄送失敗（請檢查 設定中心 → 通知 的 SMTP 設定）。";
        }
        catch (Exception ex)
        {
            ReportStatusText.Text = $"寄送失敗：{ex.Message}";
        }
    }

    /// <summary>把 app_settings 的排程設定載入控制項。</summary>
    private void LoadSchedule()
    {
        var enabledRaw = _settings.GetOrDefault(ReportSchedule.EnabledKey, "0");
        ReportScheduleCheck.IsChecked = enabledRaw == "1" || (bool.TryParse(enabledRaw, out var b) && b);
        var cadence = ReportSchedule.ParseCadence(_settings.Get(ReportSchedule.CadenceKey), ReportCadence.Daily);
        ReportCadenceCombo.SelectedItem = cadence == ReportCadence.Weekly ? ReportCadence.Weekly : ReportCadence.Daily;
        ReportWeeklyDayCombo.SelectedItem =
            ReportSchedule.TryParseDay(_settings.Get(ReportSchedule.WeeklyDayKey), out var day) ? day : DayOfWeek.Monday;
        ReportScheduleTimeBox.Text =
            ReportSchedule.TryParseTime(_settings.Get(ReportSchedule.TimeKey), out var at)
                ? at.ToString("HH\\:mm")
                : ReportSchedule.DefaultTime;
        UpdateScheduleStatus();
    }

    private void OnSaveScheduleClicked(object sender, RoutedEventArgs e)
    {
        if (!ReportSchedule.TryParseTime(ReportScheduleTimeBox.Text, out var at))
        {
            ReportScheduleStatusText.Text = "時間格式須為 HH:mm（例如 08:00）。";
            return;
        }

        var enabled = ReportScheduleCheck.IsChecked == true;
        var cadence = ReportCadenceCombo.SelectedItem is ReportCadence c ? c : ReportCadence.Daily;
        var day = ReportWeeklyDayCombo.SelectedItem is DayOfWeek d ? d : DayOfWeek.Monday;

        _settings.Set(ReportSchedule.EnabledKey, enabled ? "1" : "0");
        _settings.Set(ReportSchedule.CadenceKey, ReportSchedule.ToKey(enabled ? cadence : ReportCadence.Off));
        _settings.Set(ReportSchedule.TimeKey, at.ToString("HH\\:mm"));
        _settings.Set(ReportSchedule.WeeklyDayKey, day.ToString());

        if (enabled && !NotificationSettings.Load(_settings).SmtpEnabled)
        {
            ReportScheduleStatusText.Text = "已儲存；但 SMTP 尚未設定（設定中心 → 通知），排程不會寄出。";
            return;
        }

        UpdateScheduleStatus();
    }

    private void UpdateScheduleStatus()
    {
        if (ReportScheduleCheck.IsChecked != true)
        {
            ReportScheduleStatusText.Text = "排程未啟用。";
            return;
        }

        var cadence = ReportCadenceCombo.SelectedItem is ReportCadence c ? c : ReportCadence.Daily;
        var day = ReportWeeklyDayCombo.SelectedItem is DayOfWeek d ? d : DayOfWeek.Monday;
        var at = ReportSchedule.TryParseTime(ReportScheduleTimeBox.Text, out var t) ? t : new TimeOnly(8, 0);
        var next = ReportSchedule.NextDueLocal(DateTime.Now, cadence, at, day);
        ReportScheduleStatusText.Text = next is { } n
            ? $"排程：{ReportSchedule.ToKey(cadence)} {at:HH\\:mm}；下次寄送 {n:yyyy-MM-dd HH:mm}"
            : "排程未啟用。";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30)
        {
            return $"{bytes / (double)(1L << 30):F2} GB";
        }

        if (bytes >= 1L << 20)
        {
            return $"{bytes / (double)(1L << 20):F2} MB";
        }

        if (bytes >= 1L << 10)
        {
            return $"{bytes / (double)(1L << 10):F2} KB";
        }

        return $"{bytes} B";
    }
}
