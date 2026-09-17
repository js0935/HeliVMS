using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HeliVMS.Alarms;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>事件中心（§8.5）：依頻道／時間範圍列出警報事件、確認狀態管理、快照檢視。</summary>
public partial class EventCenterWindow : Window
{
    private readonly SqliteStore _store;
    private readonly ChannelRepository _channels;
    private readonly AlarmEventRepository _events;
    private Size _snapSource;
    private IReadOnlyList<Detection> _snapDetections = [];
    private const int PageSize = 50;
    private int _page = 1;
    private long? _selectedId;

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

        public long ChannelId { get; init; }

        public DateTime StartUtc { get; init; }

        public string StartLabel { get; init; } = "";

        public string ChannelName { get; init; } = "";

        public string EventType { get; init; } = "motion";

        public string? Detail { get; init; }

        public string DurationLabel { get; init; } = "";

        public bool Acknowledged { get; init; }

        public string Status { get; init; } = AlarmEventStatus.Pending;

        public string? AssignedTo { get; init; }

        public string? Note { get; init; }

        public string StatusLabel => AlarmEventStatus.Label(Status);

        public string AssignedLabel => string.IsNullOrWhiteSpace(AssignedTo) ? "" : AssignedTo!;

        public Brush StatusBrush => Status switch
        {
            AlarmEventStatus.Acknowledged => new SolidColorBrush(Color.FromRgb(0x2A, 0x5C, 0x8A)),
            AlarmEventStatus.Actioned => new SolidColorBrush(Color.FromRgb(0x2A, 0x7F, 0x6E)),
            AlarmEventStatus.FalseAlarm => new SolidColorBrush(Color.FromRgb(0x55, 0x5F, 0x6B)),
            _ => new SolidColorBrush(Color.FromRgb(0x8A, 0x6F, 0x3A)),
        };

        public string? SnapshotPath { get; init; }

