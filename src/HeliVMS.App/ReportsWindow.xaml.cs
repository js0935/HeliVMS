using System.Globalization;
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
    private readonly SqliteStore _store;
    private readonly string _dataRoot;
    private readonly ReportRepository _reports;
    private readonly SettingsRepository _settings;
    private string _periodKey = "7d";
    private IReadOnlyList<CapacityTrendRow> _lastTrend = [];

    private sealed record PeriodOption(string Key, string Label);

    private sealed record CadenceOption(ReportCadence Value, string Label);

    private sealed record DayOption(DayOfWeek Day, string Label);

    public ReportsWindow(SqliteStore store, string dataRoot)
    {
        _store = store;
        _dataRoot = dataRoot;
        _reports = new ReportRepository(store);
        _settings = new SettingsRepository(store);
        InitializeComponent();
        ApplyI18n();

        ReportPeriodCombo.ItemsSource = new[]
        {
            new PeriodOption("24h", PeriodText("24h")),
            new PeriodOption("7d", PeriodText("7d")),
            new PeriodOption("30d", PeriodText("30d")),
            new PeriodOption("all", PeriodText("all")),
        };
        ReportPeriodCombo.SelectedIndex = 1;
        ReportPeriodCombo.SelectionChanged += (_, _) =>
        {
            _periodKey = (ReportPeriodCombo.SelectedItem as PeriodOption)?.Key ?? _periodKey;
            Refresh();
        };

        ReportCadenceCombo.ItemsSource = new[]
        {
            new CadenceOption(ReportCadence.Daily, Localizer.T("Report.CadenceDaily")),
            new CadenceOption(ReportCadence.Weekly, Localizer.T("Report.CadenceWeekly")),
        };
        ReportCadenceCombo.SelectedIndex = 0;
        ReportWeeklyDayCombo.ItemsSource = new[]
        {
            new DayOption(DayOfWeek.Monday, Localizer.T("Report.WeekdayMon")),
            new DayOption(DayOfWeek.Tuesday, Localizer.T("Report.WeekdayTue")),
            new DayOption(DayOfWeek.Wednesday, Localizer.T("Report.WeekdayWed")),
            new DayOption(DayOfWeek.Thursday, Localizer.T("Report.WeekdayThu")),
            new DayOption(DayOfWeek.Friday, Localizer.T("Report.WeekdayFri")),
            new DayOption(DayOfWeek.Saturday, Localizer.T("Report.WeekdaySat")),
            new DayOption(DayOfWeek.Sunday, Localizer.T("Report.WeekdaySun")),
        };
        ReportScheduleCheck.Checked += (_, _) => UpdateScheduleStatus();
        ReportScheduleCheck.Unchecked += (_, _) => UpdateScheduleStatus();
        ReportCadenceCombo.SelectionChanged += (_, _) => UpdateScheduleStatus();
        ReportWeeklyDayCombo.SelectionChanged += (_, _) => UpdateScheduleStatus();

        // 版面上 Canvas 的 ActualWidth 在載入前為 0；尺寸確定後補畫一次。
        ReportTrendCanvas.SizeChanged += (_, _) => DrawTrendChart();

        LoadSchedule();
        Refresh();
    }

    /// <summary>依現況語言套用標題與靜態欄位文字（M57）。新開視窗以新語言顯示。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Report.Title");
        HeadingText.Text = Localizer.T("Report.Heading");
        PeriodLabel.Text = Localizer.T("Report.Period");
        ReportRefreshButton.Content = Localizer.T("Report.Refresh");
        ReportRefreshButton.ToolTip = Localizer.T("Report.RefreshTip");
        ReportExportButton.Content = Localizer.T("Report.Export");
        ReportExportButton.ToolTip = Localizer.T("Report.ExportTip");
        ReportEmailButton.Content = Localizer.T("Report.Email");
        ReportEmailButton.ToolTip = Localizer.T("Report.EmailTip");
        ReportScheduleCheck.Content = Localizer.T("Report.ScheduleEnable");
        ReportCadenceCombo.ToolTip = Localizer.T("Report.CadenceTip");
        WeekdayLabel.Text = Localizer.T("Report.Weekday");
        TimeLabel.Text = Localizer.T("Report.TimeLabel");
        ReportScheduleTimeBox.ToolTip = Localizer.T("Report.TimeTip");
        ReportScheduleSaveButton.Content = Localizer.T("Report.SaveSchedule");
        ReportScheduleSaveButton.ToolTip = Localizer.T("Report.SaveTip");
        RecordingLabel.Text = Localizer.T("Report.Recording");
        DisconnectLabel.Text = Localizer.T("Report.Disconnect");
        TrendLabel.Text = Localizer.T("Report.Trend");
        AiLabel.Text = Localizer.T("Report.Ai");
    }

    private static string PeriodKeyToI18n(string key) => key switch
    {
        "24h" => "Report.Period24h",
        "30d" => "Report.Period30d",
        "all" => "Report.PeriodAll",
        _ => "Report.Period7d",
    };

    private static string PeriodText(string key) => Localizer.T(PeriodKeyToI18n(key));

    private static string CadenceLabel(ReportCadence cadence) =>
        Localizer.T(cadence == ReportCadence.Weekly ? "Report.CadenceWeekly" : "Report.CadenceDaily");

    private (DateTime From, DateTime To) Range()
    {
        var to = DateTime.UtcNow;
        return _periodKey switch
        {
            "24h" => (to.AddHours(-24), to),
            "30d" => (to.AddDays(-30), to),
            "all" => (new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.MaxValue),
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
        var lines = string.Join(
            "\n",
            recording.Select(r => string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Report.RecordingLine"), r.ChannelName, r.Hours, FormatBytes(r.Bytes))));
        ReportRecordingText.Text =
            string.Format(
                CultureInfo.InvariantCulture,
                Localizer.T("Report.RecordingSummary"),
                totalHours,
                FormatBytes(totalBytes),
                recording.Count)
            + (lines.Length == 0 ? string.Empty : "\n" + lines);
        ReportDisconnectText.Text = string.Format(
            CultureInfo.InvariantCulture, Localizer.T("Report.DisconnectCount"), disconnect);
        ReportTrendText.Text = trend.Count == 0
            ? Localizer.T("Report.NoData")
            : string.Join(
                "\n",
                trend.Select(r => string.Format(
                    CultureInfo.InvariantCulture, Localizer.T("Report.TrendLine"), r.Day, FormatBytes(r.Bytes), r.Hours)));
        ReportAiText.Text = ai.Count == 0
            ? Localizer.T("Report.NoEvents")
            : string.Join(
                "\n",
                ai.Select(r => string.Format(
                    CultureInfo.InvariantCulture, Localizer.T("Report.AiLine"), r.EventType, r.Count)));
        ReportStatusText.Text = string.Format(
            CultureInfo.InvariantCulture, Localizer.T("Report.Generated"), PeriodText(_periodKey));

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
                ToolTip = string.Format(
                    CultureInfo.InvariantCulture, Localizer.T("Report.TrendLine"), row.Day, FormatBytes(row.Bytes), row.Hours),
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
            ReportStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Report.Exported"), path);
        }
        catch (Exception ex)
        {
            ReportStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Report.ExportFailed"), ex.Message);
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
        var name = $"report-{_periodKey}-{DateTime.UtcNow:yyyyMMddHHmmss}.csv";
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
                ReportStatusText.Text = Localizer.T("Report.SmtpNotEnabled");
                return;
            }

            var (from, to) = Range();
            var path = WriteReportCsv();
            var subject = string.Format(
                CultureInfo.InvariantCulture,
                Localizer.T("Report.EmailSubject"),
                PeriodText(_periodKey),
                from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var body = string.Format(
                CultureInfo.InvariantCulture,
                Localizer.T("Report.EmailBody"),
                PeriodText(_periodKey),
                from.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                to.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

            var ok = await new SmtpNotifier().SendReportAsync(cfg, subject, body, path);
            ReportStatusText.Text = ok
                ? string.Format(CultureInfo.InvariantCulture, Localizer.T("Report.EmailSent"), string.Join(", ", cfg.SmtpTo))
                : Localizer.T("Report.EmailFailed");
        }
        catch (Exception ex)
        {
            ReportStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Report.EmailFailedDetail"), ex.Message);
        }
    }

    /// <summary>把 app_settings 的排程設定載入控制項。</summary>
    private void LoadSchedule()
    {
        var enabledRaw = _settings.GetOrDefault(ReportSchedule.EnabledKey, "0");
        ReportScheduleCheck.IsChecked = enabledRaw == "1" || (bool.TryParse(enabledRaw, out var b) && b);
        var cadence = ReportSchedule.ParseCadence(_settings.Get(ReportSchedule.CadenceKey), ReportCadence.Daily);
        SelectCadence(cadence == ReportCadence.Weekly ? ReportCadence.Weekly : ReportCadence.Daily);
        SelectDay(ReportSchedule.TryParseDay(_settings.Get(ReportSchedule.WeeklyDayKey), out var day) ? day : DayOfWeek.Monday);
        ReportScheduleTimeBox.Text =
            ReportSchedule.TryParseTime(_settings.Get(ReportSchedule.TimeKey), out var at)
                ? at.ToString("HH\\:mm", CultureInfo.InvariantCulture)
                : ReportSchedule.DefaultTime;
        UpdateScheduleStatus();
    }

    private void SelectCadence(ReportCadence cadence)
    {
        foreach (var item in ReportCadenceCombo.Items)
        {
            if (item is CadenceOption option && option.Value == cadence)
            {
                ReportCadenceCombo.SelectedItem = option;
                return;
            }
        }

        ReportCadenceCombo.SelectedIndex = 0;
    }

    private void SelectDay(DayOfWeek day)
    {
        foreach (var item in ReportWeeklyDayCombo.Items)
        {
            if (item is DayOption option && option.Day == day)
            {
                ReportWeeklyDayCombo.SelectedItem = option;
                return;
            }
        }

        ReportWeeklyDayCombo.SelectedIndex = 0;
    }

    private void OnSaveScheduleClicked(object sender, RoutedEventArgs e)
    {
        if (!ReportSchedule.TryParseTime(ReportScheduleTimeBox.Text, out var at))
        {
            ReportScheduleStatusText.Text = Localizer.T("Report.TimeFormatError");
            return;
        }

        var enabled = ReportScheduleCheck.IsChecked == true;
        var cadence = ReportCadenceCombo.SelectedItem is CadenceOption c ? c.Value : ReportCadence.Daily;
        var day = ReportWeeklyDayCombo.SelectedItem is DayOption d ? d.Day : DayOfWeek.Monday;

        _settings.Set(ReportSchedule.EnabledKey, enabled ? "1" : "0");
        _settings.Set(ReportSchedule.CadenceKey, ReportSchedule.ToKey(enabled ? cadence : ReportCadence.Off));
        _settings.Set(ReportSchedule.TimeKey, at.ToString("HH\\:mm", CultureInfo.InvariantCulture));
        _settings.Set(ReportSchedule.WeeklyDayKey, day.ToString());

        if (enabled && !NotificationSettings.Load(_settings).SmtpEnabled)
        {
            ReportScheduleStatusText.Text = Localizer.T("Report.ScheduleSavedNoSmtp");
            return;
        }

        UpdateScheduleStatus();
    }

    private void UpdateScheduleStatus()
    {
        if (ReportScheduleCheck.IsChecked != true)
        {
            ReportScheduleStatusText.Text = Localizer.T("Report.ScheduleOff");
            return;
        }

        var cadence = ReportCadenceCombo.SelectedItem is CadenceOption c ? c.Value : ReportCadence.Daily;
        var day = ReportWeeklyDayCombo.SelectedItem is DayOption d ? d.Day : DayOfWeek.Monday;
        var at = ReportSchedule.TryParseTime(ReportScheduleTimeBox.Text, out var t) ? t : new TimeOnly(8, 0);
        var next = ReportSchedule.NextDueLocal(DateTime.Now, cadence, at, day);
        ReportScheduleStatusText.Text = next is { } n
            ? string.Format(
                CultureInfo.InvariantCulture,
                Localizer.T("Report.ScheduleNext"),
                CadenceLabel(cadence),
                at.ToString("HH\\:mm", CultureInfo.InvariantCulture),
                n.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            : Localizer.T("Report.ScheduleOff");
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
