namespace HeliVMS.Alarms;

/// <summary>分析用偵測輸入（中心點正規化 0..1）（M52，§14.7 #6）。</summary>
public sealed record AnalyticsDetection(string Class, double X, double Y, float Confidence = 1f, string? TrackId = null);

/// <summary>分析情境評估結果（M52，§14.7 #6）。</summary>
public sealed record AnalyticsResult(
    string Module,
    string EventType,
    int ZoneId,
    string ZoneName,
    int ChannelId,
    string? TrackId,
    int Count,
    string Detail);

/// <summary>
/// 模組化分析情境評估器（M52，§14.7 #6；§5.6）：逐幀以幾何規則判定周界跨線／區域侵入／人群聚集，
/// M58 增長時間徘徊／靜止物／車流統計／熱區圖（zone dwell＋cell 熱區近似，不需完整追蹤）。
/// 有狀態（跨幀追蹤），呼叫 <see cref="Reset"/> 可清空。
/// </summary>
public sealed class AnalyticsZoneEvaluator
{
    /// <summary>人群聚集需連續達標幀數（去抖動）。</summary>
    public const int CrowdDebounceFrames = 3;

    /// <summary>熱區圖格子邊長（5×5）。（M58）</summary>
    public const int HeatmapGridSize = 5;

    /// <summary>靜止物判定移動容忍（正規化距離，超過即視為移動而重計停留）。（M58）</summary>
    public const double StationaryMoveTolerance = 0.02;

    /// <summary>熱區圖單格計數。（M58）</summary>
    public sealed record HeatCell(int Row, int Col, int Count);

    private readonly Dictionary<int, HashSet<string>> _inside = new();
    private readonly Dictionary<(int Zone, string Key), int> _side = new();
    private readonly Dictionary<int, int> _crowdStreak = new();
    private readonly Dictionary<(int Zone, string Key), DateTime> _dwellSince = new();
    private readonly Dictionary<(int Zone, string Key), bool> _dwellFired = new();
    private readonly Dictionary<(int Zone, string Key), NormalizedPoint> _lastPos = new();
    private readonly Dictionary<(int Zone, string Direction), int> _trafficCount = new();
    private readonly Dictionary<(int Zone, int Cell), int> _heat = new();
    private readonly Dictionary<(int Zone, string Direction), List<(DateTime Utc, string Key)>> _followRecent = new();

    public void Reset()
    {
        _inside.Clear();
        _side.Clear();
        _crowdStreak.Clear();
        _dwellSince.Clear();
        _dwellFired.Clear();
        _lastPos.Clear();
        _trafficCount.Clear();
        _heat.Clear();
        _followRecent.Clear();
    }

    /// <summary>回傳指定分析區之熱區單格計數（row×col＝5×5，M58）。</summary>
    public IReadOnlyList<HeatCell> HeatmapCells(int zoneId)
        => _heat.Where(kv => kv.Key.Zone == zoneId)
            .Select(kv => new HeatCell(
                kv.Key.Cell / HeatmapGridSize,
                kv.Key.Cell % HeatmapGridSize,
                kv.Value))
            .ToArray();

    public int HeatmapTotal(int zoneId)
        => _heat.Where(kv => kv.Key.Zone == zoneId).Sum(kv => kv.Value);

    public IReadOnlyList<AnalyticsResult> Evaluate(
        DateTime snapshotUtc,
        IReadOnlyList<AnalyticsDetection> detections,
        IReadOnlyList<AnalyticsZone> zones)
    {
        var results = new List<AnalyticsResult>();
        var keyed = KeyDetections(detections);

        foreach (var zone in zones)
        {
            if (!zone.Enabled)
            {
                continue;
            }

            switch (zone.Module)
            {
                case Storage.AnalyticsModuleKinds.LineCross:
                    EvaluateLineCross(zone, keyed, results);
                    break;
                case Storage.AnalyticsModuleKinds.Intrusion:
                    EvaluateIntrusion(zone, keyed, results);
                    break;
                case Storage.AnalyticsModuleKinds.Crowd:
                    EvaluateCrowd(zone, keyed, results);
                    break;
                case Storage.AnalyticsModuleKinds.Loitering:
                    EvaluateLoitering(snapshotUtc, zone, keyed, results);
                    break;
                case Storage.AnalyticsModuleKinds.Stationary:
                    EvaluateStationary(snapshotUtc, zone, keyed, results);
                    break;
                case Storage.AnalyticsModuleKinds.Traffic:
                    EvaluateTraffic(zone, keyed, results);
                    break;
                case Storage.AnalyticsModuleKinds.Heatmap:
                    EvaluateHeatmap(zone, keyed);
                    break;
                case Storage.AnalyticsModuleKinds.Tailgating:
                    EvaluateTailgating(snapshotUtc, zone, keyed, results);
                    break;
            }
        }

        return results;
    }

