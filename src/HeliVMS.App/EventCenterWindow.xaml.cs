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

    /// <summary>RangeCombo 中「全部」的索引（與 <see cref="RangeWindow"/> 的判定共用）。</summary>
    private const int AllRangeIndex = 3;
    private int _page = 1;
    private long? _selectedId;

    /// <summary>本頁已選取的事件 Id（多選批次處置用）。重整後據此保留同頁選取。</summary>
    private readonly HashSet<long> _selectedIds = [];

    /// <summary>程式化設定選取時抑制 SelectionChanged 遞迴，避免兩清單互相同步時打轉。</summary>
    private bool _restoringSelection;
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
        new LicenseUiGate(store).Apply(
            MapLocateButton, LicenseFeatures.Gis, Localizer.T("EventCenter.MapLocateTip"));
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

        /// <summary>「詳情」欄顯示字串：把引擎寫入的機器可讀 detail 轉成在地化說明。
        /// <see cref="Detail"/> 本身（機器格式）不動，匯出與遮蔽仍靠它。</summary>
        public string DetailLabel => EventDetailText.Format(Localizer.Lang, Detail);

        public string DurationLabel { get; init; } = "";

        public bool Acknowledged { get; init; }

        public string Status { get; init; } = AlarmEventStatus.Pending;

        public string? AssignedTo { get; init; }

        public string? Note { get; init; }

        public string StatusLabel => LocalizedStatus(Status);

        /// <summary>事件類型的顯示名；未列的類型原樣顯示（上游新增類型不會炸掉視窗）。</summary>
        public string TypeLabel =>
            EventType switch
            {
                "motion" => Localizer.T("EventCenter.TypeMotion"),
                "ai_person" => Localizer.T("EventCenter.TypeAiPerson"),
                "ai_vehicle" => Localizer.T("EventCenter.TypeAiVehicle"),
                "offline" => Localizer.T("EventCenter.TypeOffline"),
                "tamper" => Localizer.T("EventCenter.TypeTamper"),
                "io_input" => Localizer.T("EventCenter.TypeIoInput"),
                "line_cross" => Localizer.T("EventCenter.TypeLineCross"),
                "intrusion" => Localizer.T("EventCenter.TypeIntrusion"),
                "license_limit" => Localizer.T("EventCenter.TypeLicenseLimit"),
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

    /// <summary>依現況語言套用標題／過濾列／清單欄位／處置列／分頁／快照區文字（M57）。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("EventCenter.Title");
        ChannelLabel.Text = Localizer.T("EventCenter.Channel");
        RangeLabel.Text = Localizer.T("EventCenter.Range");
        TypeLabel.Text = Localizer.T("EventCenter.Type");
        KeywordLabel.Text = Localizer.T("EventCenter.Keyword");
        ApplyQueryButton.Content = Localizer.T("EventCenter.Apply");
        ApplyQueryButton.ToolTip = Localizer.T("EventCenter.ApplyTip");
        RefreshButton.Content = Localizer.T("EventCenter.Refresh");
        RefreshButton.ToolTip = Localizer.T("EventCenter.RefreshTip");
        ExportCsvButton.Content = Localizer.T("EventCenter.ExportCsv");
        ExportCsvButton.ToolTip = Localizer.T("EventCenter.ExportCsvTip");
        PendingOnlyCheck.Content = Localizer.T("EventCenter.PendingOnly");
        PendingOnlyCheck.ToolTip = Localizer.T("EventCenter.PendingOnlyTip");

        // 檢視切換鈕顯示的是「切過去以後」的樣式，與目前樣式相反。
        ViewToggleButton.Content = Localizer.T(
            EventList.Visibility == Visibility.Visible ? "EventCenter.ViewCards" : "EventCenter.ViewList");
        ViewToggleButton.ToolTip = Localizer.T("EventCenter.ViewToggleTip");

        EventListHeadingText.Text = Localizer.T("EventCenter.ListHeading");
        DispositionLabel.Text = Localizer.T("EventCenter.Disposition");
        AssignLabelText.Text = Localizer.T("EventCenter.AssignLabel");
        NoteLabelText.Text = Localizer.T("EventCenter.NoteLabel");
        ApplyDispositionButton.Content = Localizer.T("EventCenter.ApplyDisposition");
        ApplyDispositionButton.ToolTip = Localizer.T("EventCenter.ApplyDispositionTip");
        TrailText.Text = Localizer.T("EventCenter.TrailEmpty");
        SnapshotHeadingText.Text = Localizer.T("EventCenter.SnapshotHeading");
        SnapshotHint.Text = Localizer.T("EventCenter.SnapshotPick");
        SnapshotRedactButton.Content = Localizer.T("EventCenter.SnapshotRedact");
        SnapshotRedactButton.ToolTip = Localizer.T("EventCenter.SnapshotRedactTip");

        AckButton.Content = Localizer.T("EventCenter.Ack");
        AckButton.ToolTip = Localizer.T("EventCenter.AckTip");
        UnackButton.Content = Localizer.T("EventCenter.Unack");
        UnackButton.ToolTip = Localizer.T("EventCenter.UnackTip");
        UpdateSelectAllButton();
        PlaybackButton.Content = Localizer.T("EventCenter.Playback");
        PlaybackButton.ToolTip = Localizer.T("EventCenter.PlaybackTip");
        MapLocateButton.Content = Localizer.T("EventCenter.MapLocate");
        MapLocateButton.ToolTip = Localizer.T("EventCenter.MapLocateTip");
        PrevPageButton.Content = Localizer.T("EventCenter.PrevPage");
        PrevPageButton.ToolTip = Localizer.T("EventCenter.PrevPageTip");
        NextPageButton.Content = Localizer.T("EventCenter.NextPage");
        NextPageButton.ToolTip = Localizer.T("EventCenter.NextPageTip");

        var ranges = new[] { "EventCenter.Today", "EventCenter.Hours24", "EventCenter.Days7", "EventCenter.All" };
        for (var i = 0; i < ranges.Length && i < RangeCombo.Items.Count; i++)
        {
            if (RangeCombo.Items[i] is System.Windows.Controls.ComboBoxItem item)
            {
                item.Content = Localizer.T(ranges[i]);
            }
        }

        // 處置選單以 Tag（pending/acknowledged/...）作為資料，顯示文字可獨立翻譯。
        var dispositions = new[]
        {
            "EventCenter.DispPending", "EventCenter.DispAcknowledged",
            "EventCenter.DispActioned", "EventCenter.DispFalseAlarm",
        };
        for (var i = 0; i < dispositions.Length && i < DispositionCombo.Items.Count; i++)
        {
            if (DispositionCombo.Items[i] is ComboBoxItem item)
            {
                item.Content = Localizer.T(dispositions[i]);
            }
        }

        if (EventList.View is GridView grid && grid.Columns.Count >= 7)
        {
            grid.Columns[0].Header = Localizer.T("EventCenter.ColTime");
            grid.Columns[1].Header = Localizer.T("EventCenter.ColChannel");
            grid.Columns[2].Header = Localizer.T("EventCenter.ColType");
            grid.Columns[3].Header = Localizer.T("EventCenter.ColDuration");
            grid.Columns[4].Header = Localizer.T("EventCenter.ColDetail");
            grid.Columns[5].Header = Localizer.T("EventCenter.ColStatus");
            grid.Columns[6].Header = Localizer.T("EventCenter.ColAssigned");
        }
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
            ViewToggleButton.Content = Localizer.T("EventCenter.ViewList");
        }
        else
        {
            EventList.Visibility = Visibility.Visible;
            CardList.Visibility = Visibility.Collapsed;
            ViewToggleButton.Content = Localizer.T("EventCenter.ViewCards");
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
            CountText.Text = Localizer.T("EventCenter.ExportAdminOnly");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Localizer.T("EventCenter.ExportDialogTitle"),
            Filter = Localizer.T("EventCenter.ExportDialogFilter"),
            FileName = $"events-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var q = BuildQuery();
        var list = await Task.Run(() => _events.ListByQuery(q));
        await Task.Run(() => WriteCsv(dlg.FileName, list, _channels.List()));
        CountText.Text = string.Format(
            CultureInfo.InvariantCulture, Localizer.T("EventCenter.Exported"), list.Count, dlg.FileName);
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
    /// <remarks>
    /// 時間範圍必須放寬到「全部」。未確認徽章是<b>全庫</b>的未確認總數，但清單預設只查近 24 小時，
    /// 兩者不一致時舊的未確認事件不會出現在清單裡，使用者「全選確認」也清不掉——實際症狀就是
    /// 徽章永遠卡在同樣的筆數。分診入口一律讓「所見＝徽章所計」。
    /// </remarks>
    public void FocusUnacknowledged(long? channelId = null)
    {
        if (channelId is { } id)
        {
            _preselect = id;
        }

        RangeCombo.SelectedIndex = AllRangeIndex;
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
        sb.AppendLine(Localizer.T("EventCenter.CsvHeader"));
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
                Csv(LocalizedStatus(ev.Status)),
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
            // 「僅未確認」必須進查詢，否則清單照樣列出已確認事件，徽章指涉的未確認事件反而找不到、
            // 也因為分頁被推到後面而「全選」不到（實際症狀：徽章卡住、清不掉）。與 BuildQuery 一致。
            Status = PendingOnlyCheck.IsChecked == true ? AlarmEventStatus.Pending : null,
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

        // 保留同頁已選取的列：以 Id 集合保存，重整（含推播／輪詢）後重新勾回，操作不被打斷。
        // 必須在換 ItemsSource 前就舉起抑制旗標，否則換源觸發的 SelectionChanged 會把集合清空。
        _restoringSelection = true;
        EventList.ItemsSource = eventRows;
        CardList.ItemsSource = eventRows;

        var pageIds = eventRows.Select(r => r.Id).ToHashSet();
        _selectedIds.RemoveWhere(id => !pageIds.Contains(id));
        foreach (var r in eventRows.Where(r => _selectedIds.Contains(r.Id)))
        {
            EventList.SelectedItems.Add(r);
            CardList.SelectedItems.Add(r);
        }

        _restoringSelection = false;

        var pages = Math.Max(1, (total + PageSize - 1) / PageSize);
        CountText.Text = string.Format(
            CultureInfo.InvariantCulture, Localizer.T("EventCenter.CountSummary"), total, _page, pages);
        PageText.Text = string.Format(
            CultureInfo.InvariantCulture, Localizer.T("EventCenter.PageOf"), _page, pages);
        PrevPageButton.IsEnabled = _page > 1;
        NextPageButton.IsEnabled = _page * PageSize < total;

        // 主要列（快照／軌跡）若已不在選取集合（例如被批次確認後從「僅未確認」視圖消失）就改指第一列。
        if (_selectedId is long keep && !_selectedIds.Contains(keep))
        {
            _selectedId = eventRows.FirstOrDefault(r => _selectedIds.Contains(r.Id))?.Id;
        }

        ApplySelection();
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
                AllRangeIndex => (DateTime.MinValue, now),
                _ => (now.AddHours(-24), now),
            };
    }

    private void OnEventSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_restoringSelection)
        {
            return;
        }

        MirrorSelection(EventList, CardList, e);
    }

    private void OnCardSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection)
        {
            return;
        }

        MirrorSelection(CardList, EventList, e);
    }

    /// <summary>把來源清單的選取鏡射到另一份檢視，並更新選取集合與主要列。
    /// 兩個檢視共用同一批 <see cref="EventRow"/> 物件，鏡射期間抑制對方的事件避免互相觸發。</summary>
    private void MirrorSelection(
        ListBox source,
        ListBox other,
        SelectionChangedEventArgs e)
    {
        _selectedIds.Clear();
        foreach (var r in source.SelectedItems.OfType<EventRow>())
        {
            _selectedIds.Add(r.Id);
        }

        // 主要列＝最後加入的列（快照／軌跡跟著最後點的那筆）；被移除時退回第一列。
        if (e.AddedItems.Count > 0 && e.AddedItems[e.AddedItems.Count - 1] is EventRow added)
        {
            _selectedId = added.Id;
        }
        else if (_selectedId is long id && !_selectedIds.Contains(id))
        {
            _selectedId = null;
        }

        _restoringSelection = true;
        other.SelectedItems.Clear();
        foreach (var r in source.SelectedItems.OfType<EventRow>())
        {
            other.SelectedItems.Add(r);
        }

        _restoringSelection = false;

        ApplySelection();
    }

    /// <summary>套用選取：按鈕狀態、快照、處置控制項與軌跡。多選時回放／地圖／遮蔽僅限單列，
    /// 確認與處置則一次套用到所有選取列。</summary>
    private void ApplySelection()
    {
        var rows = SelectedRows;
        var count = rows.Count;
        var row = SelectedRow;
        _selectedId = row?.Id;

        AckButton.IsEnabled = count > 0;
        UnackButton.IsEnabled = count > 0;
        PlaybackButton.IsEnabled = count == 1;
        MapLocateButton.IsEnabled = count == 1;
        SnapshotRedactButton.IsEnabled = count == 1;
        ApplyDispositionButton.IsEnabled = count > 0;

        if (count == 0)
        {
            DisposeDispositionEditor();
            TrailText.Text = Localizer.T("EventCenter.TrailEmpty");
            ShowSnapshot(null, null);
            UpdateSelectAllButton();
            return;
        }

        if (count == 1 && row is not null)
        {
            SelectDisposition(row.Status);
            AssignBox.Text = row.AssignedTo ?? "";
            NoteBox.Text = row.Note ?? "";
            ShowTrail(row.Id);
            ShowSnapshot(row.SnapshotPath, row.Detail);
        }
        else
        {
            // 多選：處置欄位保持可用（一次套用到全部），但不預填單列值，避免誤以為只改一列。
            DisposeDispositionEditor();
            ShowSnapshot(null, null);
            TrailText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("EventCenter.SelectedCount"), count);
        }

        UpdateSelectAllButton();
    }

    /// <summary>「全選／取消全選」在兩份檢視間切換：頁面全部已選時清空，否則選取整頁。</summary>
    private void OnSelectAllClicked(object sender, RoutedEventArgs e)
    {
        var rows = (EventList.ItemsSource as IEnumerable<EventRow>)?.ToList() ?? [];
        if (rows.Count == 0)
        {
            return;
        }

        var allSelected = _selectedIds.Count >= rows.Count;
        _restoringSelection = true;
        EventList.SelectedItems.Clear();
        CardList.SelectedItems.Clear();
        _selectedIds.Clear();
        if (!allSelected)
        {
            foreach (var r in rows)
            {
                EventList.SelectedItems.Add(r);
                CardList.SelectedItems.Add(r);
                _selectedIds.Add(r.Id);
            }
        }

        _restoringSelection = false;

        _selectedId = allSelected ? null : rows[0].Id;
        ApplySelection();
    }

    /// <summary>更新「全選／取消全選」按鈕的文字、提示與可用狀態（頁面無列即停用）。</summary>
    private void UpdateSelectAllButton()
    {
        var pageCount = (EventList.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
        var allSelected = pageCount > 0 && _selectedIds.Count >= pageCount;
        SelectAllButton.Content = Localizer.T(
            allSelected ? "EventCenter.ClearSelection" : "EventCenter.SelectAll");
        SelectAllButton.ToolTip = Localizer.T(
            allSelected ? "EventCenter.ClearSelectionTip" : "EventCenter.SelectAllTip");
        SelectAllButton.IsEnabled = pageCount > 0;
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
            TrailText.Text = Localizer.T("EventCenter.TrailNone");
            return;
        }

        var last = trail[^1];
        var at = last.ChangedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var who = string.IsNullOrWhiteSpace(last.AssignedTo) ? "" : string.Format(
            CultureInfo.InvariantCulture, Localizer.T("EventCenter.TrailBy"), last.AssignedTo);
        var note = string.IsNullOrWhiteSpace(last.Note) ? "" : string.Format(
            CultureInfo.InvariantCulture, Localizer.T("EventCenter.TrailNote"), last.Note);
        TrailText.Text = string.Format(
            CultureInfo.InvariantCulture, Localizer.T("EventCenter.TrailSummary"),
            trail.Count, at, LocalizedStatus(last.Status)) + who + note;
    }

    /// <summary>狀態代碼轉現況語言標籤；未知代碼原樣顯示。
    /// 走 <c>AlarmStatus.&lt;status&gt;</c> 鍵而非 <see cref="AlarmEventStatus.Label"/>——
    /// 後者固定中文，切換語系時事件中心會殘留中文（M293 收尾）。</summary>
    private static string LocalizedStatus(string status)
        => AlarmEventStatus.IsValid(status) ? Localizer.T("AlarmStatus." + status) : status;

    private void OnApplyDispositionClicked(object sender, RoutedEventArgs e)
    {
        var ids = SelectedIdsForBatch();
        if (ids.Count == 0)
        {
            return;
        }

        var status = (DispositionCombo.SelectedItem as ComboBoxItem)?.Tag as string
            ?? AlarmEventStatus.Pending;
        var assign = string.IsNullOrWhiteSpace(AssignBox.Text) ? null : AssignBox.Text.Trim();
        var note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();
        var at = DateTime.UtcNow;
        foreach (var id in ids)
        {
            _events.SetDisposition(id, status, assign, note, at);
        }

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

    /// <summary>目前檢視中被選取的列（多選時為全部選取列）。</summary>
    private IReadOnlyList<EventRow> SelectedRows =>
        (CardList.Visibility == Visibility.Visible ? (ListBox)CardList : EventList)
            .SelectedItems.OfType<EventRow>()
            .ToList();

    /// <summary>主要選取列：主要列 Id 命中時用它（快照／軌跡跟隨最後點選），否則取第一列。</summary>
    private EventRow? SelectedRow
    {
        get
        {
            var rows = SelectedRows;
            if (rows.Count == 0)
            {
                return null;
            }

            if (_selectedId is long id)
            {
                var match = rows.FirstOrDefault(r => r.Id == id);
                if (match is not null)
                {
                    return match;
                }
            }

            return rows[0];
        }
    }

    /// <summary>對選取事件的快照開啟遮蔽窗（M116，單例重用）。</summary>
    private void OnRedactSnapshotClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not EventRow row)
        {
            return;
        }

        if (string.IsNullOrEmpty(row.SnapshotPath) || !File.Exists(row.SnapshotPath))
        {
            SnapshotHint.Text = Localizer.T("EventCenter.SnapshotNoFile");
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
        var ids = SelectedIdsForBatch();
        if (ids.Count == 0)
        {
            return;
        }

        foreach (var id in ids)
        {
            _events.Acknowledge(id, acknowledged);
        }

        DoRefresh();
    }

    /// <summary>批次操作要處理的事件 Id：優先取多選集合，退回單一主要列。
    /// 順序依 Id 排序，確保處置軌跡與稽核的寫入次序可重現。</summary>
    private IReadOnlyList<long> SelectedIdsForBatch()
    {
        if (_selectedIds.Count > 0)
        {
            return _selectedIds.OrderBy(x => x).ToList();
        }

        return SelectedRow is EventRow row ? [row.Id] : [];
    }

    private void ShowSnapshot(string? path, string? detail)
    {
        ClearSnapshot();
        SnapshotDetailText.Text = string.IsNullOrEmpty(detail)
            ? Localizer.T("EventCenter.SnapshotNoDetail")
            : string.Format(CultureInfo.InvariantCulture, Localizer.T("EventCenter.SnapshotDetail"), EventDetailText.Format(Localizer.Lang, detail));

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            SnapshotHint.Text = Localizer.T(
                string.IsNullOrEmpty(path)
                    ? "EventCenter.SnapshotMissing"
                    : "EventCenter.SnapshotMissingCleaned");
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
            SnapshotHint.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("EventCenter.SnapshotOpenFailed"), ex.Message);
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
                Text = $"{EventDetailText.ClassLabel(Localizer.Lang, d.Class)} {d.Confidence:0.00}",
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

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
