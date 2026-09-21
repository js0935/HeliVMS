using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

public class PlaybackTimelineTests
{
    private static readonly DateTime Day = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    private static SegmentRecord Seg(long id, DateTime start, DateTime? end, double? durationSec = null, DateTime? day = null)
    {
        var s = day ?? Day;
        return new SegmentRecord
        {
            Id = id,
            ChannelId = 1,
            Stream = "main",
            StartUtc = start,
            EndUtc = end,
            DurationSec = durationSec,
            FilePath = $"seg-{id}.mp4",
            Status = SegmentStatus.Final,
        };
    }

    private static AlarmEventRecord Evt(long id, DateTime utc, string kind = "motion")
        => new() { Id = id, ChannelId = 1, EventType = kind, StartUtc = utc };

    private static double Hours(int h, int m = 0) => h * 60.0 + m;

    private static PlaybackTimeline Build(IReadOnlyList<SegmentRecord> segs, IReadOnlyList<AlarmEventRecord> evts)
        => PlaybackTimelineBuilder.Build(segs, evts, Day);

    [Fact]
    public void Build_EmptyInputs_NoBarsNoMarkersGapWholeDay()
    {
        var t = Build(Array.Empty<SegmentRecord>(), Array.Empty<AlarmEventRecord>());

        Assert.Empty(t.Bars);
        Assert.Empty(t.Markers);
        Assert.Equal(1.0, t.GapFraction, precision: 6);
        Assert.Equal(1, t.GapCount);
    }

    [Fact]
    public void Build_SingleFullDaySegment_TakesWholeDay()
    {
        var t = Build(new[] { Seg(1, Day, Day.AddHours(24)) }, Array.Empty<AlarmEventRecord>());

        var bar = Assert.Single(t.Bars);
        Assert.Equal(1, bar.SegmentId);
        Assert.Equal(0.0, bar.LeftFraction, precision: 6);
        Assert.Equal(1.0, bar.WidthFraction, precision: 6);
        Assert.Equal(0.0, t.GapFraction, precision: 6);
        Assert.Equal(0, t.GapCount);
    }

    [Fact]
    public void Build_MidDaySegment_FractionsAndTwoGaps()
    {
        var start = Day.AddHours(9);
        var t = Build(new[] { Seg(7, start, start.AddMinutes(60)) }, Array.Empty<AlarmEventRecord>());

        var bar = Assert.Single(t.Bars);
        Assert.Equal(Hours(9) / (24 * 60.0), bar.LeftFraction, precision: 6);
        Assert.Equal(1.0 / 24, bar.WidthFraction, precision: 6);
        Assert.Equal(23.0 / 24, t.GapFraction, precision: 6);
        Assert.Equal(2, t.GapCount);
    }

    [Fact]
    public void Build_SegmentCrossesMidnight_ClipsToDayEnd()
    {
        var start = Day.AddHours(23);
        var t = Build(new[] { Seg(2, start, start.AddHours(3)) }, Array.Empty<AlarmEventRecord>());

        var bar = Assert.Single(t.Bars);
        Assert.Equal(23.0 / 24, bar.LeftFraction, precision: 6);
        Assert.Equal(1.0 / 24, bar.WidthFraction, precision: 6);
    }

    [Fact]
    public void Build_SegmentBeforeDay_IsSkipped()
    {
        var t = Build(new[] { Seg(3, Day.AddDays(-1), Day.AddHours(-1)) }, Array.Empty<AlarmEventRecord>());

        Assert.Empty(t.Bars);
    }

    [Fact]
    public void Build_SegmentStartsBeforeDay_ClipsToDayStart()
    {
        var start = Day.AddMinutes(-60);
        var t = Build(new[] { Seg(4, start, Day.AddHours(12)) }, Array.Empty<AlarmEventRecord>());

        var bar = Assert.Single(t.Bars);
        Assert.Equal(0.0, bar.LeftFraction, precision: 6);
        Assert.Equal(0.5, bar.WidthFraction, precision: 6);
    }

    [Fact]
    public void Build_AdjacentSegments_NoGap()
    {
        var segs = new[]
        {
            Seg(5, Day, Day.AddHours(6)),
            Seg(6, Day.AddHours(6), Day.AddHours(24)),
        };

        var t = Build(segs, Array.Empty<AlarmEventRecord>());

        Assert.Equal(2, t.Bars.Count);
        Assert.Equal(0, t.GapCount);
        Assert.Equal(0.0, t.GapFraction, precision: 6);
    }

