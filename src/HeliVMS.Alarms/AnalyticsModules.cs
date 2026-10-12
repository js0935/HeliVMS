using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>分析模組共用的跨幀狀態工具（M290）。</summary>
internal static class AnalyticsModuleState
{
    /// <summary>移除多邊形外（present 集合不含）之目標狀態，避免離開後狀態無限累積。</summary>
    public static void RemoveLeft<T>(Dictionary<(int Zone, string Key), T> state, int zoneId, HashSet<string> present)
    {
        foreach (var stale in state.Keys.Where(k => k.Zone == zoneId && !present.Contains(k.Key)).ToList())
        {
            state.Remove(stale);
        }
    }
}

/// <summary>周界／跨線模組（M52，§5.6）：有向線段（polygon 前兩點）跨越偵測。</summary>
public sealed class LineCrossModule : IAnalyticsModule
{
    private readonly Dictionary<(int Zone, string Key), int> _side = new();

    public string Module => AnalyticsModuleKinds.LineCross;

    public void Reset() => _side.Clear();

    public void Evaluate(
        AnalyticsModuleFrame frame,
        IReadOnlyList<(string Key, AnalyticsDetection Detection)> keyedDetections,
        IReadOnlyList<AnalyticsZone> zones,
        List<AnalyticsResult> results)
    {
        foreach (var zone in zones)
        {
            if (zone.Polygon.Count < 2)
            {
                continue;
            }

            var a = zone.Polygon[0];
            var b = zone.Polygon[1];
            var present = new HashSet<string>();

            foreach (var (key, detection) in keyedDetections)
            {
                var point = new NormalizedPoint(detection.X, detection.Y);
                var side = AnalyticsGeometry.SignedSide(a, b, point);
                present.Add(key);

                if (_side.TryGetValue((zone.Id, key), out var previous) &&
                    previous != 0 && side != 0 && previous != side)
                {
                    var direction = previous > 0 ? AnalyticsDirections.AToB : AnalyticsDirections.BToA;
                    if (zone.Direction == AnalyticsDirections.Both || zone.Direction == direction)
                    {
                        var label = direction == AnalyticsDirections.AToB ? "A→B" : "B→A";
                        results.Add(new AnalyticsResult(
                            zone.Module, AnalyticsModuleCatalog.EventLineCross, zone.Id, zone.Name, zone.ChannelId,
                            key, 1, $"跨越界線（{label}）"));
                    }
                }

                if (side != 0)
                {
                    _side[(zone.Id, key)] = side;
                }
            }

            foreach (var stale in _side.Keys.Where(k => k.Zone == zone.Id && !present.Contains(k.Key)).ToList())
            {
                _side.Remove(stale);
            }
        }
    }
}

/// <summary>區域侵入模組（M52，§5.6）：進入／離開多邊形。</summary>
public sealed class IntrusionModule : IAnalyticsModule
{
    private readonly Dictionary<int, HashSet<string>> _inside = new();

    public string Module => AnalyticsModuleKinds.Intrusion;

    public void Reset() => _inside.Clear();

    public void Evaluate(
        AnalyticsModuleFrame frame,
        IReadOnlyList<(string Key, AnalyticsDetection Detection)> keyedDetections,
        IReadOnlyList<AnalyticsZone> zones,
        List<AnalyticsResult> results)
    {
        foreach (var zone in zones)
        {
            if (zone.Polygon.Count < 3)
            {
                continue;
            }

            var insideNow = keyedDetections
                .Where(x => AnalyticsGeometry.PointInPolygon(zone.Polygon, new NormalizedPoint(x.Detection.X, x.Detection.Y)))
                .Select(x => x.Key)
                .ToHashSet();

            if (!_inside.TryGetValue(zone.Id, out var previous))
            {
                previous = new HashSet<string>();
            }

            foreach (var key in insideNow)
            {
                if (!previous.Contains(key))
                {
                    results.Add(new AnalyticsResult(
                        zone.Module, AnalyticsModuleCatalog.EventIntrusion, zone.Id, zone.Name, zone.ChannelId,
                        key, insideNow.Count, $"進入區域（{insideNow.Count}）"));
                }
            }

            foreach (var key in previous)
            {
                if (!insideNow.Contains(key))
                {
                    results.Add(new AnalyticsResult(
                        zone.Module, AnalyticsModuleCatalog.EventIntrusion, zone.Id, zone.Name, zone.ChannelId,
                        key, insideNow.Count, $"離開區域（{insideNow.Count}）"));
                }
            }

            _inside[zone.Id] = insideNow;
        }
    }
}

