using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 回放時間軸縮放與刻度階梯（§3.4「時間刻度依縮放層級切換」、§8.5「滾輪無級縮放，縮放中心跟隨游標」）。
/// 全部是純算數：WPF 端只負責把算出來的窗與刻度畫上 Canvas，畫錯一定是這裡的公式先錯。
/// </summary>
public class PlaybackTimelineScaleTests
{
    private static readonly DateTime Day = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 滾輪縮放後錨點時刻留在畫面同一個位置()
    {
        var view = PlaybackTimelineScale.FullDay(Day);

        foreach (var notches in new[] { 1.0, 3.0, -1.0, -4.0 })
        {
            var anchorFraction = 0.25;
            var anchorUtc = view.TimeAtFraction(anchorFraction);
            var zoomed = PlaybackTimelineScale.Zoom(view, Day, Day.AddDays(1), anchorFraction, notches);

            // 放大三格後錨點跑掉一個像素，操作員看到的就是「畫面自己滑走」，
            // 視覺上像卡了一下而不是在縮放。
            var drift = (anchorUtc - zoomed.TimeAtFraction(anchorFraction)).Duration();
            Assert.True(drift <= TimeSpan.FromMilliseconds(1), $"錨點漂移 {drift.TotalMilliseconds}ms");
        }
    }

    [Fact]
    public void 放大一格窗寬除以設定倍率()
    {
        var view = PlaybackTimelineScale.FullDay(Day);

        var zoomed = PlaybackTimelineScale.Zoom(view, Day, Day.AddDays(1), 0.5, 1);

        Assert.Equal(
            PlaybackTimeline.DaySeconds / PlaybackTimelineScale.ZoomFactorPerNotch,
            zoomed.Span.TotalSeconds,
            precision: 3);
    }

    [Fact]
    public void 縮放夾在當日窗內且不窄於十秒()
    {
        var dayEnd = Day.AddDays(1);
        var view = PlaybackTimelineScale.FullDay(Day);

        var zoomedOut = PlaybackTimelineScale.Zoom(view, Day, dayEnd, 0, -50);
        Assert.Equal(Day, zoomedOut.StartUtc);
        Assert.Equal(dayEnd, zoomedOut.EndUtc);

        var zoomedIn = PlaybackTimelineScale.Zoom(view, Day, dayEnd, 0.5, 100);
        Assert.Equal(PlaybackTimelineScale.MinSpanSeconds, zoomedIn.Span.TotalSeconds, precision: 3);

        // 貼邊再放大：窗要整窗平移貼回邊界，而不是窗尾伸出當日窗（伸出後書籤、
        // 事件標記會排到沒有資料的位置，點下去全成「無錄影」）。
        var atLeftEdge = PlaybackTimelineScale.Zoom(view, Day, dayEnd, 0, 100);
        Assert.True(atLeftEdge.StartUtc >= Day);
        Assert.True(atLeftEdge.EndUtc <= dayEnd);

        var atRightEdge = PlaybackTimelineScale.Zoom(view, Day, dayEnd, 1, 100);
        Assert.True(atRightEdge.StartUtc >= Day);
        Assert.True(atRightEdge.EndUtc <= dayEnd);

        // 連兩次縮放才測得到夾限：貼左界放大到 10 秒窗後，再以貼窗尾的錨點一口氣縮回全日。
        // 未夾限時窗頭會算到 Day 之前（錨點時刻 − 全日×0.99），帶子從此對不到資料。
        var tiny = PlaybackTimelineScale.Zoom(view, Day, dayEnd, 0, 100);
        var reopenedLeft = PlaybackTimelineScale.Zoom(tiny, Day, dayEnd, 0.99, -100);
        Assert.Equal(Day, reopenedLeft.StartUtc);
        Assert.True(reopenedLeft.EndUtc <= dayEnd);

        var tinyRight = PlaybackTimelineScale.Zoom(view, Day, dayEnd, 1, 100);
        var reopenedRight = PlaybackTimelineScale.Zoom(tinyRight, Day, dayEnd, 0.01, -100);
        Assert.True(reopenedRight.StartUtc >= Day);
        Assert.Equal(dayEnd, reopenedRight.EndUtc);
    }

