using System.Globalization;
using System.Windows;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 法證跨來源檢索窗（M97，§14.7 #7）：把警報、門禁、POS、邊緣 AI 四個 FTS 索引
/// 併入同一次查詢。事件中心只查 alarm_events，這裡才是「一個關鍵字找遍全系統」。
/// </summary>
public partial class SearchWindow : Window
{
    private const int ResultLimit = 200;

    private readonly UnifiedEventSearch _search;

    public SearchWindow(SqliteStore store)
    {
        _search = new UnifiedEventSearch(store);
        InitializeComponent();
        Title = "HeliVMS 法證檢索";
    }

    private sealed record SearchRow(string Source, string Time, string Text, string Status);

    private void OnSearchClicked(object sender, RoutedEventArgs e)
    {
        var query = SearchQueryBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchStatusText.Text = "請輸入檢索關鍵字。";
            return;
        }

        var mask = BuildSourceMask();
        if (mask == ForensicSource.None)
        {
            SearchStatusText.Text = "請至少勾選一個來源。";
            return;
        }

        var (fromUtc, toUtc) = BuildRange();

        try
        {
            var hits = _search.Search(query, fromUtc, toUtc, mask, ResultLimit);
            SearchList.ItemsSource = hits.Select(h => new SearchRow(
                SourceLabel(h.Source),
                h.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                h.Text,
                h.Status ?? string.Empty)).ToList();

            SearchStatusText.Text = hits.Count == 0
                ? "沒有符合的事件。"
                : $"共 {hits.Count} 筆命中（最多顯示 {ResultLimit} 筆）。";
        }
        catch (Exception ex)
        {
            SearchList.ItemsSource = null;
            SearchStatusText.Text = $"檢索失敗：{ex.Message}";
        }
    }

    private void OnRebuildClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _search.RebuildAll();
            SearchStatusText.Text = "已重建全文檢索索引。";
        }
        catch (Exception ex)
        {
            SearchStatusText.Text = $"重建索引失敗：{ex.Message}";
        }
    }

    private ForensicSource BuildSourceMask()
    {
        var mask = ForensicSource.None;
        if (SearchAlarmCheck.IsChecked == true) mask |= ForensicSource.Alarm;
        if (SearchDoorCheck.IsChecked == true) mask |= ForensicSource.Door;
        if (SearchPosCheck.IsChecked == true) mask |= ForensicSource.Pos;
        if (SearchEdgeCheck.IsChecked == true) mask |= ForensicSource.EdgeSmart;
        return mask;
    }

    private (DateTime? FromUtc, DateTime? ToUtc) BuildRange()
    {
        DateTime? from = SearchFromDate.SelectedDate is { } f ? f.Date.ToUniversalTime() : null;
        DateTime? to = SearchToDate.SelectedDate is { } t ? t.Date.AddDays(1).ToUniversalTime() : null;
        return (from, to);
    }

    private static string SourceLabel(ForensicSource source) => source switch
    {
        ForensicSource.Alarm => "警報",
        ForensicSource.Door => "門禁",
        ForensicSource.Pos => "POS",
        ForensicSource.EdgeSmart => "邊緣 AI",
        _ => source.ToString(),
    };
}