    [Fact]
    public void Build_OverlappingClippedSegments_MergeForGaps()
    {
        var segs = new[]
        {
            Seg(8, Day.AddHours(23), Day.AddHours(26)),
            Seg(9, Day.AddHours(23.5), Day.AddHours(27)),
        };

        var t = Build(segs, Array.Empty<AlarmEventRecord>());

        Assert.Equal(2, t.Bars.Count);
        Assert.Equal(1, t.GapCount);
        Assert.Equal(23.0 / 24, t.GapFraction, precision: 6);
    }

    [Fact]
    public void Build_MarkerWithinDay_ComputesFraction()
    {
        var utc = Day.AddHours(6).AddMinutes(30);
        var t = Build(Array.Empty<SegmentRecord>(), new[] { Evt(11, utc) });

        var marker = Assert.Single(t.Markers);
        Assert.Equal(11, marker.EventId);
        Assert.Equal(6.5 / 24, marker.XFraction, precision: 6);
        Assert.Equal("motion", marker.Kind);
    }

    [Fact]
    public void Build_MarkerAtDayStart_Included()
    {
        var t = Build(Array.Empty<SegmentRecord>(), new[] { Evt(12, Day) });

        Assert.Equal(0.0, Assert.Single(t.Markers).XFraction, precision: 6);
    }

    [Fact]
    public void Build_MarkerAtDayEnd_Excluded()
    {
        var t = Build(Array.Empty<SegmentRecord>(), new[] { Evt(13, Day.AddHours(24)) });

        Assert.Empty(t.Markers);
    }

    [Fact]
    public void Build_MarkersOutsideDay_AreFiltered()
    {
        var t = Build(Array.Empty<SegmentRecord>(), new[]
        {
            Evt(14, Day.AddDays(-1)),
            Evt(15, Day.AddHours(3)),
            Evt(16, Day.AddHours(48)),
        });

        var marker = Assert.Single(t.Markers);
        Assert.Equal(15, marker.EventId);
    }

    [Fact]
    public void Build_NullEnd_FallsBackToDuration()
    {
        var start = Day.AddHours(10);
        var t = Build(new[] { Seg(20, start, null, durationSec: 60) }, Array.Empty<AlarmEventRecord>());

        var bar = Assert.Single(t.Bars);
        Assert.Equal(60.0 / (24 * 60 * 60.0), bar.WidthFraction, precision: 6);
    }

    [Fact]
    public void Build_NullEndNullDuration_FallsBackToTenSeconds()
    {
        var start = Day.AddHours(10);
        var t = Build(new[] { Seg(21, start, null, durationSec: null) }, Array.Empty<AlarmEventRecord>());

        var bar = Assert.Single(t.Bars);
        Assert.Equal(10.0 / (24 * 60 * 60.0), bar.WidthFraction, precision: 6);
    }

    [Fact]
    public void Build_UnsortedInputs_GetsSortedByTime()
    {
        var segs = new[]
        {
            Seg(30, Day.AddHours(6), Day.AddHours(7)),
            Seg(31, Day.AddHours(1), Day.AddHours(2)),
            Seg(32, Day.AddHours(10), Day.AddHours(11)),
        };
        var evts = new[]
        {
            Evt(40, Day.AddHours(8)),
            Evt(41, Day.AddHours(2)),
        };

        var t = Build(segs, evts);

        Assert.Equal(new long[] { 31, 30, 32 }, t.Bars.Select(b => b.SegmentId));
        Assert.Equal(new long[] { 41, 40 }, t.Markers.Select(m => m.EventId));
    }

    [Fact]
    public void ToFraction_ClampsWithinDay()
    {
        Assert.Equal(0.0, PlaybackTimelineBuilder.ToFraction(Day.AddDays(-1), Day), precision: 6);
        Assert.Equal(1.0, PlaybackTimelineBuilder.ToFraction(Day.AddHours(48), Day), precision: 6);
        Assert.Equal(0.25, PlaybackTimelineBuilder.ToFraction(Day.AddHours(6), Day), precision: 6);
    }
}