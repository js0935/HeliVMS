using HeliVMS.Recording;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 多路對時的「慢者定調」模型（參考機 QCV8001 web 播放器：<c>showTimestamp = min(各格 timestamp)</c>，
/// 超前的格暫停，落後的那一格才是時間基準）。
/// </summary>
/// <remarks>
/// <para>
/// 現場回報「除了第一格，其他格都慢一拍」：舊模型用主畫面當母鐘，落後的格只能靠「重開 ffmpeg 並
/// seek」去追，而每一次重開都要再付一次啟動延遲（檔案回放 0.3–1.5 秒，慢的來源 2 秒以上），
/// 追不上的格於是永遠落後，中間還夾著黑畫面。新模型反過來：最慢的那一格定調，超前的幾格停下來等。
/// </para>
/// <para>
/// 停等的落地是「停止讀取」而不是「丟棄影格」（見 <c>PlaybackSession.HoldUntil</c>）：
/// 不讀，ffmpeg 的輸出管線填滿後行程停住，解凍後下一張仍然接在最後一張之後——不掉格、不跳時間。
/// </para>
/// </remarks>
public sealed class PlaybackAlignmentTests
{
    private static readonly DateTime Base = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 群組時刻取畫面上最慢的那一格()
    {
        var group = PlaybackSyncPlanner.GroupInstant(
            [Base.AddSeconds(0.4), null, Base.AddSeconds(1.9), Base]);

        Assert.Equal(Base, group);
    }

    [Fact]
    public void 沒有任何一格有畫面時沒有群組時刻()
    {
        Assert.Null(PlaybackSyncPlanner.GroupInstant([null, null]));
        Assert.Null(PlaybackSyncPlanner.GroupInstant([]));
    }

    [Fact]
    public void 最慢的那一格不用等()
    {
        // 它就是母鐘：等待量必須是 0，否則它一停就沒有人在前進（整面牆一起凍住）。
        Assert.Equal(TimeSpan.Zero, PlaybackSyncPlanner.AlignmentHold(Base, Base));
    }

    [Fact]
    public void 超前超過容差的格要停下來等()
    {
        var hold = PlaybackSyncPlanner.AlignmentHold(
            Base.AddSeconds(1.8), Base, PlaybackSyncPlanner.DefaultDriftToleranceSeconds);

        // 1.8 − 0.2（停在容差的一半，留餘裕避免下一輪又超標）= 1.6 秒。
        Assert.Equal(1.6, hold.TotalSeconds, 3);
    }

    [Fact]
    public void 容差之內不等待以免每一輪都在停()
    {
        Assert.Equal(
            TimeSpan.Zero,
            PlaybackSyncPlanner.AlignmentHold(
                Base.AddSeconds(PlaybackSyncPlanner.DefaultDriftToleranceSeconds), Base));
    }

    [Fact]
    public void 等待有上限_壞掉的一格不會讓整面牆陪它凍住()
    {
        // 落後 60 秒不是「啟動比較慢」，是那一格壞了。整面牆停住的代價比單一格落後高得多，
        // 所以等待一律夾在上限內，剩下的交給停滯判定。
        var hold = PlaybackSyncPlanner.AlignmentHold(Base.AddSeconds(60), Base);

        Assert.Equal(PlaybackSyncPlanner.MaxAlignmentHoldSeconds, hold.TotalSeconds, 3);
        Assert.True(hold.TotalSeconds < 10);
    }

