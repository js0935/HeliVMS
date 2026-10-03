using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>通知送達紀錄（M23，§16.3 notification_log 表；成功與最終失敗）。</summary>
public partial class NotificationLogWindow : Window
{
    private readonly SqliteStore _store;
    private readonly NotificationLogRepository _log;
    private readonly Dictionary<int, string> _channelNames;
    private readonly Action<long>? _openEvents;

    public NotificationLogWindow(SqliteStore store, Action<long>? openEvents = null)
    {
        InitializeComponent();
        ApplyI18n();
        _store = store;
        _log = new NotificationLogRepository(store);
        _openEvents = openEvents;
        _channelNames = new ChannelRepository(store).List().ToDictionary(c => c.Id, c => c.Name);
    }

    /// <summary>依現況語言套用標題、欄位與按鈕文字（M57）。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("NotifyLog.Title");
        NotificationRefreshButton.Content = Localizer.T("NotifyLog.Refresh");
        NotificationRefreshButton.ToolTip = Localizer.T("NotifyLog.RefreshTip");
        NotificationLogList.ToolTip = Localizer.T("NotifyLog.ListTip");
        NotificationLogFootnote.Text = Localizer.T("NotifyLog.Footnote");

        if (NotificationLogList.View is GridView grid && grid.Columns.Count >= 7)
        {
            grid.Columns[0].Header = Localizer.T("NotifyLog.ColTime");
            grid.Columns[1].Header = Localizer.T("NotifyLog.ColChannel");
            grid.Columns[2].Header = Localizer.T("NotifyLog.ColEvent");
            grid.Columns[3].Header = Localizer.T("NotifyLog.ColRoute");
            grid.Columns[4].Header = Localizer.T("NotifyLog.ColResult");
            grid.Columns[5].Header = Localizer.T("NotifyLog.ColAttempts");
            grid.Columns[6].Header = Localizer.T("NotifyLog.ColDetail");
        }
    }

    private sealed class LogRow
    {
        public long ChannelId { get; init; }

        public string StartLabel { get; init; } = "";

        public string ChannelName { get; init; } = "";

        public string EventType { get; init; } = "";

        public string Route { get; init; } = "";

        public bool Ok { get; init; }

        public int Attempts { get; init; }

        public string ResultLabel => Ok ? Localizer.T("NotifyLog.Ok") : Localizer.T("NotifyLog.Fail");

        public string AttemptsLabel => string.Format(CultureInfo.InvariantCulture, Localizer.T("NotifyLog.Attempts"), Attempts);

        public string? Detail { get; init; }

        public Brush ResultBrush => Ok
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x7C))
            : new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RefreshLog();

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => RefreshLog();

    private void RefreshLog()
    {
        var rows = _log.ListRecent(200)
            .Select(l => new LogRow
            {
                ChannelId = l.ChannelId,
                StartLabel = l.TsUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ChannelName = _channelNames.TryGetValue(l.ChannelId, out var n) ? n : $"#{l.ChannelId}",
                EventType = l.EventType,
                Route = l.Route,
                Ok = l.Ok,
                Attempts = l.Attempts,
                Detail = l.Detail,
            })
            .ToList();

        NotificationLogList.ItemsSource = rows;
        NotificationCountText.Text = string.Format(
            CultureInfo.InvariantCulture, Localizer.T("NotifyLog.Count"), _log.Count(), rows.Count);
    }

    /// <summary>雙擊＝開啟事件中心（該頻道、僅未確認）分診。</summary>
    private void OnLogDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (NotificationLogList.SelectedItem is LogRow { ChannelId: > 0 } row)
        {
            _openEvents?.Invoke(row.ChannelId);
        }
    }
}