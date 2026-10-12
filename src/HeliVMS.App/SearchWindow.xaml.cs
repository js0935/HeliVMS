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
        ApplyI18n();
    }

    /// <summary>
    /// 套用目前語系到檢索視窗的固定文字（M57 §14.7）。XAML 已無硬編中文，文字集中於此，
    /// 由 WpfI18nContractTests 的棘輪釘住三語齊備。
    /// </summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Search.Title");
        DescriptionText.Text = Localizer.T("Search.Description");
        KeywordLabelText.Text = Localizer.T("Search.Keyword");
        SearchQueryBox.ToolTip = Localizer.T("Search.QueryTip");
        FromLabelText.Text = Localizer.T("Search.From");
        ToLabelText.Text = Localizer.T("Search.To");
        SearchButton.Content = Localizer.T("Search.Search");
        SearchButton.ToolTip = Localizer.T("Search.SearchTip");
        SearchRebuildButton.Content = Localizer.T("Search.Rebuild");
        SearchRebuildButton.ToolTip = Localizer.T("Search.RebuildTip");
        SearchAlarmCheck.Content = Localizer.T("Search.Alarm");
        SearchDoorCheck.Content = Localizer.T("Search.Door");
        SearchPosCheck.Content = Localizer.T("Search.Pos");
        SearchEdgeCheck.Content = Localizer.T("Search.Edge");
        SearchColSource.Header = Localizer.T("Search.ColSource");
        SearchColTime.Header = Localizer.T("Search.ColTime");
        SearchColText.Header = Localizer.T("Search.ColText");
        SearchColStatus.Header = Localizer.T("Search.ColStatus");
    }

    private sealed record SearchRow(string Source, string Time, string Text, string Status);

    private void OnSearchClicked(object sender, RoutedEventArgs e)
    {
        var query = SearchQueryBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchStatusText.Text = Localizer.T("Search.NeedQuery");
            return;
        }

        var mask = BuildSourceMask();
        if (mask == ForensicSource.None)
        {
            SearchStatusText.Text = Localizer.T("Search.NeedSource");
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
                ? Localizer.T("Search.NoHits")
                : string.Format(Localizer.T("Search.Hits"), hits.Count, ResultLimit);
        }
        catch (Exception ex)
        {
            SearchList.ItemsSource = null;
            SearchStatusText.Text = string.Format(Localizer.T("Search.Failed"), ex.Message);
        }
    }

    private void OnRebuildClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _search.RebuildAll();
            SearchStatusText.Text = Localizer.T("Search.Rebuilt");
        }
        catch (Exception ex)
        {
            SearchStatusText.Text = string.Format(Localizer.T("Search.RebuildFailed"), ex.Message);
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
        ForensicSource.Alarm => Localizer.T("Search.Alarm"),
        ForensicSource.Door => Localizer.T("Search.Door"),
        ForensicSource.Pos => Localizer.T("Search.Pos"),
        ForensicSource.EdgeSmart => Localizer.T("Search.Edge"),
        _ => source.ToString(),
    };
}
