using System.IO;
using System.Text;
using System.Windows;
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

            ReportStatusText.Text = $"已匯出：{path}";
        }
        catch (Exception ex)
        {
            ReportStatusText.Text = $"匯出失敗：{ex.Message}";
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