    private static List<(string Key, AnalyticsDetection Detection)> KeyDetections(IReadOnlyList<AnalyticsDetection> detections)
    {
        var list = new List<(string, AnalyticsDetection)>();
        var counters = new Dictionary<string, int>();
        foreach (var detection in detections)
        {
            string key;
            if (!string.IsNullOrEmpty(detection.TrackId))
            {
                key = detection.TrackId!;
            }
            else
            {
                counters.TryGetValue(detection.Class, out var n);
                counters[detection.Class] = n + 1;
                key = $"{detection.Class}#{n}";
            }

            list.Add((key, detection));
        }

        return list;
    }

    private void EvaluateIntrusion(
        AnalyticsZone zone,
        List<(string Key, AnalyticsDetection Detection)> keyed,
        List<AnalyticsResult> results)
    {
        if (zone.Polygon.Count < 3)
        {
            return;
        }

        var insideNow = keyed
            .Where(x => AnalyticsGeometry.PointInPolygon(zone.Polygon, new NormalizedPoint(x.Detection.X, x.Detection.Y)))
            .Select(x => x.Key)
            .ToHashSet();

        _inside.TryGetValue(zone.Id, out var previous);
        previous ??= new HashSet<string>();

        foreach (var key in insideNow.Where(k => !previous.Contains(k)))
        {
            results.Add(new AnalyticsResult(
                zone.Module, AnalyticsModuleCatalog.EventIntrusion, zone.Id, zone.Name, zone.ChannelId,
                key, insideNow.Count, $"進入區域（{insideNow.Count}）"));
        }

        foreach (var key in previous.Where(k => !insideNow.Contains(k)))
        {
            results.Add(new AnalyticsResult(
                zone.Module, AnalyticsModuleCatalog.EventIntrusion, zone.Id, zone.Name, zone.ChannelId,
                key, insideNow.Count, $"離開區域（{insideNow.Count}）"));
        }

        _inside[zone.Id] = insideNow;
    }

