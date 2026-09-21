namespace HeliVMS.Storage;

/// <summary>聚合軌跡之運動方向（M90，§14.7 #13）；Other 保留給多元軸/異常。</summary>
public enum TrajectoryDirection
{
    Stationary,
    LeftToRight,
    RightToLeft,
    TopToBottom,
    BottomToTop,
    Other,
}

/// <summary>消費邊緣 AI 相機 metadata 事件（M90）：ONVIF Profile M 語意簡化——
/// Behavior 為 Appear／Move／Present／Disappear；X/Y 為 bounding box 左上角、W/H 為寬高。</summary>
public sealed record EdgeMetadataEvent(
    string DeviceId,
    DateTime TimestampUtc,
    string ClassName,
    string TrackId,
    string Behavior,
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>已完成之物件軌跡（M90）：起終點為早期/晚期 1/3 樣本之中位數框位。</summary>
public sealed record EdgeObjectTrack(
    string DeviceId,
    string ClassName,
    string TrackId,
    DateTime FirstUtc,
    DateTime LastUtc,
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    TrajectoryDirection Direction);

/// <summary>
/// 軌跡方向分類（M90，純 BCL）：以物件自身箱款當「畫面尺度」——
/// 位移閾值＝箱款平均×(W+H)/2×StationaryRatio；早期 1/3 中位數 vs 晚期 1/3 中位數
/// 的淨位移判向，抵抗零星雜訊；樣本數不足 MinSamples 回 Stationary。
/// </summary>
public sealed class EdgeDirectionClassifier
{
    /// <summary>淨位移小於箱款尺度×此比例視為靜止（預設 2%）。</summary>
    public const double StationaryRatio = 0.02;

    public const int MinSamples = 3;

    public TrajectoryDirection Classify(IReadOnlyList<EdgeMetadataEvent> samples)
    {
        if (samples.Count < MinSamples)
        {
            return TrajectoryDirection.Stationary;
        }

        var ordered = samples.OrderBy(s => s.TimestampUtc).ToList();
        var third = Math.Max(1, ordered.Count / 3);

        var (firstX, firstY, _, _) = BoxMedians(ordered.Take(third).ToList());
        var (lastX, lastY, _, _) = BoxMedians(ordered.Skip(ordered.Count - third).ToList());

        var norm = ordered.Average(s => (s.Width + s.Height) / 2.0);
        var threshold = norm * StationaryRatio;

        var dx = lastX - firstX;
        var dy = lastY - firstY;
        if (Math.Sqrt((dx * dx) + (dy * dy)) < threshold)
        {
            return TrajectoryDirection.Stationary;
        }

        if (Math.Abs(dx) > Math.Abs(dy))
        {
            return dx > 0 ? TrajectoryDirection.LeftToRight : TrajectoryDirection.RightToLeft;
        }

        return dy > 0 ? TrajectoryDirection.TopToBottom : TrajectoryDirection.BottomToTop;
    }

    private static (double X, double Y, double W, double H) BoxMedians(IReadOnlyList<EdgeMetadataEvent> list)
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