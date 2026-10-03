using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<(long? ChannelId, Border Border)> _tileBorders = [];

    private IReadOnlyList<BoardCell> _board = [];
    private long? _selectedTileId;

    public SmartwallWindow(SqliteStore store)
    {
        _layouts = new SmartwallLayoutRepository(store);
        _events = new AlarmEventRepository(store);
        _triage = new AlarmTriageRepository(store);
        _channels = new ChannelRepository(store);
        InitializeComponent();

        ChannelCombo.DisplayMemberPath = nameof(ChannelInfo.Name);
        ChannelCombo.ItemsSource = _channels.List();
        RefreshLayouts();
        RefreshBoard();
        _timer.Tick += (_, _) => RefreshBoard();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
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
            TileStatusText.Text = $"已建立版面「{name}」{rows}x{cols}。";
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            TileStatusText.Text = $"建立失敗：{ex.Message}";
        }
    }

    private void OnRenameLayoutClicked(object sender, RoutedEventArgs e)
    {
        if (LayoutList.SelectedItem is not SmartwallLayout layout)
        {
            TileStatusText.Text = "請先選取要更名的版面。";
            return;
        }

        var name = LayoutNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TileStatusText.Text = "請輸入版面名稱。";
            return;
        }

        try
        {
            _layouts.RenameLayout(layout.Id, name);
            RefreshLayouts();
            TileStatusText.Text = $"版面已更名為「{name}」。";
        }
        catch (ArgumentException ex)
        {
            TileStatusText.Text = $"更名失敗：{ex.Message}";
        }
    }

    private bool TryReadNewLayout(out string name, out int rows, out int cols)
    {
        name = LayoutNameBox.Text.Trim();
        rows = 0;
        cols = 0;
        if (string.IsNullOrWhiteSpace(name))
        {
            TileStatusText.Text = "請輸入版面名稱。";
            return false;
        }

        if (!int.TryParse(LayoutRowsBox.Text, out rows) || !int.TryParse(LayoutColsBox.Text, out cols))
        {
            TileStatusText.Text = "列與欄必須是整數（1..16）。";
            return false;
        }

        return true;
    }

    private void OnAddTileClicked(object sender, RoutedEventArgs e)
    {
        if (LayoutList.SelectedItem is not SmartwallLayout layout)
        {
            TileStatusText.Text = "請先選取版面。";
            return;
        }

        if (!int.TryParse(TileRowBox.Text, out var row) || !int.TryParse(TileColBox.Text, out var col) ||
            !int.TryParse(TileRowSpanBox.Text, out var rowSpan) || !int.TryParse(TileColSpanBox.Text, out var colSpan))
        {
            TileStatusText.Text = "列／欄／跨距必須是整數。";
            return;
        }

        var channelId = (ChannelCombo.SelectedItem as ChannelInfo)?.Id;
        if (channelId is null && TileViewCombo.SelectedIndex != 1)
        {
            TileStatusText.Text = "單鏡頭方塊必須選擇頻道（或改用馬賽克）。";
            return;
        }

        var view = TileViewCombo.SelectedIndex == 1 ? 1 : 0;
        var position = _layouts.GetTiles(layout.Id).Count;
        try
        {
            var id = _layouts.AddTile(layout.Id, row, col, rowSpan, colSpan, channelId, view, position);
            RenderLayout();
            TileStatusText.Text = $"已加入方塊 #{id}（{row},{col}＋{rowSpan}x{colSpan}）。";
        }
        catch (ArgumentException ex)
        {
            TileStatusText.Text = $"加入失敗：{ex.Message}";
        }
    }

    private void OnRemoveTileClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTileId is not { } id)
        {
            TileStatusText.Text = "請先在網格中點選要移除的方塊。";
            return;
        }

        _layouts.RemoveTile(id);
        _selectedTileId = null;
        RenderLayout();
        TileStatusText.Text = $"已移除方塊 #{id}。";
    }

    private void SelectTile(long id)
    {
        _selectedTileId = id;
        RenderLayout();
        TileStatusText.Text = $"已選取方塊 #{id}，可按「移除選取方塊」。";
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
                ? "馬賽克"
                : tile.ChannelId is { } cid && channels.TryGetValue(cid, out var name) ? name : "（未指派）";
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
            ? "目前保留窗內無事件。"
            : $"保留窗內 {_board.Count} 個頻道有事件；紅底為 ≤5 秒。";
        ApplyBoardHighlight();
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
