using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HeliVMS.App.Services;
using HeliVMS.Alarms;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
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
    private long? _preselect;
    private readonly Dictionary<(string Kind, long Id), Window> _child = new();
    private const int PageSize = 50;
    private int _page = 1;
    private long? _selectedId;
    private readonly AlertFeed? _feed;

    public EventCenterWindow(SqliteStore store, long? preselectChannelId = null, AlertBroadcastHub? alerts = null)
    {
        InitializeComponent();
        _store = store;
        _channels = new ChannelRepository(store);
        _events = new AlarmEventRepository(store);
        _preselect = preselectChannelId;
        _feed = alerts is null ? null : new AlertFeed(alerts, Dispatcher, OnAlertsChanged);

        // gis 旗標限定（M209）：「在地圖定位」是地圖功能，事件中心本身不藏。
        new LicenseUiGate(store).Apply(MapLocateButton, LicenseFeatures.Gis, "在地圖上定位該事件的攝影機");
    }

    /// <summary>重定向本窗到指定頻道（供主視窗右鍵「此頻道」快速查詢重用同窗）。</summary>
    public void FocusChannel(long channelId)
    {
        _preselect = channelId;
        RefreshChannels();
        _page = 1;
        DoRefresh();
    }

    /// <summary>子視窗單例重用（依 eventId／channelId 分鍵）：同鍵已開時僅置前，
    /// 避免「回放此事件／地圖定位」連點堆疊多份視窗；關閉即釋放。</summary>
    private void OpenChild((string Kind, long Id) key, Window window)
    {
        if (_child.TryGetValue(key, out var existing) && existing is { IsVisible: true })
        {
            existing.Activate();
            return;
        }

        _child[key] = window;
        window.Owner = this;
        window.Closed += (_, _) => _child.Remove(key);
        window.Show();
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

        /// <summary>事件類型的中文顯示名；未列的類型原樣顯示（上游新增類型不會炸掉視窗）。</summary>
        public string TypeLabel =>
            EventType switch
            {
                "motion" => "動態",
                "ai_person" => "AI 人員",
                "ai_vehicle" => "AI 車輛",
                "offline" => "離線",
                "tamper" => "破壞",
                "io_input" => "IO 輸入",
                "line_cross" => "越線",
                "intrusion" => "入侵",
                "license_limit" => "授權上限",
                _ => EventType,
            };

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
                "license_limit" => Brushes.OrangeRed,
                _ => Brushes.LightSteelBlue,
            };
        
        public BitmapImage? ThumbnailSource { get; set; }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyI18n();
        RefreshChannels();
        RefreshTypes();
        DoRefresh();
    }

    /// <summary>推播進來的新警報立即重查，不必等 15 秒輪詢；保留目前頁碼以免操作中被跳頁。</summary>
    private void OnAlertsChanged()
    {
        if (IsLoaded)
        {
            DoRefresh();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _feed?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>依現況語言套用標題／過濾列文字（M57）。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("EventCenter.Title");
        ChannelLabel.Text = Localizer.T("EventCenter.Channel");
        RangeLabel.Text = Localizer.T("EventCenter.Range");
        TypeLabel.Text = Localizer.T("EventCenter.Type");
        KeywordLabel.Text = Localizer.T("EventCenter.Keyword");
        ApplyQueryButton.Content = Localizer.T("EventCenter.Apply");
        RefreshButton.Content = Localizer.T("EventCenter.Refresh");
        ExportCsvButton.Content = Localizer.T("EventCenter.ExportCsv");

        var ranges = new[] { "EventCenter.Today", "EventCenter.Hours24", "EventCenter.Days7", "EventCenter.All" };
        for (var i = 0; i < ranges.Length && i < RangeCombo.Items.Count; i++)
        {
            if (RangeCombo.Items[i] is System.Windows.Controls.ComboBoxItem item)
            {
                item.Content = Localizer.T(ranges[i]);
            }
        }
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
    }

    private void RefreshTypes()
    {
        TypeCombo.Items.Clear();
        TypeCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Localizer.T("EventCenter.AllTypes"), Tag = null });
        foreach (var t in _events.ListEventTypes())
        {
            TypeCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = t, Tag = t });
        }

        TypeCombo.SelectedIndex = 0;
    }

    private void RefreshChannels()
    {
        var preset = _preselect;
        _preselect = null;
        var current = preset ?? (ChannelCombo.SelectedItem is System.Windows.Controls.ComboBoxItem c && c.Tag is long cid
            ? cid
            : (long?)null);
        ChannelCombo.Items.Clear();
        var all = new System.Windows.Controls.ComboBoxItem { Content = Localizer.T("EventCenter.AllChannels"), Tag = null };
        ChannelCombo.Items.Add(all);
        foreach (var ch in _channels.List())
        {
            ChannelCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = ch.Name, Tag = ch.Id });
        }

        if (current is long id)
        {
            foreach (var item in ChannelCombo.Items.OfType<System.Windows.Controls.ComboBoxItem>())
            {
                if (item.Tag is long tid && tid == id)
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

    private void OnApplyQueryClicked(object sender, RoutedEventArgs e)
    {
        _page = 1;
        DoRefresh();
    }

    /// <summary>匯出目前查詢之全部事件為 CSV（UTF-8 BOM）。僅 admin（與匯出中心同權限，M42）。</summary>
    private async void OnExportCsvClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            CountText.Text = "匯出僅限管理員。";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "匯出事件中心 CSV",
            Filter = "CSV 檔 (*.csv)|*.csv",
            FileName = $"events-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var q = BuildQuery();
        var list = await Task.Run(() => _events.ListByQuery(q));
        await Task.Run(() => WriteCsv(dlg.FileName, list, _channels.List()));
        CountText.Text = $"已匯出 {list.Count} 筆至 {dlg.FileName}";
    }

    /// <summary>目前過濾列的查詢條件（不限頁數）。</summary>
    private AlarmEventRepository.QueryArgs BuildQuery()
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

        return new AlarmEventRepository.QueryArgs
        {
            ChannelId = channelId,
            EventType = eventType,
            Status = PendingOnlyCheck.IsChecked == true ? AlarmEventStatus.Pending : null,
            Keyword = KeywordBox.Text.Trim(),
            FromUtc = from,
            ToUtc = to,
            Limit = 10000,
        };
    }

    private void OnPendingOnlyChanged(object sender, RoutedEventArgs e)
    {
        _page = 1;
        DoRefresh();
    }

    /// <summary>切到「僅未確認」視圖（供主視窗未確認徽章點擊重用同窗）；
    /// 傳入 channelId 時一併聚焦該頻道。</summary>
    public void FocusUnacknowledged(long? channelId = null)
    {
        if (channelId is { } id)
        {
            _preselect = id;
        }

        PendingOnlyCheck.IsChecked = true;
        RefreshChannels();
        _page = 1;
        DoRefresh();
    }

    /// <summary>將事件寫入 CSV（UTF-8 BOM，供 UI 匯出與 --events-export 共用）。</summary>
    public static void WriteCsv(
        string path,
        IReadOnlyList<AlarmEventRecord> list,
        IReadOnlyList<ChannelInfo> channels)
    {
        var byId = channels.ToDictionary(x => x.Id);
        static string Csv(string? value)
        {
            var v = value ?? string.Empty;
            return v.IndexOfAny([',', '"', '\n', '\r']) >= 0
                ? $"\"{v.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
                : v;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("時間(本地),頻道,類型,持續(秒),詳情,快照,狀態,指派,備註");
        foreach (var ev in list)
        {
            var name = byId.TryGetValue(ev.ChannelId, out var c) ? c.Name : $"#{ev.ChannelId}";
            var duration = ev.EndUtc is DateTime end ? (end - ev.StartUtc).TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) : "";
            sb.AppendLine(string.Join(",",
                Csv(ev.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Csv(name),
                Csv(ev.EventType),
                Csv(duration),
                Csv(ev.Detail),
                Csv(ev.SnapshotPath),
                Csv(AlarmEventStatus.Label(ev.Status)),
                Csv(ev.AssignedTo),
                Csv(ev.Note)));
        }

        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        File.WriteAllBytes(path, bytes);
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
            Keyword = KeywordBox.Text.Trim(),
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

        OpenChild(("playback", row.Id), new PlaybackWindow(_store, row.ChannelId, row.StartUtc));
    }

    private void OnMapLocateClicked(object sender, RoutedEventArgs e)
    {
        var gate = new LicenseUiGate(_store);
        if (!gate.Allows(LicenseFeatures.Gis))
        {
            return;
        }

        if (SelectedRow is not EventRow row)
        {
            return;
        }

        OpenChild(("map", row.ChannelId), new MapWindow(_store, (int)row.ChannelId, "camera"));
    }

    /// <summary>目前檢視中被選取的事件列。</summary>
    private EventRow? SelectedRow =>
        (CardList.Visibility == Visibility.Visible ? CardList.SelectedItem : EventList.SelectedItem) as EventRow;

    /// <summary>對選取事件的快照開啟遮蔽窗（M116，單例重用）。</summary>
    private void OnRedactSnapshotClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not EventRow row)
        {
            return;
        }

        if (string.IsNullOrEmpty(row.SnapshotPath) || !File.Exists(row.SnapshotPath))
        {
            SnapshotHint.Text = "此事件無快照或快照檔已不在。";
            SnapshotHint.Visibility = Visibility.Visible;
            return;
        }

        OpenChild(
            ("snapredact", row.Id),
            new SnapshotRedactWindow(_store, row.SnapshotPath, row.Id, (int)row.ChannelId, row.StartUtc));
    }

    /// <summary>雙擊（清單／卡片）＝直接回放該事件（單例重用，不堆疊視窗）。</summary>
    private void OnEventDoubleClick(object sender, MouseButtonEventArgs e) => OnPlaybackClicked(this, new RoutedEventArgs());

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