/// <summary>人群聚集模組（M52，§5.6）：區域內數量達門檻且連續達去抖動幀數才觸發。</summary>
public sealed class CrowdModule : IAnalyticsModule
{
    /// <summary>人群聚集需連續達標幀數（去抖動）。</summary>
    public const int DebounceFrames = 3;

    private readonly Dictionary<int, int> _streak = new();

    public string Module => AnalyticsModuleKinds.Crowd;

    public void Reset() => _streak.Clear();

    public void Evaluate(
        AnalyticsModuleFrame frame,
        IReadOnlyList<(string Key, AnalyticsDetection Detection)> keyedDetections,
        IReadOnlyList<AnalyticsZone> zones,
        List<AnalyticsResult> results)
    {
        foreach (var zone in zones)
        {
            if (zone.Polygon.Count < 3 || zone.MinCount <= 0)
            {
                _streak.Remove(zone.Id);
                continue;
            }

            var count = keyedDetections.Count(x =>
                AnalyticsGeometry.PointInPolygon(zone.Polygon, new NormalizedPoint(x.Detection.X, x.Detection.Y)));

            _streak.TryGetValue(zone.Id, out var streak);
            if (count >= zone.MinCount)
            {
                streak++;
                if (streak == DebounceFrames)
                {
                    results.Add(new AnalyticsResult(
                        zone.Module, AnalyticsModuleCatalog.EventCrowd, zone.Id, zone.Name, zone.ChannelId,
                        null, count, $"人群聚集（{count}≥{zone.MinCount}）"));
                }
            }
            else
            {
                streak = 0;
            }

            _streak[zone.Id] = streak;
        }
    }
}

/// <summary>長時間徘徊模組（M58，§5.6）：目標在多邊形內連續停留達門檻觸發一次；離開後重新進入才會再觸發。</summary>
public sealed class LoiteringModule : IAnalyticsModule
{
    private readonly Dictionary<(int Zone, string Key), DateTime> _dwellSince = new();
    private readonly Dictionary<(int Zone, string Key), bool> _dwellFired = new();

    public string Module => AnalyticsModuleKinds.Loitering;

    public void Reset()
    {
        _dwellSince.Clear();
        _dwellFired.Clear();
    }

    public void Evaluate(
        AnalyticsModuleFrame frame,
        IReadOnlyList<(string Key, AnalyticsDetection Detection)> keyedDetections,
        IReadOnlyList<AnalyticsZone> zones,
        List<AnalyticsResult> results)
    {
        foreach (var zone in zones)
        {
            if (zone.Polygon.Count < 3)
            {
                continue;
            }

            var insideNow = keyedDetections
                .Where(x => AnalyticsGeometry.PointInPolygon(zone.Polygon, new NormalizedPoint(x.Detection.X, x.Detection.Y)))
                .Select(x => x.Key)
                .ToHashSet();

            foreach (var key in insideNow)
            {
                var sinceKey = (zone.Id, key);
                if (!_dwellSince.TryGetValue(sinceKey, out var since))
                {
                    since = frame.SnapshotUtc;
                    _dwellSince[sinceKey] = since;
                    _dwellFired[sinceKey] = false;
                }

                var elapsed = Math.Max(0, (int)Math.Floor((frame.SnapshotUtc - since).TotalSeconds));
                if (!_dwellFired[sinceKey] && elapsed >= zone.DwellSeconds)
                {
                    _dwellFired[sinceKey] = true;
                    results.Add(new AnalyticsResult(
                        zone.Module, AnalyticsModuleCatalog.EventLoitering, zone.Id, zone.Name, zone.ChannelId,
                        key, elapsed, $"長時間徘徊（{elapsed}s≥{zone.DwellSeconds}s）"));
                }
            }

            AnalyticsModuleState.RemoveLeft(_dwellSince, zone.Id, insideNow);
            AnalyticsModuleState.RemoveLeft(_dwellFired, zone.Id, insideNow);
        }
    }
}