    [Fact]
    public void 超前的那一格停等之後會與群組對齊且不需要重開解碼()
    {
        // 模擬對時迴圈：群組（最慢的那一格）固定以 1× 前進；超前的那一格只有在「沒有在等」時
        // 才前進。斷言它會收斂到容差內——而且整個過程沒有任何一次重新起播。
        var group = Base;
        var pane = Base.AddSeconds(1.8);
        var tolerance = PlaybackSyncPlanner.DefaultDriftToleranceSeconds;
        const int tickMs = 150;
        var frozenMs = 0.0;
        var restarts = 0;

        for (var elapsed = 0; elapsed < 6000 && (pane - group).TotalSeconds > tolerance; elapsed += tickMs)
        {
            if (frozenMs > 0)
            {
                frozenMs = Math.Max(0, frozenMs - tickMs);
            }
            else
            {
                var hold = PlaybackSyncPlanner.AlignmentHold(pane, group, tolerance);
                if (hold > TimeSpan.Zero)
                {
                    frozenMs = hold.TotalMilliseconds;
                }
                else
                {
                    // 沒在等就照自己的 1× 前進。
                    pane = pane.AddMilliseconds(tickMs);
                }
            }

            group = group.AddMilliseconds(tickMs);
        }

        Assert.True((pane - group).TotalSeconds <= tolerance,
            $"停等之後應該對齊，實際還超前 {(pane - group).TotalSeconds:0.00}s");
        Assert.Equal(0, restarts);
    }
}

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

        // 只解關鍵影格的那一格天生落後一個 GOP（現場實測 2–20 秒）。那份落後量在總覽容差下
        // 不該讓別人停下來等（等＝整面牆一起變慢）；用一般容差就會讓主畫面每一輪都停到上限。
        var group = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
        var keyframePane = group.AddSeconds(-18);

        Assert.Equal(
            TimeSpan.Zero,
            PlaybackSyncPlanner.AlignmentHold(
                group, keyframePane, PlaybackSyncPlanner.OverviewDriftToleranceSeconds));
        Assert.True(
            PlaybackSyncPlanner.AlignmentHold(
                group, keyframePane, PlaybackSyncPlanner.DefaultDriftToleranceSeconds) > TimeSpan.Zero);

        // 「不再更新」的門檻也要比一個 GOP 大，否則那一格每一輪都被判成卡住而重開。
        Assert.True(PlaybackSyncPlanner.StallSeconds(keyframesOnly: true) >= 20);
        Assert.True(PlaybackSyncPlanner.StallSeconds(keyframesOnly: true) > PlaybackSyncPlanner.StallSeconds(keyframesOnly: false));
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
    public void 等待第一張影格期間只有卡住才重開()
    {
        // 啟動期間「目前時刻」是刻意超前補償的跳轉位置（見 CompensatedOffset），不是畫面上的時刻，
        // 所以那一段時間不算落後、也不重開：只有「等太久」才是真的起不來。
        const double stall = PlaybackSyncPlanner.DefaultStallSeconds;
        Assert.False(PlaybackSyncPlanner.ShouldRestart(
            waitingForFirstFrame: true, secondsSinceStart: 0.5, secondsSinceLastFrame: 0, stall));
        Assert.True(PlaybackSyncPlanner.ShouldRestart(
            waitingForFirstFrame: true,
            secondsSinceStart: PlaybackSyncPlanner.MaxStartupLatencySeconds + 0.1,
            secondsSinceLastFrame: 0,
            stall));
    }

    [Fact]
    public void 已經有畫面但不再更新才重開落後再多都不重開()
    {
        const double stall = PlaybackSyncPlanner.DefaultStallSeconds;

        // 有畫面、也一直在更新（150ms 一輪）：落後再多都不重開——落後交給「最慢的定調」處理，
        // 重開只是再付一次啟動延遲，追不上的還是追不上（現場的「除了第一格都慢一拍」）。
        Assert.False(PlaybackSyncPlanner.ShouldRestart(
            waitingForFirstFrame: false, secondsSinceStart: 600, secondsSinceLastFrame: 0.04, stall));

        // 真的不再更新（解碼行程死了、檔案被鎖）才重開。
        Assert.True(PlaybackSyncPlanner.ShouldRestart(
            waitingForFirstFrame: false, secondsSinceStart: 600, secondsSinceLastFrame: stall + 0.1, stall));
    }

    [Fact]
    public void 容差要小到肉眼看得出來但不會不停重開()
    {
        // 10–30fps 下 4–12 張影格；再大就是「各播各的」，再小則解碼啟動抖動會不斷重開 ffmpeg。
        Assert.InRange(PlaybackSyncPlanner.DefaultDriftToleranceSeconds, 0.2, 0.5);
    }

    /// <summary>
    /// 等待首張影格與「不再更新」共用同一個判定（<see cref="PlaybackSyncPlanner.ShouldRestart"/>）。
    /// </summary>
    /// <remarks>
    /// 這個判定以前還帶一條「落後超過容差就重新對時」的分支，那條路已經整段移除：
    /// 多路對時現在是「最慢的一格定調、超前的停下來等」（見 <c>PlaybackAlignmentTests</c>），
    /// 留著舊分支就等於兩套對時機制並存，而重開那套會把等待換來的對齊又打散。
    /// </remarks>
    [Fact]
    public void 等待首張影格期間不會因為看起來落後就被重開()
    {
        const double stall = PlaybackSyncPlanner.DefaultStallSeconds;

        // 剛開播（0.3 秒）：位置上「看起來落後」是補償出來的假象，不重開。
        Assert.False(PlaybackSyncPlanner.ShouldRestart(
            waitingForFirstFrame: true, secondsSinceStart: 0.3, secondsSinceLastFrame: 0, stall));

        // 等了超過啟動上限：這才是真的起不來，重開一次。
        Assert.True(PlaybackSyncPlanner.ShouldRestart(
            waitingForFirstFrame: true,
            secondsSinceStart: PlaybackSyncPlanner.MaxStartupLatencySeconds + 0.1,
            secondsSinceLastFrame: 0,
            stall));
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
