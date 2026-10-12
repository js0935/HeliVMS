using HeliVMS.Recording;
using HeliVMS.Shared.Models;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 由實際錄影檔推估碼率（<see cref="ObservedBitrateEstimator"/>）的行為契約。
///
/// 背景：<see cref="BitrateGovernor"/> 需要「現況碼率」才有輸入，但全系統沒有任何地方
/// 回報實際碼率，它的測試都是餵假數字。沒有這個推估，頻寬治理在現場是無米之炊。
///
/// 現場症狀：磁碟比預期快滿。沒有這個數字，唯一能回答「哪一台吃最多頻寬」
/// 的方式是人工去翻每台的區段檔大小——16 台的 NVR 就是 16 趟來回。
/// </summary>
public sealed class ObservedBitrateEstimatorTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SegmentRecord Seg(
        int channelId,
        int sizeBytes,
        double seconds,
        DateTime? startUtc = null,
        long id = 1)
    {
        var start = startUtc ?? T0;
        return new SegmentRecord
        {
            Id = id,
            ChannelId = channelId,
            StartUtc = start,
            EndUtc = start.AddSeconds(seconds),
            SizeBytes = sizeBytes,
            DurationSec = seconds,
        };
    }

    [Fact]
    public void 單一區段能換算成每秒位元組乘以八()
    {
        // 1 MiB／10 秒 = 838860.8 bps
        var bps = ObservedBitrateEstimator.ForChannel(1, new[] { Seg(1, 1_048_576, 10) }).ObservedBps;

        Assert.Equal((long)(1_048_576 * 8 / 10), bps);
    }

    [Fact]
    public void 多段以總量計算而不是逐段平均()
    {
        // 一段 100KB/1 秒（快取滿載）+ 一段 100KB/99 秒。
        // 逐段平均會得到 2 × (800kbps) = 800kbps，嚴重高估；
        // 實際佔用是 200KB/100 秒 = 16kbps。治理器會因此給錯配額。
        var bps = ObservedBitrateEstimator.ForChannel(1, new[]
        {
            Seg(1, 102_400, 1, T0, id: 1),
            Seg(1, 102_400, 99, T0.AddSeconds(1), id: 2),
        }).ObservedBps;

        Assert.Equal((long)(204_800 * 8 / 100), bps);
    }

    [Fact]
    public void 長短不一的區段仍然反映真實總量()
    {
        var segments = new[]
        {
            Seg(1, 3_000_000, 15, T0, id: 1),
            Seg(1, 1_000_000, 5, T0.AddSeconds(15), id: 2),
        };

        var bps = ObservedBitrateEstimator.ForChannel(1, segments).ObservedBps;

        // 4 MB / 20 秒
        Assert.Equal((long)(4_000_000 * 8 / 20), bps);
    }

    [Fact]
    public void 沒有完成時間的區段不參與推估()
    {
        // 正在寫或被刪到一半的區段，長度會被低估而讓碼率被高估；
        // 高估的碼率讓治理器算出偏低的配額，正好在最該省頻寬時給太少。
        var inProgress = new SegmentRecord
        {
            Id = 9,
            ChannelId = 1,
            StartUtc = T0,
            EndUtc = null,
            SizeBytes = 500_000_000,
        };

        var bps = ObservedBitrateEstimator.ForChannel(1, new[] { inProgress, Seg(1, 1_000_000, 10) }).ObservedBps;

        Assert.Equal((long)(1_000_000 * 8 / 10), bps);
    }

    [Fact]
    public void 單一區段不被視為可信()
    {
        // 單一區段可能正好橫跨 I/O 忙碌或移動警報，看單段會誤判好幾倍。
        var result = ObservedBitrateEstimator.ForChannel(1, new[] { Seg(1, 1_000_000, 10) });

        Assert.False(result.IsReliable);
    }

    [Fact]
    public void 兩段以上就被視為可信()
        => Assert.True(
            ObservedBitrateEstimator
                .ForChannel(1, new[] { Seg(1, 1, 10, T0, 1), Seg(1, 1, 10, T0.AddSeconds(10), 2) })
                .IsReliable);

    [Fact]
    public void 沒有可用樣本時回報零而不是猜測()
    {
        var result = ObservedBitrateEstimator.ForChannel(1, Array.Empty<SegmentRecord>());

        Assert.Equal(0, result.ObservedBps);
        Assert.False(result.IsReliable);
        Assert.Null(result.WindowStartUtc);
    }

    [Fact]
    public void 不同頻道分別估算()
    {
        var segments = new[]
        {
            Seg(1, 1_000_000, 10, T0, 1),
            Seg(2, 4_000_000, 10, T0, 2),
        };

        var all = ObservedBitrateEstimator.ForChannels(segments);

        Assert.Equal(2, all.Count);
        Assert.Equal((long)(1_000_000 * 8 / 10), all[1].ObservedBps);
        Assert.Equal((long)(4_000_000 * 8 / 10), all[2].ObservedBps);
    }

    [Fact]
    public void 治理時所有配額加總等於預算()
    {
        // 這是 BitrateGovernor 的核心承諾：ΣTarget == Budget。
        // 用實際檔案當輸入跑一次，確認接上真實資料後這個承諾仍成立。
        var segments = new[]
        {
            Seg(1, 4_000_000, 10, T0, 1),
            Seg(1, 4_000_000, 10, T0.AddSeconds(10), 2),
            Seg(2, 1_000_000, 10, T0, 3),
            Seg(2, 1_000_000, 10, T0.AddSeconds(10), 4),
        };

        var result = ObservedBitrateEstimator.Govern(
            budgetBps: 8_000_000,
            segments,
            priority: _ => 3);

        Assert.Equal(8_000_000, result.AllocatedBps);
        Assert.Equal(8_000_000, result.Targets.Sum(t => t.TargetBps));
    }

    [Fact]
    public void 高優先權頻道分到較多配額()
    {
        var segments = new[]
        {
            Seg(1, 2_000_000, 10, T0, 1),
            Seg(2, 2_000_000, 10, T0, 2),
        };

        var result = ObservedBitrateEstimator.Govern(
            budgetBps: 9_000_000,
            segments,
            priority: ch => ch == 1 ? 5 : 1);

        var high = result.Targets.Single(t => t.ChannelId == 1).TargetBps;
        var low = result.Targets.Single(t => t.ChannelId == 2).TargetBps;

        Assert.True(high > low, $"優先權 5 的頻道應分到更多：{high} vs {low}");
    }

    [Fact]
    public void 事件中的頻道即使優先權低也保住配額()
    {
        // 警報當下把頻寬從閒置攝影機抽走給事件攝影機，是這條規則存在的理由：
        // 保全來電那一秒的畫質比背景巡看重要。
        var segments = new[]
        {
            Seg(1, 2_000_000, 10, T0, 1),
            Seg(2, 2_000_000, 10, T0, 2),
        };

        var result = ObservedBitrateEstimator.Govern(
            budgetBps: 6_000_000,
            segments,
            priority: _ => 1,
            inEvent: ch => ch == 2);

        var inAlarm = result.Targets.Single(t => t.ChannelId == 2).TargetBps;
        var idle = result.Targets.Single(t => t.ChannelId == 1).TargetBps;

        Assert.True(inAlarm > idle, $"事件中的頻道應拿到更多配額：{inAlarm} vs {idle}");
    }
}