/// <summary>靜止物／遺留物模組（M58，§5.6）：與徘徊同為停留判定，但移動超過容忍距離即重計停留。</summary>
public sealed class StationaryModule : IAnalyticsModule
{
    /// <summary>靜止物判定移動容忍（正規化距離，超過即視為移動而重計停留）。</summary>
    public const double MoveTolerance = 0.02;

    private readonly Dictionary<(int Zone, string Key), DateTime> _dwellSince = new();
    private readonly Dictionary<(int Zone, string Key), bool> _dwellFired = new();
    private readonly Dictionary<(int Zone, string Key), NormalizedPoint> _lastPos = new();

    public string Module => AnalyticsModuleKinds.Stationary;

    public void Reset()
    {
        _dwellSince.Clear();
        _dwellFired.Clear();
        _lastPos.Clear();
    }

    public void Evaluate(
        AnalyticsModuleFrame frame,
        IReadOnlyList<(string Key, AnalyticsDetection Detection)> keyedDetections,
        IReadOnlyList<AnalyticsZone> zones,
        List<AnalyticsResult> results)
    {
        foreach (var zone in zones)
        {
            if (zone.Polygon.Count < 3)
            {
                continue;
            }

            var insideNow = keyedDetections
                .Where(x => AnalyticsGeometry.PointInPolygon(zone.Polygon, new NormalizedPoint(x.Detection.X, x.Detection.Y)))
                .ToDictionary(x => x.Key, x => x.Detection);

            foreach (var (key, detection) in insideNow)
            {
                var sinceKey = (zone.Id, key);
                var point = new NormalizedPoint(detection.X, detection.Y);

                if (!_dwellSince.TryGetValue(sinceKey, out var since))
                {
                    since = frame.SnapshotUtc;
                    _dwellSince[sinceKey] = since;
                    _dwellFired[sinceKey] = false;
                    _lastPos[sinceKey] = point;
                }
                else if (Distance(point, _lastPos[sinceKey]) > MoveTolerance)
                {
                    _dwellSince[sinceKey] = frame.SnapshotUtc;
                    _dwellFired[sinceKey] = false;
                    _lastPos[sinceKey] = point;
                }

                var elapsed = Math.Max(0, (int)Math.Floor((frame.SnapshotUtc - _dwellSince[sinceKey]).TotalSeconds));
                if (!_dwellFired[sinceKey] && elapsed >= zone.DwellSeconds)
                {
                    _dwellFired[sinceKey] = true;
                    results.Add(new AnalyticsResult(
                        zone.Module, AnalyticsModuleCatalog.EventStationary, zone.Id, zone.Name, zone.ChannelId,
                        key, elapsed, $"靜止/遺留（{elapsed}s≥{zone.DwellSeconds}s）"));
                }
            }

            var present = insideNow.Keys.ToHashSet();
            AnalyticsModuleState.RemoveLeft(_dwellSince, zone.Id, present);
            AnalyticsModuleState.RemoveLeft(_dwellFired, zone.Id, present);
            AnalyticsModuleState.RemoveLeft(_lastPos, zone.Id, present);
        }
    }

    private static double Distance(NormalizedPoint a, NormalizedPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}

/// <summary>車流統計模組（M58，§5.6）：有向線段跨越即累計，回報累計車流事件。</summary>
public sealed class TrafficModule : IAnalyticsModule
{
    private readonly Dictionary<(int Zone, string Key), int> _side = new();
    private readonly Dictionary<(int Zone, string Direction), int> _count = new();

    public string Module => AnalyticsModuleKinds.Traffic;

    public void Reset()
    {
        _side.Clear();
        _count.Clear();
    }

