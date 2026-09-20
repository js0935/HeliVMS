using System.Windows;
using System.Windows.Controls;
using HeliVMS.Alarms;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 分析情境視窗（M52，§14.7 #6；§5.6）：維護模組化分析區（跨線／侵入／聚集），
/// 並可以範例偵測驗證評估結果。
/// </summary>
public partial class AnalyticsWindow : Window
{
    private readonly AnalyticsZoneRepository _zones;

    public AnalyticsWindow(SqliteStore store)
    {
        _zones = new AnalyticsZoneRepository(store);
        _store = store;

        InitializeComponent();

        var channels = LoadChannels();
        AnalyticsChannelCombo.ItemsSource = channels;
        if (channels.Count > 0)
        {
            AnalyticsChannelCombo.SelectedIndex = 0;
        }

        AnalyticsModuleCombo.ItemsSource = AnalyticsModuleCatalog.All;
        AnalyticsModuleCombo.SelectedIndex = 0;
        AnalyticsDirectionCombo.ItemsSource = new[] { AnalyticsDirections.Both, AnalyticsDirections.AToB, AnalyticsDirections.BToA };
        AnalyticsDirectionCombo.SelectedIndex = 0;
        AnalyticsPolygonBox.Text = "0.2,0.2;0.8,0.2;0.8,0.8;0.2,0.8";
        AnalyticsMinCountBox.Text = "0";
        AnalyticsDwellBox.Text = "0";

        Reload();
    }

    private readonly SqliteStore _store;

    private sealed record ChannelRow(int Id, string Display);

    private sealed record ZoneRow(
        int Id,
        string Name,
        string ModuleLabel,
        string ChannelLabel,
        string StateLabel,
        string GeometryLabel);

    private List<ChannelRow> LoadChannels()
        => _store.Query(
            "SELECT id, name FROM channels ORDER BY id;",
            static r =>
            {
                var list = new List<ChannelRow>();
                while (r.Read())
                {
                    list.Add(new ChannelRow(r.GetInt32(0), $"#{r.GetInt32(0)} {r.GetString(1)}"));
                }

                return list;
            });

    private Dictionary<int, string> ChannelNames()
        => _store.Query(
            "SELECT id, name FROM channels;",
            static r =>
            {
                var map = new Dictionary<int, string>();
                while (r.Read())
                {
                    map[r.GetInt32(0)] = r.GetString(1);
                }

                return map;
            });

    private void Reload()
    {
        var names = ChannelNames();
        var rows = new List<ZoneRow>();
        foreach (var record in _zones.List())
        {
            var info = AnalyticsModuleCatalog.For(record.Module);
            var points = AnalyticsGeometry.ParsePoints(record.Polygon);
            var channel = names.TryGetValue(record.ChannelId, out var n)
                ? $"#{record.ChannelId} {n}"
                : $"#{record.ChannelId}";

            rows.Add(new ZoneRow(
                record.Id,
                record.Name,
                info?.DisplayName ?? record.Module,
                channel,
                record.Enabled ? "啟用" : "停用",
                $"{points.Count} 點｜{record.Direction}｜min={record.MinCount}｜dwell={record.DwellSeconds}s"));
        }

        AnalyticsList.ItemsSource = rows;
    }

    private ZoneRow? Selected => AnalyticsList.SelectedItem as ZoneRow;

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        Reload();
        AnalyticsStatusText.Text = "已刷新分析區清單。";
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        if (AnalyticsChannelCombo.SelectedItem is not ChannelRow channel)
        {
            AnalyticsStatusText.Text = "新增失敗：請先建立頻道。";
            return;
        }

        if (AnalyticsModuleCombo.SelectedItem is not AnalyticsModuleCatalog.ModuleInfo module)
        {
            AnalyticsStatusText.Text = "新增失敗：請選擇模組。";
            return;
        }

