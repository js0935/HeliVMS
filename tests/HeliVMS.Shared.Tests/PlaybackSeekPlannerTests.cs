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
    public void 跳轉目標正好落在段尾時會前進到下一段()
    {
        // 08:00:50 按右鍵 10 秒，目標剛好是 08:01:00（第一段結束、下一段 08:05 才開始）。
        // 舊寫法把它算成「在第一段之內的第 60 秒」：ffmpeg -ss 60 不產生影格，於是直接回報
        // 播放完成，狀態列跳成「已播放結束」——後面明明還有錄影，按第二次才前進。
        var segs = new[] { Seg(1, 0, 60), Seg(2, 5, 60) };

        var landing = PlaybackSeekPlanner.Resolve(segs, T0.AddSeconds(60), forward: true);

        Assert.NotNull(landing);
        Assert.Equal(2, landing!.Value.Segment.Id);
        Assert.Equal(0, landing.Value.OffsetSeconds, 3);
    }

    [Fact]
    public void 目標正好落在最後一段段尾時停在最後一格()
    {
        // 當日已經沒有下一段：要停在最後一格，而不是落在段長本身（那會沒有影格、
        // 回報播放完成，時間軸游標卻停在尾端）。與「越過最後一段」同一口徑。
        var segs = new[] { Seg(1, 0, 60) };

        var landing = PlaybackSeekPlanner.Resolve(segs, T0.AddSeconds(60), forward: true);

        Assert.NotNull(landing);
        Assert.Equal(1, landing!.Value.Segment.Id);
        Assert.True(landing.Value.OffsetSeconds < 60);
        Assert.True(landing.Value.OffsetSeconds > 59);
    }

    /// <summary>
    /// 時間軸上「點這一刻」的落點（與方向鍵共用 ResolveAt）：接縫歸後一段、段尾停在最後一格、
    /// 空檔不硬跳。三條都是「點下去之後畫面有沒有動」的差別，在桌面上沒有執行期測試可驗。
    /// </summary>
    [Fact]
    public void 時間軸點在接縫上會播後一段()
    {
        var segs = new[] { Seg(1, 0, 60), Seg(2, 1, 60) };

        var landing = PlaybackSeekPlanner.ResolveAt(segs, T0.AddMinutes(1));

        Assert.NotNull(landing);
        Assert.Equal(2, landing!.Value.Segment.Id);
        Assert.Equal(0, landing.Value.OffsetSeconds, 3);
    }

    [Fact]
    public void 時間軸點在段尾那一刻不算在段內()
    {
        // 段尾那一瞬間沒有影格：舊的閉區間會回「該段第 60 秒」，開出來的工作立刻回報
        // 已播放結束（畫面不動），而右邊一格就是有影像的。寧可回報此刻無錄影。
        var segs = new[] { Seg(1, 0, 60) };

        Assert.Null(PlaybackSeekPlanner.ResolveAt(segs, T0.AddSeconds(60)));

        var inside = PlaybackSeekPlanner.ResolveAt(segs, T0.AddSeconds(59));
        Assert.NotNull(inside);
        Assert.Equal(59, inside!.Value.OffsetSeconds, 3);
    }

    [Fact]
    public void 時間軸點在空檔上沒有落點()
    {
        var segs = new[] { Seg(1, 0, 60), Seg(2, 5, 60) };

        Assert.Null(PlaybackSeekPlanner.ResolveAt(segs, T0.AddMinutes(3)));
        Assert.Null(PlaybackSeekPlanner.ResolveAt([], T0));
    }

    [Fact]
    public void 沒有錄影段時回空()
    {
        Assert.Null(PlaybackSeekPlanner.Resolve([], T0, forward: true));
        Assert.Null(PlaybackSeekPlanner.Resolve([], T0, forward: false));
    }
}
