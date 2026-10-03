using System.IO;
using System.Text;
using System.Windows;
using HeliVMS.Alarms;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>統圖報表視窗（M60，§14.7 #9）：錄影時數／斷線次數／容量趨勢／AI 事件統計，可匯出 CSV。</summary>
public partial class ReportsWindow : Window
{
    private readonly SqliteStore _store;
    private readonly string _dataRoot;
    private readonly ReportRepository _reports;
    private string _period = "近7日";

    public ReportsWindow(SqliteStore store, string dataRoot)
    {
        _store = store;
        _dataRoot = dataRoot;
        _reports = new ReportRepository(store);
        InitializeComponent();

        ReportPeriodCombo.ItemsSource = new[] { "近24小時", "近7日", "近30日", "全部" };
        ReportPeriodCombo.SelectedIndex = 1;
        ReportPeriodCombo.SelectionChanged += (_, _) =>
        {
            _period = ReportPeriodCombo.SelectedItem?.ToString() ?? _period;
            Refresh();
        };

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
    /// 以目前的期間產生 CSV 並回傳落地路徑（匯出與寄送共用，避免兩份欄位順序漂移）。
    /// </summary>
    private string WriteReportCsv()
    {
        var (from, to) = Range();
        var recording = _reports.ListRecordingSummary(from, to);
        var disconnect = _reports.GetDisconnectCount(from, to);
        var trend = _reports.ListCapacityTrend(from, to);
        var ai = _reports.ListAiEventSummary(from, to);

        var sb = new StringBuilder();
        sb.AppendLine("類別,項目,值1,值2");
        foreach (var r in recording)
        {
            sb.AppendLine($"錄影時數,{Csv(r.ChannelName)},{r.Hours:F2}小時,{r.Bytes}位元組");
        }

        sb.AppendLine($"斷線次數,offline,{disconnect},");

        foreach (var r in trend)
        {
            sb.AppendLine($"容量趨勢,{r.Day},{r.Hours:F2}小時,{r.Bytes}位元組");
        }

        foreach (var r in ai)
        {
            sb.AppendLine($"AI事件統計,{Csv(r.EventType)},{r.Count},");
        }

        var dir = Path.Combine(_dataRoot, "reports");
        Directory.CreateDirectory(dir);
        var name = $"report-{_period}-{DateTime.UtcNow:yyyyMMddHHmmss}.csv";
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // UTF-8 BOM（Excel 相容）
        return path;
    }

    /// <summary>
    /// 以設定中心的 SMTP 組態（notify.smtp.*）寄送目前報表（§14.1 #9 週期郵寄的手動版）。
    /// 沿用事件通知的帳密與收件者；未設定或寄送失敗都只更新狀態列，不讓 UI 崩潰。
    /// </summary>
    private async void OnEmailClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = NotificationSettings.Load(new SettingsRepository(_store));
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

    private static string Csv(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

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