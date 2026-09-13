using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>事件中心（§8.5）：依頻道／時間範圍列出警報事件、確認狀態管理、快照檢視。</summary>
public partial class EventCenterWindow : Window
{
    private readonly SqliteStore _store;
    private readonly ChannelRepository _channels;
    private readonly AlarmEventRepository _events;

    public EventCenterWindow(SqliteStore store)
    {
        InitializeComponent();
        _store = store;
        _channels = new ChannelRepository(store);
        _events = new AlarmEventRepository(store);
    }

    private sealed class EventRow
    {
        public long Id { get; init; }

        public string StartLabel { get; init; } = "";

        public string ChannelName { get; init; } = "";

        public string EventType { get; init; } = "motion";

        public string? Detail { get; init; }

        public string DurationLabel { get; init; } = "";

        public bool Acknowledged { get; init; }

        public string AckLabel => Acknowledged ? "已確認" : "未確認";

        public string? SnapshotPath { get; init; }

        public Brush TypeBrush =>
            EventType switch
            {
                "offline" => Brushes.LightCoral,
                "line_cross" or "intrusion" => Brushes.Gold,
                _ => Brushes.LightSteelBlue,
            };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshChannels();
        DoRefresh();
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
    }

    private void RefreshChannels()
    {
        var current = ChannelCombo.SelectedItem is System.Windows.Controls.ComboBoxItem c && c.Tag is int cid
            ? cid
            : (int?)null;
        ChannelCombo.Items.Clear();
        var all = new System.Windows.Controls.ComboBoxItem { Content = "全部頻道", Tag = null };
        ChannelCombo.Items.Add(all);
        foreach (var ch in _channels.List())
        {
            ChannelCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = ch.Name, Tag = ch.Id });
        }

        if (current is int id)
        {
            foreach (var item in ChannelCombo.Items.OfType<System.Windows.Controls.ComboBoxItem>())
            {
                if (item.Tag is int tid && tid == id)
                {
                    ChannelCombo.SelectedItem = item;
                    break;
                }
            }
        }
        else
        {
            ChannelCombo.SelectedIndex = 0;
        }
    }

    private void OnChannelSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => DoRefresh();

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => DoRefresh();

    private void DoRefresh()
    {
        var (from, to) = RangeWindow();
        int? channelId = null;
        if (ChannelCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is int cid)
        {
            channelId = cid;
        }

        var list = _events.ListByRange(channelId, from, to);
        var byId = _channels.List().ToDictionary(x => x.Id);
        EventList.ItemsSource = list
            .Select(ev => new EventRow
            {
                Id = ev.Id,
                StartLabel = ev.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ChannelName = byId.TryGetValue(ev.ChannelId, out var c) ? c.Name : $"#{ev.ChannelId}",
                EventType = ev.EventType,
                Detail = ev.Detail,
                DurationLabel = ev.EndUtc is DateTime end
                    ? $"{(end - ev.StartUtc).TotalSeconds:0.#}s"
                    : "",
                Acknowledged = ev.Acknowledged,
                SnapshotPath = ev.SnapshotPath,
            })
            .ToList();

        CountText.Text = $"共 {list.Count} 筆事件";
        EventList.SelectedItem = null;
        ClearSnapshot();
    }

    private (DateTime From, DateTime To) RangeWindow()
    {
        var now = DateTime.UtcNow;
        var idx = RangeCombo.SelectedIndex;
        return idx switch
        {
            0 => (now.Date, now.Date.AddDays(1).AddTicks(-1)),
            2 => (now.AddDays(-7), now),
            3 => (DateTime.MinValue, now),
            _ => (now.AddHours(-24), now),
        };
    }

    private void OnEventSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var row = EventList.SelectedItem as EventRow;
        AckButton.IsEnabled = row != null && !row.Acknowledged;
        UnackButton.IsEnabled = row != null && row.Acknowledged;
        ShowSnapshot(row?.SnapshotPath);
    }

    private void OnEventDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = EventList.SelectedItem as EventRow;
        if (row is null)
        {
            return;
        }

        var date = row.StartLabel.Split(' ')[0];
        Clipboard.SetText(date);
        SnapshotHint.Text = $"已複製開始日期（本地）「{date}」－可切到回放視窗查詢。";
    }

    private void OnAckClicked(object sender, RoutedEventArgs e) => SetAck(true);

    private void OnUnackClicked(object sender, RoutedEventArgs e) => SetAck(false);

    private void SetAck(bool acknowledged)
    {
        if (EventList.SelectedItem is not EventRow row)
        {
            return;
        }

        _events.Acknowledge(row.Id, acknowledged);
        DoRefresh();
    }

    private void ShowSnapshot(string? path)
    {
        ClearSnapshot();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            SnapshotHint.Text = string.IsNullOrEmpty(path) ? "此事件無快照。" : "快照檔已不在（可能已清理）。";
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            SnapshotImage.Source = bmp;
            SnapshotPathText.Text = path;
            SnapshotImage.Stretch = Stretch.Uniform;
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            SnapshotHint.Text = "無法開啟快照：" + ex.Message;
        }
    }

    private void ClearSnapshot()
    {
        SnapshotImage.Source = null;
        SnapshotPathText.Text = "";
    }
}