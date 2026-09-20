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
/// 模組化分析情境評估器（M52，§14.7 #6；§5.6）：逐幀以幾何規則判定周界跨線／區域侵入／人群聚集。
/// 有狀態（跨幀追蹤），呼叫 <see cref="Reset"/> 可清空。
/// </summary>
public sealed class AnalyticsZoneEvaluator
{
    /// <summary>人群聚集需連續達標幀數（去抖動）。</summary>
    public const int CrowdDebounceFrames = 3;

    private readonly Dictionary<int, HashSet<string>> _inside = new();
    private readonly Dictionary<(int Zone, string Key), int> _side = new();
    private readonly Dictionary<int, int> _crowdStreak = new();

    public void Reset()
    {
        _inside.Clear();
        _side.Clear();
        _crowdStreak.Clear();
    }

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
}