    /// <summary>
    /// 有向線段為 polygon 前兩點 a→b；<c>a_to_b</c> 表示由左側（正）跨越至右側（負），<c>b_to_a</c> 反之。
    /// </summary>
    private void EvaluateLineCross(
        AnalyticsZone zone,
        List<(string Key, AnalyticsDetection Detection)> keyed,
        List<AnalyticsResult> results)
    {
        if (zone.Polygon.Count < 2)
        {
            return;
        }

        var a = zone.Polygon[0];
        var b = zone.Polygon[1];
        var present = new HashSet<string>();

        foreach (var (key, detection) in keyed)
        {
            var point = new NormalizedPoint(detection.X, detection.Y);
            var side = AnalyticsGeometry.SignedSide(a, b, point);
            present.Add(key);

            if (_side.TryGetValue((zone.Id, key), out var previous) &&
                previous != 0 && side != 0 && previous != side)
            {
                var direction = previous > 0 ? Storage.AnalyticsDirections.AToB : Storage.AnalyticsDirections.BToA;
                if (zone.Direction == Storage.AnalyticsDirections.Both || zone.Direction == direction)
                {
                    var label = direction == Storage.AnalyticsDirections.AToB ? "A→B" : "B→A";
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

    private void EvaluateCrowd(
        AnalyticsZone zone,
        List<(string Key, AnalyticsDetection Detection)> keyed,
        List<AnalyticsResult> results)
    {
        if (zone.Polygon.Count < 3 || zone.MinCount <= 0)
        {
            _crowdStreak.Remove(zone.Id);
            return;
        }

        var count = keyed.Count(x =>
            AnalyticsGeometry.PointInPolygon(zone.Polygon, new NormalizedPoint(x.Detection.X, x.Detection.Y)));

        _crowdStreak.TryGetValue(zone.Id, out var streak);
        if (count >= zone.MinCount)
        {
            streak++;
            if (streak == CrowdDebounceFrames)
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

        _crowdStreak[zone.Id] = streak;
    }

    /// <summary>
    /// 長時間徘徊（M58）：目標在多邊形內連續停留達 <see cref="AnalyticsZone.DwellSeconds"/> 即觸發一次；
    /// 離開後重新進入才會再次觸發。
    /// </summary>
    private void EvaluateLoitering(
        DateTime snapshotUtc,
        AnalyticsZone zone,
        List<(string Key, AnalyticsDetection Detection)> keyed,
        List<AnalyticsResult> results)
    {
        if (zone.Polygon.Count < 3)
        {
            return;
        }

        var insideNow = keyed
            .Where(x => AnalyticsGeometry.PointInPolygon(zone.Polygon, new NormalizedPoint(x.Detection.X, x.Detection.Y)))
            .Select(x => x.Key)
            .ToHashSet();

        foreach (var key in insideNow)
        {
            var sinceKey = (zone.Id, key);
            if (!_dwellSince.TryGetValue(sinceKey, out var since))
            {
                since = snapshotUtc;
                _dwellSince[sinceKey] = since;
                _dwellFired[sinceKey] = false;
            }

            var elapsed = Math.Max(0, (int)Math.Floor((snapshotUtc - since).TotalSeconds));
            if (!_dwellFired[sinceKey] && elapsed >= zone.DwellSeconds)
            {
                _dwellFired[sinceKey] = true;
                results.Add(new AnalyticsResult(
                    zone.Module, AnalyticsModuleCatalog.EventLoitering, zone.Id, zone.Name, zone.ChannelId,
                    key, elapsed, $"長時間徘徊（{elapsed}s≥{zone.DwellSeconds}s）"));
            }
        }

        RemoveLeft(_dwellSince, _dwellFired, zone.Id, insideNow);
    }

    /// <summary>
    /// 靜止物/遺留物（M58）：與 <see cref="EvaluateLoitering"/> 相同之停留判定，
    /// 但目標移動超過 <see cref="StationaryMoveTolerance"/> 即重計停留時間。
    /// </summary>
    private void EvaluateStationary(
        DateTime snapshotUtc,
        AnalyticsZone zone,
        List<(string Key, AnalyticsDetection Detection)> keyed,
        List<AnalyticsResult> results)
    {
        if (zone.Polygon.Count < 3)
        {
            return;
        }

        var insideNow = keyed
            .Where(x => AnalyticsGeometry.PointInPolygon(zone.Polygon, new NormalizedPoint(x.Detection.X, x.Detection.Y)))
            .ToDictionary(x => x.Key, x => x.Detection);

        foreach (var (key, detection) in insideNow)
        {
            var sinceKey = (zone.Id, key);
            var point = new NormalizedPoint(detection.X, detection.Y);

            if (!_dwellSince.TryGetValue(sinceKey, out var since))
            {
                since = snapshotUtc;
                _dwellSince[sinceKey] = since;
                _dwellFired[sinceKey] = false;
                _lastPos[sinceKey] = point;
            }
            else if (Distance(point, _lastPos[sinceKey]) > StationaryMoveTolerance)
            {
                _dwellSince[sinceKey] = snapshotUtc;
                _dwellFired[sinceKey] = false;
                _lastPos[sinceKey] = point;
            }

            var elapsed = Math.Max(0, (int)Math.Floor((snapshotUtc - _dwellSince[sinceKey]).TotalSeconds));
            if (!_dwellFired[sinceKey] && elapsed >= zone.DwellSeconds)
            {
                _dwellFired[sinceKey] = true;
                results.Add(new AnalyticsResult(
                    zone.Module, AnalyticsModuleCatalog.EventStationary, zone.Id, zone.Name, zone.ChannelId,
                    key, elapsed, $"靜止/遺留（{elapsed}s≥{zone.DwellSeconds}s）"));
            }
        }

        RemoveLeft(_dwellSince, _dwellFired, zone.Id, insideNow.Keys.ToHashSet());
        foreach (var stale in _lastPos.Where(kv => kv.Key.Zone == zone.Id && !insideNow.ContainsKey(kv.Key.Key)).ToList())
        {
            _lastPos.Remove(stale.Key);
        }
    }

    /// <summary>
    /// 車流統計（M58）：有向線段（polygon 前兩點）跨越即計一次，回報累計車流事件（<c>ai_traffic</c>）。
    /// </summary>
    private void EvaluateTraffic(
        AnalyticsZone zone,
        List<(string Key, AnalyticsDetection Detection)> keyed,
        List<AnalyticsResult> results)
    {
        if (zone.Polygon.Count < 2)
        {
            return;
        }

        var a = zone.Polygon[0];
        var b = zone.Polygon[1];
        var present = new HashSet<string>();

        foreach (var (key, detection) in keyed)
        {
            var point = new NormalizedPoint(detection.X, detection.Y);
            var side = AnalyticsGeometry.SignedSide(a, b, point);
            present.Add(key);

            if (_side.TryGetValue((zone.Id, key), out var previous) &&
                previous != 0 && side != 0 && previous != side)
            {
                var direction = previous > 0 ? Storage.AnalyticsDirections.AToB : Storage.AnalyticsDirections.BToA;
                if (zone.Direction == Storage.AnalyticsDirections.Both || zone.Direction == direction)
                {
                    var label = direction == Storage.AnalyticsDirections.AToB ? "A→B" : "B→A";
                    _trafficCount.TryGetValue((zone.Id, direction), out var cumulative);
                    cumulative++;
                    _trafficCount[(zone.Id, direction)] = cumulative;
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

    /// <summary>
    /// 尾隨/逆行（M59，§5.6）：2 點線段管制流向，`Direction`≠both 時跨線方向違反＝逆行；
    /// `DwellSeconds` 窗內另一 key 同方向跨線＝尾隨（以 per-key 跨線狀態近似，不需 §5.7 完整追蹤）。
    /// </summary>
    private void EvaluateTailgating(
        DateTime snapshotUtc,
        AnalyticsZone zone,
        List<(string Key, AnalyticsDetection Detection)> keyed,
        List<AnalyticsResult> results)
    {
        if (zone.Polygon.Count < 2)
        {
            return;
        }

        var a = zone.Polygon[0];
        var b = zone.Polygon[1];
        var present = new HashSet<string>();

        foreach (var (key, detection) in keyed)
        {
            var point = new NormalizedPoint(detection.X, detection.Y);
            var side = AnalyticsGeometry.SignedSide(a, b, point);
            present.Add(key);

            if (_side.TryGetValue((zone.Id, key), out var previous) &&
                previous != 0 && side != 0 && previous != side)
            {
                var direction = previous > 0 ? Storage.AnalyticsDirections.AToB : Storage.AnalyticsDirections.BToA;
                var label = direction == Storage.AnalyticsDirections.AToB ? "A→B" : "B→A";

                if (zone.Direction != Storage.AnalyticsDirections.Both && zone.Direction != direction)
                {
                    results.Add(new AnalyticsResult(
                        zone.Module, AnalyticsModuleCatalog.EventTailgating, zone.Id, zone.Name, zone.ChannelId,
                        key, 1, $"逆行 {label}（限制 {zone.Direction}）"));
                }

                if (zone.DwellSeconds > 0)
                {
                    if (!_followRecent.TryGetValue((zone.Id, direction), out var recent))
                    {
                        recent = new List<(DateTime, string)>();
                        _followRecent[(zone.Id, direction)] = recent;
                    }

                    var windowStart = snapshotUtc.AddSeconds(-zone.DwellSeconds);
                    recent.RemoveAll(x => x.Utc < windowStart);
                    var prior = recent.FirstOrDefault(x => x.Key != key);
                    if (prior != default)
                    {
                        var elapsed = (int)Math.Round((snapshotUtc - prior.Utc).TotalSeconds);
                        results.Add(new AnalyticsResult(
                            zone.Module, AnalyticsModuleCatalog.EventTailgating, zone.Id, zone.Name, zone.ChannelId,
                            key, 1, $"尾隨 {label}（{elapsed}s 窗）"));
                    }

                    recent.Add((snapshotUtc, key));
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

    /// <summary>
    /// 熱區圖（M58）：在多邊形內之偵測按 5×5 格子累計（無事件輸出），供 <see cref="HeatmapCells"/> 呈現熱區分佈。
    /// </summary>
    private void EvaluateHeatmap(AnalyticsZone zone, List<(string Key, AnalyticsDetection Detection)> keyed)
    {
        if (zone.Polygon.Count < 3)
        {
            return;
        }

        foreach (var (_, detection) in keyed)
        {
            var point = new NormalizedPoint(detection.X, detection.Y);
            if (!AnalyticsGeometry.PointInPolygon(zone.Polygon, point))
            {
                continue;
            }

            var col = Math.Clamp((int)Math.Floor(point.X * HeatmapGridSize), 0, HeatmapGridSize - 1);
            var row = Math.Clamp((int)Math.Floor(point.Y * HeatmapGridSize), 0, HeatmapGridSize - 1);
            var cell = (row * HeatmapGridSize) + col;
            _heat.TryGetValue((zone.Id, cell), out var count);
            _heat[(zone.Id, cell)] = count + 1;
        }
    }

    private static double Distance(NormalizedPoint a, NormalizedPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static void RemoveLeft(
        Dictionary<(int Zone, string Key), DateTime> since,
        Dictionary<(int Zone, string Key), bool> fired,
        int zoneId,
        HashSet<string> present)
    {
        foreach (var stale in since.Keys.Where(k => k.Zone == zoneId && !present.Contains(k.Key)).ToList())
        {
            since.Remove(stale);
            fired.Remove(stale);
        }
    }
}