    public void Evaluate(
        AnalyticsModuleFrame frame,
        IReadOnlyList<(string Key, AnalyticsDetection Detection)> keyedDetections,
        IReadOnlyList<AnalyticsZone> zones,
        List<AnalyticsResult> results)
    {
        foreach (var zone in zones)
        {
            if (zone.Polygon.Count < 2)
            {
                continue;
            }

            var a = zone.Polygon[0];
            var b = zone.Polygon[1];
            var present = new HashSet<string>();

            foreach (var (key, detection) in keyedDetections)
            {
                var point = new NormalizedPoint(detection.X, detection.Y);
                var side = AnalyticsGeometry.SignedSide(a, b, point);
                present.Add(key);

                if (_side.TryGetValue((zone.Id, key), out var previous) &&
                    previous != 0 && side != 0 && previous != side)
                {
                    var direction = previous > 0 ? AnalyticsDirections.AToB : AnalyticsDirections.BToA;
                    if (zone.Direction == AnalyticsDirections.Both || zone.Direction == direction)
                    {
                        var label = direction == AnalyticsDirections.AToB ? "A→B" : "B→A";
                        _count.TryGetValue((zone.Id, direction), out var cumulative);
                        cumulative++;
                        _count[(zone.Id, direction)] = cumulative;
                        results.Add(new AnalyticsResult(
                            zone.Module, AnalyticsModuleCatalog.EventTraffic, zone.Id, zone.Name, zone.ChannelId,
                            key, cumulative, $"車流 {label} 累計 {cumulative}"));
                    }
                }

                if (side != 0)
                {
                    _side[(zone.Id, key)] = side;
                }
            }

            foreach (var stale in _side.Keys.Where(k => k.Zone == zone.Id && !present.Contains(k.Key)).ToList())
            {
                _side.Remove(stale);
            }
        }
    }
}

/// <summary>熱區圖模組（M58，§5.6）：多邊形內偵測按網格累計，無事件輸出，供 <see cref="IHeatmapSource"/> 查詢。</summary>
public sealed class HeatmapModule : IAnalyticsModule, IHeatmapSource
{
    /// <summary>熱區圖格子邊長（5×5）。</summary>
    public const int GridSize = 5;

    private readonly Dictionary<(int Zone, int Cell), int> _heat = new();

    public string Module => AnalyticsModuleKinds.Heatmap;

    public void Reset() => _heat.Clear();

    public void Evaluate(
        AnalyticsModuleFrame frame,
        IReadOnlyList<(string Key, AnalyticsDetection Detection)> keyedDetections,
        IReadOnlyList<AnalyticsZone> zones,
        List<AnalyticsResult> results)
    {
        foreach (var zone in zones)
        {
            if (zone.Polygon.Count < 3)
            {
                continue;
            }

            foreach (var (_, detection) in keyedDetections)
            {
                var point = new NormalizedPoint(detection.X, detection.Y);
                if (!AnalyticsGeometry.PointInPolygon(zone.Polygon, point))
                {
                    continue;
                }

                var col = Math.Clamp((int)Math.Floor(point.X * GridSize), 0, GridSize - 1);
                var row = Math.Clamp((int)Math.Floor(point.Y * GridSize), 0, GridSize - 1);
                var cell = (row * GridSize) + col;
                _heat.TryGetValue((zone.Id, cell), out var count);
                _heat[(zone.Id, cell)] = count + 1;
            }
        }
    }

    public IReadOnlyList<AnalyticsZoneEvaluator.HeatCell> HeatmapCells(int zoneId)
        => _heat.Where(kv => kv.Key.Zone == zoneId)
            .Select(kv => new AnalyticsZoneEvaluator.HeatCell(
                kv.Key.Cell / GridSize,
                kv.Key.Cell % GridSize,
                kv.Value))
            .ToArray();

    public int HeatmapTotal(int zoneId)
        => _heat.Where(kv => kv.Key.Zone == zoneId).Sum(kv => kv.Value);
}

/// <summary>
/// 尾隨／逆行模組（M59，§5.6）：2 點線段管制流向，<c>Direction</c>≠both 時跨線方向違反＝逆行；
/// <c>DwellSeconds</c> 窗內另一 key 同方向跨線＝尾隨（以 per-key 跨線狀態近似，不需 §5.7 完整追蹤）。
/// </summary>
public sealed class TailgatingModule : IAnalyticsModule
{
    private readonly Dictionary<(int Zone, string Key), int> _side = new();
    private readonly Dictionary<(int Zone, string Direction), List<(DateTime Utc, string Key)>> _recent = new();

