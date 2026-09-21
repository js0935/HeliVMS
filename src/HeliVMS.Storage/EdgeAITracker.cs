namespace HeliVMS.Storage;

/// <summary>
/// Edge AI metadata 軌跡聚合器（M90，§14.7 #13，時鐘注入、in-memory 滑動窗）：
/// 依 (DeviceId, ClassName, TrackId) 累積樣本，遇 Disappear 或跨軌間隔逾
/// trackTimeout 即收尾為 EdgeObjectTrack；逾 maxTracks 時逐「最近活躍最舊」軌；
/// 具備時間戳亂序容忍（收尾時排序）。
/// </summary>
public sealed class EdgeAITracker
{
    public const int DefaultMaxTracks = 256;
    public const double DefaultTrackTimeoutSeconds = 5;

    private readonly TimeSpan _trackTimeout;
    private readonly int _maxTracks;
    private readonly EdgeDirectionClassifier _classifier = new();
    private readonly Dictionary<(string DeviceId, string ClassName, string TrackId), List<EdgeMetadataEvent>> _tracks = new();

    public EdgeAITracker(TimeSpan? trackTimeout = null, int maxTracks = DefaultMaxTracks)
    {
        _trackTimeout = trackTimeout ?? TimeSpan.FromSeconds(DefaultTrackTimeoutSeconds);
        _maxTracks = maxTracks;

        if (_trackTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(trackTimeout), "軌 timeout 須為正。");
        }

        if (_maxTracks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTracks), "軌數上限須為正。");
        }
    }

    /// <summary>推送一筆 metadata 事件；回傳此次推送所收尾的軌跡（可能為空）。</summary>
    public IReadOnlyList<EdgeObjectTrack> Push(EdgeMetadataEvent e, DateTime utcNow)
    {
        var completed = new List<EdgeObjectTrack>();
        var key = (e.DeviceId, e.ClassName, e.TrackId);

        if (!_tracks.TryGetValue(key, out var samples))
        {
            samples = new List<EdgeMetadataEvent>();
            _tracks[key] = samples;
        }
        else if (utcNow - samples[^1].TimestampUtc > _trackTimeout)
        {
            completed.Add(Finalize(samples));
            samples.Clear();
        }

        samples.Add(e);

        if (string.Equals(e.Behavior, "Disappear", StringComparison.OrdinalIgnoreCase))
        {
            completed.Add(Finalize(samples));
            _tracks.Remove(key);
        }

        EvictIfOverflow();
        return completed;
    }

    /// <summary>收尾並清空所有仍在途軌跡（eg 取流結束），回傳全部。</summary>
    public IReadOnlyList<EdgeObjectTrack> Flush()
    {
        var tracks = _tracks.Values.Select(Finalize).ToList();
        _tracks.Clear();
        return tracks;
    }

    private void EvictIfOverflow()
    {
        while (_tracks.Count > _maxTracks)
        {
            var oldest = _tracks.OrderBy(kv => kv.Value[^1].TimestampUtc).First().Key;
            _tracks.Remove(oldest);
        }
    }

    private EdgeObjectTrack Finalize(List<EdgeMetadataEvent> samples)
    {
        var ordered = samples
            .OrderBy(s => s.TimestampUtc)
            .ThenBy(s => s.Behavior)
            .ToList();
        var direction = _classifier.Classify(ordered);
        var (startX, startY, _, _) = BoxFirstMedian(ordered);
        var (endX, endY, _, _) = BoxFirstMedian(ordered.Count >= 2
            ? ordered.Skip(Math.Max(0, ordered.Count - Math.Max(1, ordered.Count / 3))).ToList()
            : ordered);
        var first = ordered[0];
        var last = ordered[^1];

        return new EdgeObjectTrack(
            first.DeviceId,
            first.ClassName,
            first.TrackId,
            first.TimestampUtc,
            last.TimestampUtc,
            startX,
            startY,
            endX,
            endY,
            direction);
    }

    private static (double X, double Y, double W, double H) BoxFirstMedian(IReadOnlyList<EdgeMetadataEvent> list)
        => (Median(list.Select(s => s.X)),
            Median(list.Select(s => s.Y)),
            Median(list.Select(s => s.Width)),
            Median(list.Select(s => s.Height)));

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return (sorted.Count & 1) == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}