        try
        {
            var minCount = int.TryParse(AnalyticsMinCountBox.Text, out var mc) ? mc : 0;
            var dwell = int.TryParse(AnalyticsDwellBox.Text, out var dw) ? dw : 0;
            var direction = AnalyticsDirectionCombo.SelectedItem as string ?? AnalyticsDirections.Both;

            var name = string.IsNullOrWhiteSpace(AnalyticsNameBox.Text)
                ? $"{module.DisplayName} @{channel.Id}"
                : AnalyticsNameBox.Text.Trim();

            var id = _zones.Add(name, channel.Id, module.Module, AnalyticsPolygonBox.Text.Trim(), direction, minCount, dwell);

            Reload();
            AnalyticsStatusText.Text = $"已新增分析區 #{id}（{module.DisplayName}）。";
        }
        catch (Exception ex)
        {
            AnalyticsStatusText.Text = $"新增失敗：{ex.Message}";
        }
    }

    private void OnToggleClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            AnalyticsStatusText.Text = "請先選擇一個分析區。";
            return;
        }

        var record = _zones.Get(row.Id);
        if (record is null)
        {
            AnalyticsStatusText.Text = $"找不到分析區 #{row.Id}。";
            return;
        }

        _zones.SetEnabled(row.Id, !record.Enabled);
        Reload();
        AnalyticsStatusText.Text = record.Enabled
            ? $"已停用分析區 #{row.Id}。"
            : $"已啟用分析區 #{row.Id}。";
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            AnalyticsStatusText.Text = "請先選擇一個分析區。";
            return;
        }

        _zones.Delete(row.Id);
        Reload();
        AnalyticsStatusText.Text = $"已刪除分析區 #{row.Id}。";
    }

    /// <summary>以位於幾何內部（跨線為兩側）的範例偵測連續評估，回報命中事件數。</summary>
    private void OnEvaluateClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            AnalyticsStatusText.Text = "請先選擇一個分析區。";
            return;
        }

        var record = _zones.Get(row.Id);
        if (record is null)
        {
            AnalyticsStatusText.Text = $"找不到分析區 #{row.Id}。";
            return;
        }

        var zone = AnalyticsZone.From(record);
        var evaluator = new AnalyticsZoneEvaluator();
        var now = DateTime.UtcNow;
        var events = new List<AnalyticsResult>();
        string? heatInfo = null;

        if (AnalyticsModuleKinds.IsLineModule(zone.Module) && zone.Polygon.Count >= 2)
        {
            var a = zone.Polygon[0];
            var b = zone.Polygon[1];
            var midX = (a.X + b.X) / 2;
            var midY = (a.Y + b.Y) / 2;
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Sqrt((dx * dx) + (dy * dy));
            if (len <= 0)
            {
                len = 1;
            }

            var nx = -dy / len;
            var ny = dx / len;
            const double off = 0.1;
            var p1 = Clamp(midX - (nx * off), midY - (ny * off));
            var p2 = Clamp(midX + (nx * off), midY + (ny * off));

            events.AddRange(evaluator.Evaluate(now, new[] { new AnalyticsDetection("vehicle", p1.X, p1.Y, 0.9f, "probe") }, new[] { zone }));
            events.AddRange(evaluator.Evaluate(now, new[] { new AnalyticsDetection("vehicle", p2.X, p2.Y, 0.9f, "probe") }, new[] { zone }));
        }
        else if (zone.Module == AnalyticsModuleKinds.Heatmap && zone.Polygon.Count >= 3)
        {
            var (cx, cy) = Center(zone);
            var center = Clamp(cx, cy);
            for (var i = 0; i < 25; i++)
            {
                events.AddRange(evaluator.Evaluate(now, new[] { new AnalyticsDetection("person", center.X, center.Y, 0.9f, "probe") }, new[] { zone }));
            }

            var cells = evaluator.HeatmapCells(zone.Id)
                .OrderByDescending(c => c.Count)
                .Take(3);
            heatInfo = string.Join(";", cells.Select(c => $"({c.Col},{c.Row})x{c.Count}"));
        }
        else if (zone.Module is AnalyticsModuleKinds.Loitering or AnalyticsModuleKinds.Stationary && zone.Polygon.Count >= 3)
        {
            var (cx, cy) = Center(zone);
            var center = Clamp(cx, cy);
            for (var i = 0; i <= zone.DwellSeconds; i++)
            {
                events.AddRange(evaluator.Evaluate(
                    now.AddSeconds(i),
                    new[] { new AnalyticsDetection("person", center.X, center.Y, 0.9f, "probe") },
                    new[] { zone }));
            }
        }
        else if (zone.Polygon.Count >= 3)
        {
            var (cx, cy) = Center(zone);
            var center = Clamp(cx, cy);
            for (var i = 0; i < AnalyticsZoneEvaluator.CrowdDebounceFrames; i++)
            {
                events.AddRange(evaluator.Evaluate(now, new[] { new AnalyticsDetection("person", center.X, center.Y, 0.9f, "probe") }, new[] { zone }));
            }
        }

        var info = AnalyticsModuleCatalog.For(zone.Module);
        var heatSuffix = string.IsNullOrEmpty(heatInfo) ? "" : $" 熱區：{heatInfo}";
        AnalyticsStatusText.Text = $"評估：{events.Count} 筆事件（{info?.DisplayName ?? zone.Module}）。" +
            (events.Count > 0 ? $" 首筆：{events[0].EventType}" : " 未命中（可能門檻過高或幾何無效）") +
            heatSuffix;
    }

    private static (double X, double Y) Center(AnalyticsZone zone)
    {
        var cx = 0.0;
        var cy = 0.0;
        foreach (var p in zone.Polygon)
        {
            cx += p.X;
            cy += p.Y;
        }

        return (cx / zone.Polygon.Count, cy / zone.Polygon.Count);
    }

    private static (double X, double Y) Clamp(double x, double y)
        => (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
}