    public string Module => AnalyticsModuleKinds.Tailgating;

    public void Reset()
    {
        _side.Clear();
        _recent.Clear();
    }

    public void Evaluate(
        AnalyticsModuleFrame frame,
        IReadOnlyList<(string Key, AnalyticsDetection Detection)> keyedDetections,
        IReadOnlyList<AnalyticsZone> zones,
        List<AnalyticsResult> results)
    {
        foreach (var zone in zones)
        {
            if (zone.Polygon.Count < 2)
            {
                continue;
            }

            var a = zone.Polygon[0];
            var b = zone.Polygon[1];
            var present = new HashSet<string>();

            foreach (var (key, detection) in keyedDetections)
            {
                var point = new NormalizedPoint(detection.X, detection.Y);
                var side = AnalyticsGeometry.SignedSide(a, b, point);
                present.Add(key);

                if (_side.TryGetValue((zone.Id, key), out var previous) &&
                    previous != 0 && side != 0 && previous != side)
                {
                    var direction = previous > 0 ? AnalyticsDirections.AToB : AnalyticsDirections.BToA;
                    var label = direction == AnalyticsDirections.AToB ? "A→B" : "B→A";

                    if (zone.Direction != AnalyticsDirections.Both && zone.Direction != direction)
                    {
                        results.Add(new AnalyticsResult(
                            zone.Module, AnalyticsModuleCatalog.EventTailgating, zone.Id, zone.Name, zone.ChannelId,
                            key, 1, $"逆行 {label}（限制 {zone.Direction}）"));
                    }

                    if (zone.DwellSeconds > 0)
                    {
                        if (!_recent.TryGetValue((zone.Id, direction), out var recent))
                        {
                            recent = new List<(DateTime, string)>();
                            _recent[(zone.Id, direction)] = recent;
                        }

                        var windowStart = frame.SnapshotUtc.AddSeconds(-zone.DwellSeconds);
                        recent.RemoveAll(x => x.Utc < windowStart);
                        var prior = recent.FirstOrDefault(x => x.Key != key);
                        if (prior != default)
                        {
                            var elapsed = (int)Math.Round((frame.SnapshotUtc - prior.Utc).TotalSeconds);
                            results.Add(new AnalyticsResult(
                                zone.Module, AnalyticsModuleCatalog.EventTailgating, zone.Id, zone.Name, zone.ChannelId,
                                key, 1, $"尾隨 {label}（{elapsed}s 窗）"));
                        }

                        recent.Add((frame.SnapshotUtc, key));
                    }
                }

                if (side != 0)
                {
                    _side[(zone.Id, key)] = side;
                }
            }

            foreach (var stale in _side.Keys.Where(k => k.Zone == zone.Id && !present.Contains(k.Key)).ToList())
            {
                _side.Remove(stale);
            }
        }
    }
}

/// <summary>
/// 內建分析模組註冊表（M290，§5.6）：提供預設模組集，並可依授權旗標過濾。
/// </summary>
public static class AnalyticsModuleRegistry
{
    /// <summary>全部內建模組（順序對應 <c>AnalyticsModuleKinds.All</c>）。</summary>
    public static IReadOnlyList<IAnalyticsModule> CreateDefault() =>
    [
        new LineCrossModule(),
        new IntrusionModule(),
        new CrowdModule(),
        new LoiteringModule(),
        new StationaryModule(),
        new TrafficModule(),
        new HeatmapModule(),
        new TailgatingModule(),
    ];

    /// <summary>依授權旗標過濾模組；未授權的模組不會進入評估器，也就不會產生事件（§5.6「未授權模組不顯示」）。</summary>
    public static IReadOnlyList<IAnalyticsModule> Filter(
        IEnumerable<IAnalyticsModule> modules,
        Func<string, bool> licenseAllows)
        => modules.Where(m => licenseAllows(m.LicenseFeature)).ToArray();
}
