using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HeliVMS.Alarms;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 智慧牆視窗（M105/M106，§14.7 #14）：版面網格編輯（<see cref="SmartwallLayoutRepository"/>）
/// ＋警報牆快照（<see cref="SmartwallAlertBoard"/>）。Web 有 /api/smartwall/board，桌面先前無入口。
/// </summary>
public partial class SmartwallWindow : Window
{
    private readonly SmartwallLayoutRepository _layouts;
    private readonly AlarmEventRepository _events;
    private readonly AlarmTriageRepository _triage;
    private readonly ChannelRepository _channels;
    private readonly SettingsRepository _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<(long? ChannelId, Border Border)> _tileBorders = [];

    // 看板告警 MQTT 派送（§14.7 #14）：沿用既有 notify.mqtt.* 主機／帳密，主題前綴固定。
    private readonly MqttClient _mqtt = new();
    private readonly SemaphoreSlim _mqttGate = new(1, 1);
    private HashSet<long> _highlighted = [];

    private IReadOnlyList<BoardCell> _board = [];
    private long? _selectedTileId;

    public SmartwallWindow(SqliteStore store)
    {
        _layouts = new SmartwallLayoutRepository(store);
        _events = new AlarmEventRepository(store);
        _triage = new AlarmTriageRepository(store);
        _channels = new ChannelRepository(store);
        _settings = new SettingsRepository(store);
        InitializeComponent();
        ApplyI18n();

        ChannelCombo.DisplayMemberPath = nameof(ChannelInfo.Name);
        ChannelCombo.ItemsSource = _channels.List();
        RefreshLayouts();
        RefreshBoard();
        _timer.Tick += (_, _) => RefreshBoard();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_mqttGate.Wait(TimeSpan.FromSeconds(2)))
            {
                try { _mqtt.Dispose(); }
                finally { _mqttGate.Release(); }
            }
        };
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("Smartwall.Title");
        SmartwallDescText.Text = Localizer.T("Smartwall.Desc");
        SmartwallLayoutsLabel.Text = Localizer.T("Smartwall.Layouts");
        SmartwallNameLabel.Text = Localizer.T("Smartwall.Name");
        SmartwallRowsLabel.Text = Localizer.T("Smartwall.Rows");
        SmartwallColsLabel.Text = Localizer.T("Smartwall.Cols");
        CreateLayoutButton.Content = Localizer.T("Smartwall.CreateLayout");
        RenameLayoutButton.Content = Localizer.T("Smartwall.Rename");
        SmartwallTileRowLabel.Text = Localizer.T("Smartwall.Rows");
        SmartwallTileColLabel.Text = Localizer.T("Smartwall.Cols");
        SmartwallRowSpanLabel.Text = Localizer.T("Smartwall.RowSpan");
        SmartwallColSpanLabel.Text = Localizer.T("Smartwall.ColSpan");
        TileViewSingleItem.Content = Localizer.T("Smartwall.ViewSingle");
        TileViewMosaicItem.Content = Localizer.T("Smartwall.ViewMosaic");
        AddTileButton.Content = Localizer.T("Smartwall.AddTile");
        RemoveTileButton.Content = Localizer.T("Smartwall.RemoveTile");
        SmartwallBoardHeaderText.Text = Localizer.T("Smartwall.BoardHeader");
        SmartwallColRank.Header = Localizer.T("Smartwall.ColRank");
        SmartwallColChannel.Header = Localizer.T("Smartwall.ColChannel");
        SmartwallColEventType.Header = Localizer.T("Smartwall.ColEventType");
        SmartwallColPriority.Header = Localizer.T("Smartwall.ColPriority");
        SmartwallColAge.Header = Localizer.T("Smartwall.ColAge");
    }

    private void RefreshLayouts()
    {
        var selectedId = (LayoutList.SelectedItem as SmartwallLayout)?.Id;
        var layouts = _layouts.ListLayouts();
        LayoutList.ItemsSource = layouts;

        if (selectedId is { } id && layouts.FirstOrDefault(l => l.Id == id) is { } match)
        {
            LayoutList.SelectedItem = match;
        }
        else if (layouts.Count > 0)
        {
            LayoutList.SelectedItem = layouts[0];
        }
        else
        {
            RenderLayout();
        }
    }

    private void OnLayoutSelected(object sender, SelectionChangedEventArgs e)
    {
        _selectedTileId = null;
        RenderLayout();
    }

    private void OnCreateLayoutClicked(object sender, RoutedEventArgs e)
    {
        if (!TryReadNewLayout(out var name, out var rows, out var cols))
        {
            return;
        }

        try
        {
            _layouts.CreateLayout(name, rows, cols);
            RefreshLayouts();
            TileStatusText.Text = string.Format(Localizer.T("Smartwall.LayoutCreated"), name, rows, cols);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            TileStatusText.Text = string.Format(Localizer.T("Smartwall.CreateFailed"), ex.Message);
        }
    }

    private void OnRenameLayoutClicked(object sender, RoutedEventArgs e)
    {
        if (LayoutList.SelectedItem is not SmartwallLayout layout)
        {
            TileStatusText.Text = Localizer.T("Smartwall.NeedLayoutToRename");
            return;
        }

        var name = LayoutNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TileStatusText.Text = Localizer.T("Smartwall.NeedName");
            return;
        }

        try
        {
            _layouts.RenameLayout(layout.Id, name);
            RefreshLayouts();
            TileStatusText.Text = string.Format(Localizer.T("Smartwall.Renamed"), name);
        }
        catch (ArgumentException ex)
        {
            TileStatusText.Text = string.Format(Localizer.T("Smartwall.RenameFailed"), ex.Message);
        }
    }

    private bool TryReadNewLayout(out string name, out int rows, out int cols)
    {
        name = LayoutNameBox.Text.Trim();
        rows = 0;
        cols = 0;
        if (string.IsNullOrWhiteSpace(name))
        {
            TileStatusText.Text = Localizer.T("Smartwall.NeedName");
            return false;
        }

        if (!int.TryParse(LayoutRowsBox.Text, out rows) || !int.TryParse(LayoutColsBox.Text, out cols))
        {
            TileStatusText.Text = Localizer.T("Smartwall.BadRowsCols");
            return false;
        }

        return true;
    }

    private void OnAddTileClicked(object sender, RoutedEventArgs e)
    {
        if (LayoutList.SelectedItem is not SmartwallLayout layout)
        {
            TileStatusText.Text = Localizer.T("Smartwall.NeedLayout");
            return;
        }

        if (!int.TryParse(TileRowBox.Text, out var row) || !int.TryParse(TileColBox.Text, out var col) ||
            !int.TryParse(TileRowSpanBox.Text, out var rowSpan) || !int.TryParse(TileColSpanBox.Text, out var colSpan))
        {
            TileStatusText.Text = Localizer.T("Smartwall.BadTileNumbers");
            return;
        }

        var channelId = (ChannelCombo.SelectedItem as ChannelInfo)?.Id;
        if (channelId is null && TileViewCombo.SelectedIndex != 1)
        {
            TileStatusText.Text = Localizer.T("Smartwall.NeedChannelForSingle");
            return;
        }

        var view = TileViewCombo.SelectedIndex == 1 ? 1 : 0;
        var position = _layouts.GetTiles(layout.Id).Count;
        try
        {
            var id = _layouts.AddTile(layout.Id, row, col, rowSpan, colSpan, channelId, view, position);
            RenderLayout();
            TileStatusText.Text = string.Format(Localizer.T("Smartwall.TileAdded"), id, row, col, rowSpan, colSpan);
        }
        catch (ArgumentException ex)
        {
            TileStatusText.Text = string.Format(Localizer.T("Smartwall.AddTileFailed"), ex.Message);
        }
    }

    private void OnRemoveTileClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTileId is not { } id)
        {
            TileStatusText.Text = Localizer.T("Smartwall.NeedTile");
            return;
        }

        _layouts.RemoveTile(id);
        _selectedTileId = null;
        RenderLayout();
        TileStatusText.Text = string.Format(Localizer.T("Smartwall.TileRemoved"), id);
    }

    private void SelectTile(long id)
    {
        _selectedTileId = id;
        RenderLayout();
        TileStatusText.Text = string.Format(Localizer.T("Smartwall.TileSelected"), id);
    }

    /// <summary>依目前版面重建網格：每個方塊一個可點選 Border，點選後可由「移除」刪除。</summary>
    private void RenderLayout()
    {
        SmartwallGrid.Children.Clear();
        SmartwallGrid.RowDefinitions.Clear();
        SmartwallGrid.ColumnDefinitions.Clear();
        _tileBorders.Clear();

        if (LayoutList.SelectedItem is not SmartwallLayout layout)
        {
            return;
        }

        for (var r = 0; r < layout.Rows; r++)
        {
            SmartwallGrid.RowDefinitions.Add(new RowDefinition());
        }

        for (var c = 0; c < layout.Cols; c++)
        {
            SmartwallGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        var channels = _channels.List().ToDictionary(ch => (long)ch.Id, ch => ch.Name);
        foreach (var tile in _layouts.GetTiles(layout.Id))
        {
            var label = tile.View == 1
                ? Localizer.T("Smartwall.ViewMosaic")
                : tile.ChannelId is { } cid && channels.TryGetValue(cid, out var name) ? name : Localizer.T("Smartwall.Unassigned");
            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x73)),
                BorderThickness = new Thickness(tile.Id == _selectedTileId ? 2 : 1),
                Margin = new Thickness(2),
                Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x1D, 0x33)),
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                },
            };
            border.MouseLeftButtonDown += (_, _) => SelectTile(tile.Id);
            Grid.SetRow(border, tile.Row);
            Grid.SetColumn(border, tile.Col);
            Grid.SetRowSpan(border, tile.RowSpan);
            Grid.SetColumnSpan(border, tile.ColSpan);
            SmartwallGrid.Children.Add(border);
            _tileBorders.Add((tile.ChannelId, border));
        }

        ApplyBoardHighlight();
    }

    private void RefreshBoard()
    {
        var now = DateTime.UtcNow;
        var events = new List<SmartwallBoardEvent>();
        foreach (var e in _events.ListByRange(null, now - SmartwallTimings.MosaicKeepLast, now))
        {
            events.Add(new SmartwallBoardEvent(
                e.ChannelId, e.EventType, _triage.Get(e.Id)?.Priority ?? "normal", e.StartUtc, 0));
        }

        _board = SmartwallAlertBoard.Snapshot(events, now, 64);
        var channels = _channels.List().ToDictionary(ch => (long)ch.Id, ch => ch.Name);
        BoardList.ItemsSource = _board
            .Select(c => new
            {
                c.Rank,
                Channel = channels.TryGetValue(c.ChannelId, out var n) ? n : $"ch {c.ChannelId}",
                c.EventType,
                c.Priority,
                Age = $"{c.Age.TotalSeconds:0}",
            })
            .ToList();
        BoardStatusText.Text = _board.Count == 0
            ? Localizer.T("Smartwall.BoardEmpty")
            : string.Format(Localizer.T("Smartwall.BoardActive"), _board.Count);
        ApplyBoardHighlight();
        DispatchBoardAlerts(events, now);
    }

    /// <summary>
    /// 對「相對前次快照新進入高亮」的頻道做 MQTT 派送（§14.7 #14）。未設定 MQTT 時仍更新
    /// 高亮集合（僅不送出），避免設定後把先前已高亮的頻道誤判為新事件。
    /// </summary>
    private void DispatchBoardAlerts(IReadOnlyList<SmartwallBoardEvent> events, DateTime nowUtc)
    {
        var entering = SmartwallBoardDispatcher.EnteringHighlight(_highlighted, _board, events, nowUtc);
        _highlighted = SmartwallBoardDispatcher.HighlightedChannels(_board);

        if (entering.Count == 0)
        {
            return;
        }

        var notify = NotificationSettings.Load(_settings);
        if (!notify.MqttEnabled || string.IsNullOrWhiteSpace(notify.MqttHost))
        {
            return;
        }

        DispatchBatch(entering, notify.MqttHost!, notify.MqttPort, notify.MqttUser, notify.MqttPassword);
    }

    /// <summary>背景序號化派送（單一連線循序送，避免並行共用 <see cref="MqttClient"/>）；盡力而為不拋例外。</summary>
    private void DispatchBatch(
        IReadOnlyList<SmartwallBoardEvent> events, string host, int port, string? user, string? password)
    {
        _ = Task.Run(async () =>
        {
            await _mqttGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var e in events)
                {
                    SmartwallBoardDispatcher.Dispatch(
                        _mqtt, e, host, port, SmartwallBoardDispatcher.DefaultTopicPrefix, user, password, e.OccurredAtUtc);
                }
            }
            catch
            {
                // 派送為盡力而為：MQTT 不可用或已關窗不應影響看板。
            }
            finally
            {
                _mqttGate.Release();
            }
        });
    }

    private void ApplyBoardHighlight()
    {
        var byChannel = _board.ToDictionary(c => c.ChannelId);
        foreach (var (channelId, border) in _tileBorders)
        {
            border.Background = channelId is { } cid && byChannel.TryGetValue(cid, out var cell)
                ? new SolidColorBrush(cell.Highlight
                    ? Color.FromRgb(0x7A, 0x1A, 0x1A)
                    : Color.FromRgb(0x1A, 0x3A, 0x5A))
                : new SolidColorBrush(Color.FromRgb(0x0E, 0x1D, 0x33));
        }
    }
}
