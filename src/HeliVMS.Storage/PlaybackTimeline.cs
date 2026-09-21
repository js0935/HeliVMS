using HeliVMS.Shared.Models;

namespace HeliVMS.Storage;

/// <summary>時間軸上一段錄影欄（M83，回放強化）：`LeftFraction/WidthFraction` 皆為 0..1 對全日窗。</summary>
public sealed record TimelineBar(long SegmentId, double LeftFraction, double WidthFraction);

/// <summary>時間軸上一個事件標記（M83）：`XFraction` 為事件時刻對全日窗的 0..1 位置。</summary>
public sealed record TimelineMarker(long EventId, int ChannelId, string Kind, DateTime Utc, double XFraction);

/// <summary>
/// 回放時間軸模型（M83）：當日窗固定為 `[DayUtc, DayUtc+24h)`；Bars/Markers 依時間排序；
/// `GapFraction`＝全日窗未被任何錄影欄涵蓋的比例（0..1），`GapCount`＝未涵蓋區間數（空＝1）。
/// </summary>
public sealed record PlaybackTimeline(
    IReadOnlyList<TimelineBar> Bars,
    IReadOnlyList<TimelineMarker> Markers,
    DateTime DayUtc,
    double GapFraction,
    int GapCount)
{
    /// <summary>全日窗長度（秒）。</summary>
    public const double DaySeconds = 24 * 60 * 60;
}

/// <summary>
/// 回放時間軸建構（M83，純 BCL）：把當日錄影片段與事件規格化為 0..1 比例（與地圖圖釘同理念——
/// 縮放/換尺寸不跑位），供回放帶（band）渲染與點擊跳轉使用。
/// 規則（已測試）：窗含左端不含右端〔dayStart ≤ t &lt; dayEnd〕；區段裁切至窗內、零長度略過；
/// `EndUtc` 為 null 時以 `StartUtc + DurationSec（預設 10s）` 推算；Bar/Marker 依時間排序；
/// 重疊區段以聯集計算間隙。
/// </summary>
public static class PlaybackTimelineBuilder
{
    public static PlaybackTimeline Build(
        IReadOnlyList<SegmentRecord> segments,
        IReadOnlyList<AlarmEventRecord> events,
        DateTime dayStartUtc)
    {
        var dayEndUtc = dayStartUtc.AddSeconds(PlaybackTimeline.DaySeconds);

        var bars = new List<TimelineBar>();
        var intervals = new List<(DateTime Start, DateTime End)>();
        foreach (var seg in segments)
        {
            var segEnd = seg.EndUtc ?? seg.StartUtc.AddSeconds(seg.DurationSec ?? 10);
            var start = seg.StartUtc > dayStartUtc ? seg.StartUtc : dayStartUtc;
            var end = segEnd < dayEndUtc ? segEnd : dayEndUtc;
            if (end <= start)
            {
                continue;
            }

            bars.Add(new TimelineBar(
                seg.Id,
                start.Subtract(dayStartUtc).TotalSeconds / PlaybackTimeline.DaySeconds,
                end.Subtract(start).TotalSeconds / PlaybackTimeline.DaySeconds));
            intervals.Add((start, end));
        }

        bars.Sort((a, b) => a.LeftFraction.CompareTo(b.LeftFraction));

        var markers = new List<TimelineMarker>();
        foreach (var ev in events)
        {
            if (ev.StartUtc < dayStartUtc || ev.StartUtc >= dayEndUtc)
            {
                continue;
            }

            markers.Add(new TimelineMarker(
                ev.Id,
                ev.ChannelId,
                ev.EventType,
                ev.StartUtc,
                ev.StartUtc.Subtract(dayStartUtc).TotalSeconds / PlaybackTimeline.DaySeconds));
        }

        markers.Sort((a, b) => a.XFraction.CompareTo(b.XFraction));

        var (gapFraction, gapCount) = ComputeGaps(intervals, dayStartUtc, dayEndUtc);
        return new PlaybackTimeline(bars, markers, dayStartUtc, gapFraction, gapCount);
    }

    /// <summary>將啟用時間換算為對全日窗的 0..1 比例（夾在窗內）。</summary>
    public static double ToFraction(DateTime utc, DateTime dayStartUtc)
    {
        var f = utc.Subtract(dayStartUtc).TotalSeconds / PlaybackTimeline.DaySeconds;
        return Math.Clamp(f, 0, 1);
    }

    private static (double Fraction, int Count) ComputeGaps(
        List<(DateTime Start, DateTime End)> intervals,
        DateTime dayStartUtc,
        DateTime dayEndUtc)
    {
        if (intervals.Count == 0)
        {
            return (1.0, 1);
        }

        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(DateTime Start, DateTime End)> { intervals[0] };
        foreach (var next in intervals)
        {
            var last = merged[^1];
            if (next.Start.CompareTo(last.End) <= 0)
            {
                merged[^1] = (last.Start, next.End > last.End ? next.End : last.End);
            }
            else
            {
                merged.Add(next);
            }
        }

        double uncoveredSeconds = 0;
        var cursor = dayStartUtc;
        foreach (var (start, end) in merged)
        {
            if (start > cursor)
            {
                uncoveredSeconds += start.Subtract(cursor).TotalSeconds;
            }

            if (end > cursor)
            {
                cursor = end;
            }
        }

        if (dayEndUtc > cursor)
        {
            uncoveredSeconds += dayEndUtc.Subtract(cursor).TotalSeconds;
        }

        // 間隙區間數：窗頭未涵蓋、窗尾未涵蓋、中間每段未涵蓋各計一區。
        var count = merged.Count + 1;
        if (merged[0].Start <= dayStartUtc)
        {
            count--;
        }

        if (merged[^1].End >= dayEndUtc)
        {
            count--;
        }

        return (Math.Clamp(uncoveredSeconds / PlaybackTimeline.DaySeconds, 0, 1), count);
    }
}