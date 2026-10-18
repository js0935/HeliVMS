using HeliVMS.Recording;
using HeliVMS.Shared.Models;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 多路同步回放排程核心（ARCHITECTURE §8.5／§11.4）。
///
/// 這組測試守的是「同一播放頭換算到每一路」的語意：各路的錄影切點不同，
/// 同一個絕對時刻對某一路是段內、對另一路可能是空隙。若把空隙誤判成有錄影
/// （硬跳到別的段），操作員會看到兩台畫面在播不同時刻卻以為同步——是證據比對時最危險的錯。
/// </summary>
public sealed class PlaybackSyncPlannerTests
{
    private static readonly DateTime T0 = new(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);

    private static SegmentRecord Seg(long id, double startMin, double durSec, double? endMin = null) => new()
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
    public void 落在區段內時回傳該段與段內偏移()
    {
        var segs = new[] { Seg(1, 0, 60), Seg(2, 10, 60) };

        var cursor = PlaybackSyncPlanner.Resolve(segs, T0.AddMinutes(10).AddSeconds(30));

        Assert.NotNull(cursor);
        Assert.Equal(2, cursor!.Value.Segment.Id);
        Assert.Equal(30, cursor.Value.OffsetSeconds, 3);
    }

    [Fact]
    public void 落在兩段之間的空隙回傳無錄影()
    {
        // 10:00–10:01 與 10:05–10:06 之間空了 4 分鐘。
        var segs = new[] { Seg(1, 0, 60), Seg(2, 5, 60) };

        var cursor = PlaybackSyncPlanner.Resolve(segs, T0.AddMinutes(3));

        Assert.Null(cursor);
    }

    [Fact]
    public void 落在整天範圍之外回傳無錄影()
    {
        var segs = new[] { Seg(1, 0, 60) };

        Assert.Null(PlaybackSyncPlanner.Resolve(segs, T0.AddSeconds(-1)));
        Assert.Null(PlaybackSyncPlanner.Resolve(segs, T0.AddSeconds(61)));
    }

    [Fact]
    public void 端點採閉區間_起點偏移為零且終點仍屬該段()
    {
        var seg = Seg(1, 0, 60);
        var segs = new[] { seg };

        var atStart = PlaybackSyncPlanner.Resolve(segs, T0);
        Assert.NotNull(atStart);
        Assert.Equal(0, atStart!.Value.OffsetSeconds, 3);

        // 端點若採開區間，兩段相接處會出現「剛好卡在接縫」的一格空窗，同步時忽有忽無。
        var atEnd = PlaybackSyncPlanner.Resolve(segs, T0.AddSeconds(60));
        Assert.NotNull(atEnd);
        Assert.Equal(1, atEnd!.Value.Segment.Id);
    }

    [Fact]
    public void 未排序的區段也能命中正確的那一段()
    {
        var segs = new[] { Seg(3, 20, 60), Seg(1, 0, 60), Seg(2, 10, 60) };

        var cursor = PlaybackSyncPlanner.Resolve(segs, T0.AddMinutes(20).AddSeconds(5));

        Assert.NotNull(cursor);
        Assert.Equal(3, cursor!.Value.Segment.Id);
    }

    [Fact]
    public void 結束時間為空時以段落長度推算()
    {
        // EndUtc 為 null（錄影中或舊資料）；60 秒長度之外就該視為無錄影。
        var segs = new[] { Seg(1, 0, 60, endMin: null) };

        Assert.NotNull(PlaybackSyncPlanner.Resolve(segs, T0.AddSeconds(59)));
        Assert.Null(PlaybackSyncPlanner.Resolve(segs, T0.AddSeconds(61)));
    }

    [Fact]
    public void 空隙時下一段取起點不早於目標者_沒有下一段回空()
    {
        var segs = new[] { Seg(1, 0, 60), Seg(2, 5, 60) };

        var next = PlaybackSyncPlanner.NextAtOrAfter(segs, T0.AddMinutes(3));
        Assert.NotNull(next);
        Assert.Equal(2, next!.Id);

        Assert.Null(PlaybackSyncPlanner.NextAtOrAfter(segs, T0.AddMinutes(7)));
    }

    [Fact]
    public void 範圍取最早起點與最晚終點_空集合回空()
    {
        var segs = new[] { Seg(1, 5, 60), Seg(2, 0, 30) };

        var bounds = PlaybackSyncPlanner.Bounds(segs);
        Assert.NotNull(bounds);
        Assert.Equal(T0, bounds!.Value.StartUtc);
        Assert.Equal(T0.AddMinutes(6), bounds.Value.EndUtc);

        Assert.Null(PlaybackSyncPlanner.Bounds([]));
    }

    [Fact]
    public void 群組範圍由所有路聯集決定()
    {
        var ch1 = new[] { Seg(1, 0, 60) };
        var ch2 = new[] { Seg(2, 30, 60) };

        var bounds = PlaybackSyncPlanner.GroupBounds([ch1, ch2]);
        Assert.NotNull(bounds);
        Assert.Equal(T0, bounds!.Value.StartUtc);
        Assert.Equal(T0.AddMinutes(31), bounds.Value.EndUtc);

        Assert.Null(PlaybackSyncPlanner.GroupBounds([[], []]));
    }

    [Fact]
    public void 壁面頻道以焦點排第一格並依序遞補繞回()
    {
        var channels = new[] { 10, 20, 30, 40 };

        Assert.Equal(new[] { 30, 40, 10, 20 }, PlaybackSyncPlanner.WallChannelIds(channels, 30, 4));
        Assert.Equal(new[] { 30, 40 }, PlaybackSyncPlanner.WallChannelIds(channels, 30, 2));
        Assert.Equal(new[] { 40, 10 }, PlaybackSyncPlanner.WallChannelIds(channels, 40, 2));
    }

    [Fact]
    public void 壁面格數多於頻道時只給現有頻道且焦點不存在時用第一台()
    {
        var channels = new[] { 10, 20 };

        // 焦點頻道永遠排第一格，即使格數多到用不完也一樣。
        Assert.Equal(new[] { 20, 10 }, PlaybackSyncPlanner.WallChannelIds(channels, 20, 4));
        Assert.Equal(new[] { 10, 20 }, PlaybackSyncPlanner.WallChannelIds(channels, 99, 4));
        Assert.Empty(PlaybackSyncPlanner.WallChannelIds([], 1, 4));
    }
}
