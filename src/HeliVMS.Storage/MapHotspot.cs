namespace HeliVMS.Storage;

/// <summary>事件熱點單一取樣（M81，§16 電子地圖強化）：一地圖圖釘鏈定的頻道在時間窗內的一筆事件。</summary>
public sealed record MapEventSample(int ChannelId, string Kind, DateTimeOffset Utc);

/// <summary>依「事件熱點」聚合結果（一個啟用圖釘＝一個熱點；熱度需達門檻）。</summary>
public sealed record MapHotspot(
    int DeviceId,
    int ChannelId,
    double X,
    double Y,
    int Count,
    IReadOnlyList<string> DistinctKinds,
    DateTimeOffset LastUtc,
    bool Hot,
    int Urgency);

/// <summary>
/// 電子地圖事件熱點聚合（M81，§16 電子地圖強化②，純 BCL）：把「近窗事件」依啟用圖釘
/// （<see cref="MapDeviceRecord"/>）聚合為熱點清單，供 M82 地圖視窗渲染脈動與 tooltip。
/// 規則（均已測試）：窗含兩端〔`from ≤ utc ≤ now`〕；未來事件與未鉚定頻道忽略；僅啟用圖釘；
/// 同頻道 camera/io 兩種圖釘並存時以 camera 優先；熱門＝Count ≥ minHotThreshold；Urgency＝0（不熱）或 min(100, Count×20＋60 秒內 +15)；排序 Count
/// 降序→LastUtc 降序→DeviceId 升序（平手穩定）。
/// </summary>
public static class MapEventAggregator
{
    /// <summary>預設熱門門檻（近窗事件數）。</summary>
    public const int DefaultMinHotThreshold = 3;

    /// <summary>預設時間窗長度。</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan RecencyBonusWindow = TimeSpan.FromSeconds(60);
    private const int RecencyBonus = 15;
    private const int PointsPerEvent = 20;

    /// <summary>聚合計算（純函式，無 DB／UI 依賴）。</summary>
    public static IReadOnlyList<MapHotspot> Compute(
        IReadOnlyList<MapDeviceRecord> pins,
        IReadOnlyList<MapEventSample> samples,
        DateTimeOffset now,
        TimeSpan? window = null,
        int minHotThreshold = DefaultMinHotThreshold)
    {
        if (minHotThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minHotThreshold), "熱門門檻需 ≥ 1。");
        }

        var cutoff = now.Subtract(window ?? DefaultWindow);

        var byChannel = new System.Collections.Generic.Dictionary<int, MapDeviceRecord>();
        foreach (var pin in pins)
        {
            if (!pin.Enabled)
            {
                continue;
            }

            var hasCamera = string.Equals(pin.DeviceType, "camera", StringComparison.OrdinalIgnoreCase);
            if (byChannel.TryGetValue(pin.ChannelId, out var existing))
            {
                var existingIsCamera = string.Equals(existing.DeviceType, "camera", StringComparison.OrdinalIgnoreCase);
                if (existingIsCamera || !hasCamera)
                {
                    continue;
                }
            }

            byChannel[pin.ChannelId] = pin;
        }

        var counts = new System.Collections.Generic.Dictionary<int, int>();
        var lastUtc = new System.Collections.Generic.Dictionary<int, DateTimeOffset>();
        var kinds = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.HashSet<string>>();

        foreach (var sample in samples)
        {
            if (sample.Utc < cutoff || sample.Utc > now)
            {
                continue;
            }

            if (!byChannel.TryGetValue(sample.ChannelId, out _))
            {
                continue;
            }

            counts[sample.ChannelId] = counts.TryGetValue(sample.ChannelId, out var c) ? c + 1 : 1;
            if (!lastUtc.TryGetValue(sample.ChannelId, out var last) || sample.Utc > last)
            {
                lastUtc[sample.ChannelId] = sample.Utc;
            }

            if (!kinds.TryGetValue(sample.ChannelId, out var set))
            {
                set = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                kinds[sample.ChannelId] = set;
            }

            set.Add(sample.Kind);
        }

        var result = new List<MapHotspot>();
        foreach (var (channelId, pin) in byChannel)
        {
            var count = counts.GetValueOrDefault(channelId);
            var last = lastUtc.GetValueOrDefault(channelId);
            var hot = count >= minHotThreshold;
            var urgency = 0;
            if (hot)
            {
                var recency = now.Subtract(last) <= RecencyBonusWindow ? RecencyBonus : 0;
                urgency = Math.Min(100, count * PointsPerEvent + recency);
            }

            var distinct = new List<string>(kinds.TryGetValue(channelId, out var set) ? set : new System.Collections.Generic.HashSet<string>());
            distinct.Sort(StringComparer.Ordinal);

            result.Add(new MapHotspot(
                pin.Id,
                pin.ChannelId,
                pin.X,
                pin.Y,
                count,
                distinct,
                last,
                hot,
                urgency));
        }

        result.Sort((a, b) =>
        {
            var byCount = b.Count.CompareTo(a.Count);
            if (byCount != 0)
            {
                return byCount;
            }

            var byLast = b.LastUtc.CompareTo(a.LastUtc);
            if (byLast != 0)
            {
                return byLast;
            }

            return a.DeviceId.CompareTo(b.DeviceId);
        });

        return result;
    }
}