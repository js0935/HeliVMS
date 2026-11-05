using HeliVMS.Recording;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 多路同步回放排程核心（ARCHITECTURE §8.5／§11.4）。
///
/// 這組測試守的是「同一播放頭換算到每一路」的語意：各路的錄影切點不同，
/// 同一個絕對時刻對某一路是段內、對另一路可能是空隙。若把空隙誤判成有錄影
/// （硬跳到別的段），操作員會看到兩台畫面在播不同時刻卻以為同步——是證據比對時最危險的錯。
/// </summary>
/// <summary>
/// 多路同步的容差與啟動延遲補償。現場症狀：四格「各播各的」——舊容差 1.5 秒讓每一格都
/// 可以落後一個半秒以上才被拉回，而啟動慢的那格又永遠追不上（每輪都落後同樣的啟動延遲）。
/// </summary>
public sealed class PlaybackSyncTimingTests
{
    /// <summary>
    /// 大格數總覽牆（只解關鍵影格）不能沿用 0.4 秒的容差判定。
    /// </summary>
    /// <remarks>
    /// 關鍵影格之間是一個 GOP（現場實測 2–20 秒），所以那一格顯示的時刻本來就會落後母鐘
    /// 最多一個 GOP。用 0.4 秒判定「該重新對時」的話，64 條解碼行程每一輪（150ms）都會被
    /// 重開一次：畫面永遠起不來，CPU 全花在啟動 ffmpeg 上，而日誌乾淨。
    /// </remarks>
    [Fact]
    public void 總覽牆的容差比一個GOP還大()
    {
        Assert.True(PlaybackSyncPlanner.OverviewDriftToleranceSeconds >= 20);

        // 落後 5 秒的關鍵影格格：總覽容差下不重開，一般容差下才重開。
        const double drift = 5;
        Assert.False(PlaybackSyncPlanner.ShouldReseek(
            drift, waitingForFirstFrame: false, secondsSinceStart: 30,
            PlaybackSyncPlanner.OverviewDriftToleranceSeconds));
        Assert.True(PlaybackSyncPlanner.ShouldReseek(
            drift, waitingForFirstFrame: false, secondsSinceStart: 30,
            PlaybackSyncPlanner.DefaultDriftToleranceSeconds));
    }

    [Fact]
    public void 對時跳轉會補上這一格的啟動延遲()
    {
        // 要求 10:00:07，這一格啟動要 0.6 秒 → 跳到 07.6，第一張影格出現時剛好是 07.6 的內容。
        Assert.Equal(7.6, PlaybackSyncPlanner.CompensatedOffset(7.0, 0.6), 3);
    }

    [Fact]
    public void 補償不會把跳轉位置往回推()
    {
        Assert.Equal(0.8, PlaybackSyncPlanner.CompensatedOffset(0.2, 0.6), 3);
        Assert.Equal(0, PlaybackSyncPlanner.CompensatedOffset(0, 0), 3);
        // 負的估計值（不該出現）視為 0，而不是把位置往回推。
        Assert.Equal(5, PlaybackSyncPlanner.CompensatedOffset(5, -3), 3);
    }

    [Fact]
    public void 啟動延遲估計是移動平均且夾在上限內()
    {
        // 前一次 1.0、這次量到 3.0 → 1.0*0.5 + 3.0*0.5 = 2.0（單次抖動不至於讓下一跳過衝）。
        Assert.Equal(2.0, PlaybackSyncPlanner.NextLatencyEstimate(1.0, 3.0), 3);
        // 荒謬值被夾在上限：一路把上限加進跳轉位置會直接跳過要看的畫面。
        Assert.Equal(
            PlaybackSyncPlanner.MaxStartupLatencySeconds,
            PlaybackSyncPlanner.NextLatencyEstimate(PlaybackSyncPlanner.MaxStartupLatencySeconds, 600),
            3);
    }

    [Fact]
    public void 等待第一張影格期間不比對漂移只有卡住才重開()
    {
        // 啟動期間「目前時刻」是刻意超前補償的跳轉位置，與母鐘比一定超出容差；
        // 照著判定就會每一輪重開 ffmpeg，畫面永遠起不來。
        Assert.False(PlaybackSyncPlanner.ShouldReseek(driftSeconds: 0.8, waitingForFirstFrame: true, secondsSinceStart: 0.5));
        // 卡住（超過啟動延遲上限）仍要救：重開一次。
        Assert.True(PlaybackSyncPlanner.ShouldReseek(
            driftSeconds: 0.8,
            waitingForFirstFrame: true,
            secondsSinceStart: PlaybackSyncPlanner.MaxStartupLatencySeconds + 0.1));
    }

    [Fact]
    public void 出畫之後才用容差判定()
    {
        Assert.False(PlaybackSyncPlanner.ShouldReseek(0.3, waitingForFirstFrame: false, secondsSinceStart: 10));
        Assert.True(PlaybackSyncPlanner.ShouldReseek(0.6, waitingForFirstFrame: false, secondsSinceStart: 10));
        // 正負都是漂移：落後與超前都要拉回。
        Assert.True(PlaybackSyncPlanner.ShouldReseek(-0.6, waitingForFirstFrame: false, secondsSinceStart: 10));
    }

    [Fact]
    public void 容差要小到肉眼看得出來但不會不停重開()
    {
        // 10–30fps 下 4–12 張影格；再大就是「各播各的」，再小則解碼啟動抖動會不斷重開 ffmpeg。
        Assert.InRange(PlaybackSyncPlanner.DefaultDriftToleranceSeconds, 0.2, 0.5);
    }
}

public sealed class PlaybackSyncPlannerTests
{
    private static readonly DateTime T0 = new(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);

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

    /// <summary>
    /// 段長未知（索引還沒回填，或從磁碟補登的舊資料）時，同步仍要把它當成有錄影。
    /// </summary>
    /// <remarks>
    /// 這裡原本對「EndUtc 與 DurationSec 都沒有」的段回零長度，於是時間軸色帶畫得出那一段、
    /// 多路同步卻判定為空隙：次畫面停住並標示無錄影，看起來像同步壞掉。估算值現在只有一份
    /// （<see cref="PlaybackSegmentTiming.FallbackDurationSeconds"/>），與色帶、事件比對一致。
    /// </remarks>
    [Fact]
    public void 段長未知的段在同步時仍視為有錄影()
    {
        var seg = Seg(1, 0, null, endMin: null);

        var cursor = PlaybackSyncPlanner.Resolve([seg], T0.AddSeconds(3));
        Assert.NotNull(cursor);
        Assert.Equal(3, cursor!.Value.OffsetSeconds, 3);

        // 估算值必須與時間軸色塊相同（10 秒），否則游標會走出色帶或提早停在色帶中間。
        Assert.Equal(T0.AddSeconds(PlaybackSegmentTiming.FallbackDurationSeconds), PlaybackSyncPlanner.EndOf(seg));
        Assert.Equal(
            PlaybackSegmentTiming.FallbackDurationSeconds,
            PlaybackSyncPlanner.LengthOf(seg),
            3);
    }
}
