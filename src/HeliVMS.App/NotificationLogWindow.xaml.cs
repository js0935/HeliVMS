using System.Windows;
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
        _store = store;
        _log = new NotificationLogRepository(store);
        _openEvents = openEvents;
        _channelNames = new ChannelRepository(store).List().ToDictionary(c => c.Id, c => c.Name);
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

        public string ResultLabel => Ok ? "成功" : "失敗";

        public string AttemptsLabel => $"{Attempts} 次";

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
        NotificationCountText.Text = $"共 {_log.Count()} 筆（顯示最近 {rows.Count} 筆）";
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