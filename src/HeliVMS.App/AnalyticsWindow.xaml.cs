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
        ApplyI18n();

        var channels = LoadChannels();
        AnalyticsChannelCombo.ItemsSource = channels;
        if (channels.Count > 0)
        {
            AnalyticsChannelCombo.SelectedIndex = 0;
        }

        AnalyticsModuleCombo.ItemsSource = BuildModules();
        AnalyticsModuleCombo.SelectedIndex = 0;
        AnalyticsDirectionCombo.ItemsSource = BuildDirections();
        AnalyticsDirectionCombo.SelectedIndex = 0;
        AnalyticsPolygonBox.Text = "0.2,0.2;0.8,0.2;0.8,0.8;0.2,0.8";
        AnalyticsMinCountBox.Text = "0";
        AnalyticsDwellBox.Text = "0";

        Reload();
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("Analytics.Title");
        AnalyticsDescText.Text = Localizer.T("Analytics.FormDesc");
        AnalyticsNameLabel.Text = Localizer.T("Analytics.Name");
        AnalyticsChannelLabel.Text = Localizer.T("Analytics.Channel");
        AnalyticsModuleLabel.Text = Localizer.T("Analytics.Module");
        AnalyticsDirectionLabel.Text = Localizer.T("Analytics.Direction");
        AnalyticsPolygonLabel.Text = Localizer.T("Analytics.Polygon");
        AnalyticsMinCountLabel.Text = Localizer.T("Analytics.MinCount");
        AnalyticsDwellLabel.Text = Localizer.T("Analytics.DwellSec");
        AnalyticsAddButton.Content = Localizer.T("Analytics.Add");
        AnalyticsAddButton.ToolTip = Localizer.T("Analytics.AddTip");
        AnalyticsListHeaderText.Text = Localizer.T("Analytics.ListHeader");
        AnalyticsColName.Header = Localizer.T("Analytics.ColName");
        AnalyticsColModule.Header = Localizer.T("Analytics.Module");
        AnalyticsColChannel.Header = Localizer.T("Analytics.Channel");
        AnalyticsColState.Header = Localizer.T("Analytics.ColState");
        AnalyticsColGeometry.Header = Localizer.T("Analytics.ColGeometry");
        AnalyticsRefreshButton.Content = Localizer.T("Analytics.Refresh");
        AnalyticsRefreshButton.ToolTip = Localizer.T("Analytics.RefreshTip");
        AnalyticsToggleButton.Content = Localizer.T("Analytics.Toggle");
        AnalyticsToggleButton.ToolTip = Localizer.T("Analytics.ToggleTip");
        AnalyticsEvaluateButton.Content = Localizer.T("Analytics.Evaluate");
        AnalyticsEvaluateButton.ToolTip = Localizer.T("Analytics.EvaluateTip");
        AnalyticsDeleteButton.Content = Localizer.T("Analytics.Delete");
        AnalyticsDeleteButton.ToolTip = Localizer.T("Analytics.DeleteTip");
    }

    private static readonly Dictionary<string, string> ModuleKeyMap = new()
    {
        [AnalyticsModuleKinds.LineCross] = "Analytics.ModLineCross",
        [AnalyticsModuleKinds.Intrusion] = "Analytics.ModIntrusion",
        [AnalyticsModuleKinds.Crowd] = "Analytics.ModCrowd",
        [AnalyticsModuleKinds.Loitering] = "Analytics.ModLoitering",
        [AnalyticsModuleKinds.Stationary] = "Analytics.ModStationary",
        [AnalyticsModuleKinds.Traffic] = "Analytics.ModTraffic",
        [AnalyticsModuleKinds.Heatmap] = "Analytics.ModHeatmap",
        [AnalyticsModuleKinds.Tailgating] = "Analytics.ModTailgating",
    };

    private static readonly Dictionary<string, string> DirectionKeyMap = new()
    {
        [AnalyticsDirections.Both] = "Analytics.DirBoth",
        [AnalyticsDirections.AToB] = "Analytics.DirAToB",
        [AnalyticsDirections.BToA] = "Analytics.DirBToA",
    };

    private sealed record ModuleOption(string Module, string Label);

    private sealed record DirectionOption(string Direction, string Label);

    private static string ModuleLabel(string module)
        => ModuleKeyMap.TryGetValue(module, out var key) ? Localizer.T(key) : module;

    private static string DirectionLabel(string direction)
        => DirectionKeyMap.TryGetValue(direction, out var key) ? Localizer.T(key) : direction;

    private static List<ModuleOption> BuildModules()
        => AnalyticsModuleCatalog.All.Select(m => new ModuleOption(m.Module, ModuleLabel(m.Module))).ToList();

    private static List<DirectionOption> BuildDirections()
        => new[] { AnalyticsDirections.Both, AnalyticsDirections.AToB, AnalyticsDirections.BToA }
            .Select(d => new DirectionOption(d, DirectionLabel(d)))
            .ToList();

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
            var points = AnalyticsGeometry.ParsePoints(record.Polygon);
            var channel = names.TryGetValue(record.ChannelId, out var n)
                ? $"#{record.ChannelId} {n}"
                : $"#{record.ChannelId}";

            rows.Add(new ZoneRow(
                record.Id,
                record.Name,
                ModuleLabel(record.Module),
                channel,
                record.Enabled ? Localizer.T("Analytics.Enabled") : Localizer.T("Analytics.Disabled"),
                string.Format(Localizer.T("Analytics.GeometryCell"), points.Count, DirectionLabel(record.Direction), record.MinCount, record.DwellSeconds)));
        }

        AnalyticsList.ItemsSource = rows;
    }

    private ZoneRow? Selected => AnalyticsList.SelectedItem as ZoneRow;

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        Reload();
        AnalyticsStatusText.Text = Localizer.T("Analytics.Refreshed");
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        if (AnalyticsChannelCombo.SelectedItem is not ChannelRow channel)
        {
            AnalyticsStatusText.Text = Localizer.T("Analytics.NeedChannel");
            return;
        }

        if (AnalyticsModuleCombo.SelectedItem is not ModuleOption module)
        {
            AnalyticsStatusText.Text = Localizer.T("Analytics.NeedModule");
            return;
        }

        try
        {
            var minCount = int.TryParse(AnalyticsMinCountBox.Text, out var mc) ? mc : 0;
            var dwell = int.TryParse(AnalyticsDwellBox.Text, out var dw) ? dw : 0;
            var direction = AnalyticsDirectionCombo.SelectedItem is DirectionOption dir ? dir.Direction : AnalyticsDirections.Both;

            var name = string.IsNullOrWhiteSpace(AnalyticsNameBox.Text)
                ? $"{module.Label} @{channel.Id}"
                : AnalyticsNameBox.Text.Trim();

            var id = _zones.Add(name, channel.Id, module.Module, AnalyticsPolygonBox.Text.Trim(), direction, minCount, dwell);

            Reload();
            AnalyticsStatusText.Text = string.Format(Localizer.T("Analytics.Added"), id, module.Label);
        }
        catch (Exception ex)
        {
            AnalyticsStatusText.Text = string.Format(Localizer.T("Analytics.AddFailed"), ex.Message);
        }
    }

    private void OnToggleClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            AnalyticsStatusText.Text = Localizer.T("Analytics.NeedSelect");
            return;
        }

        var record = _zones.Get(row.Id);
        if (record is null)
        {
            AnalyticsStatusText.Text = string.Format(Localizer.T("Analytics.NotFound"), row.Id);
            return;
        }

        _zones.SetEnabled(row.Id, !record.Enabled);
        Reload();
        AnalyticsStatusText.Text = record.Enabled
            ? string.Format(Localizer.T("Analytics.NowDisabled"), row.Id)
            : string.Format(Localizer.T("Analytics.NowEnabled"), row.Id);
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            AnalyticsStatusText.Text = Localizer.T("Analytics.NeedSelect");
            return;
        }

        _zones.Delete(row.Id);
        Reload();
        AnalyticsStatusText.Text = string.Format(Localizer.T("Analytics.Deleted"), row.Id);
    }

    /// <summary>以位於幾何內部（跨線為兩側）的範例偵測連續評估，回報命中事件數。</summary>
    private void OnEvaluateClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            AnalyticsStatusText.Text = Localizer.T("Analytics.NeedSelect");
            return;
        }

        var record = _zones.Get(row.Id);
        if (record is null)
        {
            AnalyticsStatusText.Text = string.Format(Localizer.T("Analytics.NotFound"), row.Id);
            return;
        }

        var zone = AnalyticsZone.From(record);
        var evaluator = new AnalyticsZoneEvaluator();
        var now = DateTime.UtcNow;
        var events = new List<AnalyticsResult>();
        string? heatInfo = null;

        if (zone.Module == AnalyticsModuleKinds.Tailgating && zone.Polygon.Count >= 2)
        {
            var points = ProbePoints(zone);
            events.AddRange(evaluator.Evaluate(now, new[] { new AnalyticsDetection("person", points.p1.X, points.p1.Y, 0.9f, "probeA") }, new[] { zone }));
            events.AddRange(evaluator.Evaluate(now.AddSeconds(1), new[] { new AnalyticsDetection("person", points.p2.X, points.p2.Y, 0.9f, "probeA") }, new[] { zone }));
            if (zone.DwellSeconds >= 1)
            {
                events.AddRange(evaluator.Evaluate(now.AddSeconds(1), new[] { new AnalyticsDetection("person", points.p1.X, points.p1.Y, 0.9f, "probeB") }, new[] { zone }));
                events.AddRange(evaluator.Evaluate(now.AddSeconds(2), new[] { new AnalyticsDetection("person", points.p2.X, points.p2.Y, 0.9f, "probeB") }, new[] { zone }));
            }
        }
        else if (AnalyticsModuleKinds.IsLineModule(zone.Module) && zone.Polygon.Count >= 2)
        {
            var points = ProbePoints(zone);
            events.AddRange(evaluator.Evaluate(now, new[] { new AnalyticsDetection("vehicle", points.p1.X, points.p1.Y, 0.9f, "probe") }, new[] { zone }));
            events.AddRange(evaluator.Evaluate(now, new[] { new AnalyticsDetection("vehicle", points.p2.X, points.p2.Y, 0.9f, "probe") }, new[] { zone }));
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

        var moduleLabel = ModuleLabel(zone.Module);
        var heatSuffix = string.IsNullOrEmpty(heatInfo) ? "" : string.Format(Localizer.T("Analytics.HeatSuffix"), heatInfo);
        AnalyticsStatusText.Text =
            string.Format(Localizer.T("Analytics.EvalResult"), events.Count, moduleLabel) +
            (events.Count > 0
                ? string.Format(Localizer.T("Analytics.EvalFirst"), events[0].EventType)
                : Localizer.T("Analytics.EvalNone")) +
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

    private static ((double X, double Y) p1, (double X, double Y) p2) ProbePoints(AnalyticsZone zone)
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
        return (
            Clamp(midX - (nx * off), midY - (ny * off)),
            Clamp(midX + (nx * off), midY + (ny * off)));
    }

    private static (double X, double Y) Clamp(double x, double y)
        => (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
}