    [Theory]
    [InlineData(24 * 3600, 2 * 3600)]
    [InlineData(12 * 3600, 3600)]
    [InlineData(4 * 3600, 30 * 60)]
    [InlineData(2 * 3600, 10 * 60)]
    [InlineData(30 * 60, 5 * 60)]
    [InlineData(10 * 60, 60)]
    [InlineData(60, 5)]
    [InlineData(10, 1)]
    public void 刻度階梯依窗寬切換且主刻度數不超過上限(int spanSeconds, int expectedStepSeconds)
    {
        var step = PlaybackTimelineScale.SelectMajorStep(TimeSpan.FromSeconds(spanSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedStepSeconds), step);
        Assert.True(
            spanSeconds / step.TotalSeconds <= PlaybackTimelineScale.MaxMajorTicks,
            $"窗寬 {spanSeconds}s 選出刻度 {step}，主刻度數超過上限");
    }

    [Fact]
    public void 零長度的窗選最小刻度()
    {
        Assert.Equal(
            PlaybackTimelineScale.MajorSteps[0],
            PlaybackTimelineScale.SelectMajorStep(TimeSpan.Zero));
    }

    [Fact]
    public void 刻度對齊當地時間整刻度且都落在窗內()
    {
        // 10 秒窗 → 1 秒刻度：從窗頭的下一個整秒開始，含頭含尾。
        var view = new TimelineViewWindow(
            new DateTime(2026, 10, 1, 10, 0, 7, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 10, 0, 17, DateTimeKind.Utc));

        var ticks = PlaybackTimelineScale.Ticks(view, TimeZoneInfo.Utc);

        Assert.Equal(11, ticks.Count);
        Assert.All(ticks, t => Assert.True(t.Utc >= view.StartUtc && t.Utc <= view.EndUtc));
        Assert.All(ticks, t => Assert.True(t.IsMajor));
        Assert.Equal("10:00:07", ticks[0].Label);
        Assert.Equal("10:00:17", ticks[^1].Label);
    }

    [Fact]
    public void 主刻度有標籤而中點的次要刻度沒有()
    {
        // 2 小時窗 → 10 分鐘主刻度，主刻度中點加 5 分鐘次要刻度。
        var view = new TimelineViewWindow(
            new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));

        var ticks = PlaybackTimelineScale.Ticks(view, TimeZoneInfo.Utc);

        Assert.Equal(25, ticks.Count);
        Assert.All(ticks.Where((_, i) => i % 2 == 0), t => Assert.True(t.IsMajor));
        Assert.All(ticks.Where((_, i) => i % 2 == 1), t =>
        {
            Assert.False(t.IsMajor);
            Assert.Equal(string.Empty, t.Label);
        });

        Assert.Equal("10:00", ticks[0].Label);
        Assert.Equal("12:00", ticks[^1].Label);
    }

    [Fact]
    public void 秒級窗的刻度全都是主刻度()
    {
        // 60 秒窗 → 5 秒刻度；秒級階梯畫次要刻度只會讓刻度列糊成一條線。
        var view = new TimelineViewWindow(Day, Day.AddSeconds(60));

        var ticks = PlaybackTimelineScale.Ticks(view, TimeZoneInfo.Utc);

        Assert.Equal(13, ticks.Count);
        Assert.All(ticks, t => Assert.True(t.IsMajor));
        Assert.All(ticks, t => Assert.Equal(8, t.Label.Length));
    }

    [Fact]
    public void 非整點時區的刻度對齊當地時間而不是UTC()
    {
        // +05:45 這種非整點時區是「UTC 對齊」與「當地對齊」唯一分得出來的地方：
        // 若刻度從 UTC 整刻起算，14:00 的格線會漂到當地 14:15，操作員看牆鐘對不上時間軸。
        var tz = TimeZoneInfo.CreateCustomTimeZone("test-offset-545", TimeSpan.FromMinutes(345), "test", "test");
        var view = new TimelineViewWindow(
            new DateTime(2026, 10, 1, 8, 7, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 10, 7, 0, DateTimeKind.Utc));

        var ticks = PlaybackTimelineScale.Ticks(view, tz);

        // 當地 13:52 起算，下一根 10 分鐘主刻度是當地 14:00 ＝ UTC 08:15。
        Assert.Equal("14:00", ticks[0].Label);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 15, 0, DateTimeKind.Utc), ticks[0].Utc);
    }

    [Fact]
    public void 全日窗與時間軸日窗同口徑()
    {
        var full = PlaybackTimelineScale.FullDay(Day);

        Assert.Equal(Day, full.StartUtc);
        Assert.Equal(PlaybackTimeline.DaySeconds, full.Span.TotalSeconds, precision: 3);
        Assert.Equal(TimeSpan.FromHours(2), PlaybackTimelineScale.SelectMajorStep(full.EndUtc - full.StartUtc));
    }
}