        public Brush TypeBrush =>
            EventType switch
            {
                "ai_person" => Brushes.Tomato,
                "ai_vehicle" => new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6)),
                "offline" => Brushes.LightCoral,
                "tamper" => Brushes.MediumPurple,
                "io_input" => Brushes.Orange,
                "line_cross" or "intrusion" => Brushes.Gold,
                _ => Brushes.LightSteelBlue,
            };
        
        public BitmapImage? ThumbnailSource { get; set; }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshChannels();
        RefreshTypes();
        DoRefresh();
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
    }

    private void RefreshTypes()
    {
        TypeCombo.Items.Clear();
        TypeCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "全部類型", Tag = null });
        foreach (var t in _events.ListEventTypes())
        {
            TypeCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = t, Tag = t });
        }

        TypeCombo.SelectedIndex = 0;
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

    private void OnChannelSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_events is null) return;
        _page = 1;
        DoRefresh();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_events is null) return;
        DoRefresh();
    }

    private void OnViewToggleClicked(object sender, RoutedEventArgs e)
    {
        if (EventList.Visibility == Visibility.Visible)
        {
            EventList.Visibility = Visibility.Collapsed;
            CardList.Visibility = Visibility.Visible;
            ViewToggleButton.Content = "清單";
        }
        else
        {
            EventList.Visibility = Visibility.Visible;
            CardList.Visibility = Visibility.Collapsed;
            ViewToggleButton.Content = "卡片";
        }
    }

    private void DoRefresh()
    {
        var (from, to) = RangeWindow();
        int? channelId = null;
        if (ChannelCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is int cid)
        {
            channelId = cid;
        }

        string? eventType = null;
        if (TypeCombo.SelectedItem is System.Windows.Controls.ComboBoxItem ti && ti.Tag is string ts)
        {
            eventType = ts;
        }

        var q = new AlarmEventRepository.QueryArgs
        {
            ChannelId = channelId,
            EventType = eventType,
            FromUtc = from,
            ToUtc = to,
            Limit = PageSize,
            Offset = (_page - 1) * PageSize,
        };
        var list = _events.ListByQuery(q);
        var total = _events.CountByQuery(q);
        var byId = _channels.List().ToDictionary(x => x.Id);
        var eventRows = list
            .Select(ev => new EventRow
            {
                Id = ev.Id,
                ChannelId = ev.ChannelId,
                StartUtc = ev.StartUtc,
                StartLabel = ev.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ChannelName = byId.TryGetValue(ev.ChannelId, out var c) ? c.Name : $"#{ev.ChannelId}",
                EventType = ev.EventType,
                Detail = ev.Detail,
                DurationLabel = ev.EndUtc is DateTime end
                    ? $"{(end - ev.StartUtc).TotalSeconds:0.#}s"
                    : "",
                Acknowledged = ev.Acknowledged,
                Status = ev.Status,
                AssignedTo = ev.AssignedTo,
                Note = ev.Note,
                SnapshotPath = ev.SnapshotPath,
            })
            .ToList();

        // 載入縮圖（background, non-blocking）
        foreach (var row in eventRows)
        {
            if (!string.IsNullOrEmpty(row.SnapshotPath) && File.Exists(row.SnapshotPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.UriSource = new Uri(row.SnapshotPath, UriKind.Absolute);
                    bmp.DecodePixelWidth = 48;
                    bmp.EndInit();
                    bmp.Freeze();
                    row.ThumbnailSource = bmp;
                }
                catch
                {
                    // 縮圖載入失敗，保持 null
                }
            }
        }

        EventList.ItemsSource = eventRows;
        CardList.ItemsSource = eventRows;

        var pages = Math.Max(1, (total + PageSize - 1) / PageSize);
        CountText.Text = $"共 {total} 筆事件（第 {_page}/{pages} 頁）";
        PageText.Text = $"第 {_page} / {pages} 頁";
        PrevPageButton.IsEnabled = _page > 1;
        NextPageButton.IsEnabled = _page * PageSize < total;
        var keepId = _selectedId;
        EventList.SelectedItem = null;
        CardList.SelectedItem = null;
        _selectedId = keepId;
        ClearSnapshot();

        if (keepId is long keep)
        {
            var again = eventRows.FirstOrDefault(r => r.Id == keep);
            if (again is not null)
            {
                EventList.SelectedItem = again;
            }
            else
            {
                _selectedId = null;
                ApplySelection(null);
            }
        }
        else
        {
            ApplySelection(null);
        }
    }

    private void OnPrevPageClicked(object sender, RoutedEventArgs e)
    {
        if (_page <= 1)
        {
            return;
        }

        _page--;
        DoRefresh();
    }

    private void OnNextPageClicked(object sender, RoutedEventArgs e)
    {
        _page++;
        DoRefresh();
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
        ApplySelection(row);

        // 同步卡片檢視的選中狀態
        if (row != null && CardList.Visibility == Visibility.Visible)
        {
            CardList.SelectedItem = row;
        }
    }

    private void OnCardSelected(object sender, SelectionChangedEventArgs e)
    {
        var row = CardList.SelectedItem as EventRow;
        ApplySelection(row);

        // 同步網格檢視的選中狀態
        if (row != null && EventList.Visibility == Visibility.Visible)
        {
            EventList.SelectedItem = row;
        }
    }

    /// <summary>套用選取：按鈕狀態、快照、處置控制項與軌跡。</summary>
    private void ApplySelection(EventRow? row)
    {
        _selectedId = row?.Id;
        AckButton.IsEnabled = row != null && !row.Acknowledged;
        UnackButton.IsEnabled = row != null && row.Acknowledged;
        PlaybackButton.IsEnabled = row != null;
        MapLocateButton.IsEnabled = row != null;
        ApplyDispositionButton.IsEnabled = row != null;
        ShowSnapshot(row?.SnapshotPath, row?.Detail);

        if (row is null)
        {
            DisposeDispositionEditor();
            TrailText.Text = "軌跡：—";
            return;
        }

        SelectDisposition(row.Status);
        AssignBox.Text = row.AssignedTo ?? "";
        NoteBox.Text = row.Note ?? "";
        ShowTrail(row.Id);
    }

    private void DisposeDispositionEditor()
    {
        DispositionCombo.SelectedIndex = 0;
        AssignBox.Text = "";
        NoteBox.Text = "";
    }

    private void SelectDisposition(string status)
    {
        var idx = 0;
        for (var i = 0; i < DispositionCombo.Items.Count; i++)
        {
            if (DispositionCombo.Items[i] is ComboBoxItem item && (item.Tag as string) == status)
            {
                idx = i;
                break;
            }
        }

        DispositionCombo.SelectedIndex = idx;
    }

    private void ShowTrail(long eventId)
    {
        var trail = _events.ListDispositionTrail(eventId);
        if (trail.Count == 0)
        {
            TrailText.Text = "軌跡：尚無處置紀錄。";
            return;
        }

        var last = trail[^1];
        var at = last.ChangedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var who = string.IsNullOrWhiteSpace(last.AssignedTo) ? "" : $" by {last.AssignedTo}";
        var note = string.IsNullOrWhiteSpace(last.Note) ? "" : $" － {last.Note}";
        TrailText.Text = $"軌跡：共 {trail.Count} 筆｜最近 {at} {AlarmEventStatus.Label(last.Status)}{who}{note}";
    }

    private void OnApplyDispositionClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not EventRow row)
        {
            return;
        }

        var status = (DispositionCombo.SelectedItem as ComboBoxItem)?.Tag as string
            ?? AlarmEventStatus.Pending;
        var assign = string.IsNullOrWhiteSpace(AssignBox.Text) ? null : AssignBox.Text.Trim();
        var note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();
        _events.SetDisposition(row.Id, status, assign, note, DateTime.UtcNow);
        DoRefresh();
    }

    private void OnPlaybackClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not EventRow row)
        {
            return;
        }

        var playback = new PlaybackWindow(_store, row.ChannelId, row.StartUtc) { Owner = this };
        playback.Show();
    }

    private void OnMapLocateClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not EventRow row)
        {
            return;
        }

        var map = new MapWindow(_store, (int)row.ChannelId, "camera") { Owner = this };
        map.Show();
    }

    /// <summary>目前檢視中被選取的事件列。</summary>
    private EventRow? SelectedRow =>
        (CardList.Visibility == Visibility.Visible ? CardList.SelectedItem : EventList.SelectedItem) as EventRow;

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
        if (SelectedRow is not EventRow row)
        {
            return;
        }

        _events.Acknowledge(row.Id, acknowledged);
        DoRefresh();
    }

    private void ShowSnapshot(string? path, string? detail)
    {
        ClearSnapshot();
        SnapshotDetailText.Text = string.IsNullOrEmpty(detail)
            ? "此事件無明細。"
            : $"明細：{detail}";

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
            _snapSource = new Size(bmp.PixelWidth, bmp.PixelHeight);
            SnapshotPathText.Text = path;
            SnapshotImage.Stretch = Stretch.Uniform;
            SnapshotHint.Visibility = Visibility.Collapsed;

            if (DetectionDetail.TryParse(detail, out var det))
            {
                _snapDetections = [det];
            }
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            SnapshotHint.Text = "無法開啟快照：" + ex.Message;
            SnapshotHint.Visibility = Visibility.Visible;
        }

        LayoutOverlay();
    }

    private void OnSnapGridSizeChanged(object sender, SizeChangedEventArgs e) => LayoutOverlay();

    private void LayoutOverlay()
    {
        SnapOverlay.Children.Clear();
        if (_snapSource.Width <= 0 || _snapDetections.Count == 0)
        {
            return;
        }

        var availW = Math.Max(0, SnapGrid.ActualWidth - 12);
        var availH = Math.Max(0, SnapGrid.ActualHeight - 12);
        if (availW <= 0 || availH <= 0)
        {
            return;
        }

        var scale = Math.Min(availW / _snapSource.Width, availH / _snapSource.Height);
        if (scale <= 0)
        {
            return;
        }

        SnapOverlay.Width = _snapSource.Width * scale;
        SnapOverlay.Height = _snapSource.Height * scale;

        foreach (var d in _snapDetections)
        {
            var x = d.X * SnapOverlay.Width;
            var y = d.Y * SnapOverlay.Height;
            var w = d.W * SnapOverlay.Width;
            var h = d.H * SnapOverlay.Height;
            var brush = d.Class is "car" or "bus" or "truck" or "motorcycle" or "bicycle"
                ? Brushes.DeepSkyBlue
                : Brushes.Tomato;

            var rect = new Rectangle
            {
                Width = w,
                Height = h,
                Stroke = brush,
                StrokeThickness = Math.Max(1.5, 2.0 / scale),
                Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            SnapOverlay.Children.Add(rect);

            var label = new TextBlock
            {
                Text = $"{d.Class} {d.Confidence:0.00}",
                FontSize = 11,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(170, 8, 14, 22)),
                Padding = new Thickness(3, 0, 3, 0),
            };
            var ly = y - 16 >= 0 ? y - 16 : y;
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, ly);
            SnapOverlay.Children.Add(label);
        }
    }

    private void ClearSnapshot()
    {
        SnapshotImage.Source = null;
        SnapshotPathText.Text = "";
        SnapOverlay.Children.Clear();
        _snapSource = default;
        _snapDetections = [];
    }
}