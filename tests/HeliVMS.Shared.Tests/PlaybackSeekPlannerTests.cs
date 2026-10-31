using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 回放方向鍵跳轉的落點（←／→，Ctrl 一分鐘、Shift 十分鐘）。
///
/// 守的是「目標落在兩段錄影之間的空檔時要跳到哪裡」：白天錄影中斷很常見，舊寫法會從第一個
/// 空隙一路掉到當日最後一段，於是按一下右鍵就從早上跳到晚上，真正想看的下一段被靜默跳過。
/// 這種錯在畫面上看起來完全正常（有影像、有時間），只能靠測試釘住。
/// </summary>
public sealed class PlaybackSeekPlannerTests
{
    private static readonly DateTime T0 = new(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc);

    private static SegmentRecord Seg(long id, double startMin, double? durSec, double? endMin = null) => new()
    {
        Id = id,
        ChannelId = 1,
        Stream = "main",
        StartUtc = T0.AddMinutes(startMin),
        EndUtc = endMin is { } em ? T0.AddMinutes(em) : null,
        DurationSec = durSec,
        FilePath = $"seg{id}.mp4",
    };

    [Fact]
    public void 目標落在段內時從該點起播()
    {
        var segs = new[] { Seg(1, 0, 60), Seg(2, 10, 60) };

        var landing = PlaybackSeekPlanner.Resolve(segs, T0.AddMinutes(10).AddSeconds(20), forward: true);

        Assert.NotNull(landing);
        Assert.Equal(2, landing!.Value.Segment.Id);
        Assert.Equal(20, landing.Value.OffsetSeconds, 3);
    }

    [Fact]
    public void 往後遇空隙會進下一段而不是跳到當日最後一段()
    {
        // 08:00–08:01 錄影，08:05 才再有錄影，08:20 還有一段。從 08:00:30 按右鍵 30 秒，
        // 目標 08:01:00… 這裡取目標落在空隙中間（08:03）以確保走的是空隙分支。
        var segs = new[] { Seg(1, 0, 60), Seg(2, 5, 60), Seg(3, 20, 60) };

        var landing = PlaybackSeekPlanner.Resolve(segs, T0.AddMinutes(3), forward: true);

        Assert.NotNull(landing);
        Assert.Equal(2, landing!.Value.Segment.Id);
        Assert.Equal(0, landing.Value.OffsetSeconds, 3);
    }

    [Fact]
    public void 往前遇空隙會回到前一段的尾端而不是跳到第一段開頭()
    {
        var segs = new[] { Seg(1, 0, 60), Seg(2, 5, 60), Seg(3, 20, 60) };

        var landing = PlaybackSeekPlanner.Resolve(segs, T0.AddMinutes(3), forward: false);

        Assert.NotNull(landing);
        Assert.Equal(1, landing!.Value.Segment.Id);
        Assert.Equal(60, landing.Value.OffsetSeconds, 2);
    }

    [Fact]
    public void 往後越過最後一段會停在尾端且不會繞回開頭()
    {
        var segs = new[] { Seg(1, 0, 60), Seg(2, 5, 60) };

        var landing = PlaybackSeekPlanner.Resolve(segs, T0.AddMinutes(30), forward: true);

        Assert.NotNull(landing);
        Assert.Equal(2, landing!.Value.Segment.Id);

        // ffmpeg -ss 指到段長本身不會產生影格：停在最後一格之前。
        Assert.True(landing.Value.OffsetSeconds < 60);
        Assert.True(landing.Value.OffsetSeconds > 59);
    }

    [Fact]
    public void 往前越過第一段會停在開頭()
    {
        var segs = new[] { Seg(1, 10, 60) };

        var landing = PlaybackSeekPlanner.Resolve(segs, T0.AddMinutes(-30), forward: false);

        Assert.NotNull(landing);
        Assert.Equal(1, landing!.Value.Segment.Id);
        Assert.Equal(0, landing.Value.OffsetSeconds, 3);
    }

    [Fact]
    public void 段長未知的段也參與跳轉而不被當成不存在()
    {
        // 索引還沒回填長度（剛切出來的段、從磁碟補登的舊資料）：若用 0 秒比對，
        // 這一段等於不存在，往後跳會直接掠過它——畫面上卻有它的色塊。
        var segs = new[] { Seg(1, 0, 60), Seg(2, 5, null, endMin: null) };

        var landing = PlaybackSeekPlanner.Resolve(segs, T0.AddMinutes(5).AddSeconds(3), forward: true);

        Assert.NotNull(landing);
        Assert.Equal(2, landing!.Value.Segment.Id);
        Assert.Equal(3, landing.Value.OffsetSeconds, 3);
    }

    [Fact]
    public void 落在兩段接縫時屬於後一段()
    {
        // 08:00–08:01 與 08:01–08:02 首尾相接：接縫那一瞬間必須有落點，否則同步與跳轉會卡住。
        var segs = new[] { Seg(1, 0, 60), Seg(2, 1, 60) };

        var landing = PlaybackSeekPlanner.Resolve(segs, T0.AddMinutes(1), forward: true);

        Assert.NotNull(landing);
        Assert.Equal(2, landing!.Value.Segment.Id);
        Assert.Equal(0, landing.Value.OffsetSeconds, 3);
    }

    [Fact]
    public void 沒有錄影段時回空()
    {
        Assert.Null(PlaybackSeekPlanner.Resolve([], T0, forward: true));
        Assert.Null(PlaybackSeekPlanner.Resolve([], T0, forward: false));
    }
}
