using HeliVMS.Recording;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 容量規劃（<see cref="RecordingCapacityPlanner"/>）：給還沒裝機的場合算需要多大的碟。
/// </summary>
/// <remarks>
/// <para>
/// 這一支最容易造成的傷害不是算錯，而是<b>看起來很精確地算錯一個數量級</b>——買錯容量比算不出來貴得多。
/// `docs/ARCHITECTURE.md` §11.2 的範例（5 Mbps、32 路、30 天）原本寫「≈518 TB」，
/// 同一個算式其實是 51.84 TB（除以 1,000,000 是把 MB 換成 TB）。那個數字現在由測試釘住。
/// </para>
/// <para>
/// 第二個易錯點是容錯耗損：需求算的是「可用容量」，RAID 的容錯碟不算在可用容量裡。
/// 建議碟數必須含容錯碟，而且「買得不夠」要回報，不能四捨五入成剛好。
/// </para>
/// </remarks>
public class RecordingCapacityPlannerTests
{
    private const long Tb = 1_000_000_000_000L;

    [Fact]
    public void 文件上的範例是五十一點八四TB而不是五百一十八TB()
    {
        // 5 Mbps × 32 路 × 30 天、單流、不含餘裕 → 51.84 TB（文件上原本寫 518 TB，差了一個數量級）。
        var raw = RecordingCapacityPlanner.Plan(new CapacityPlanRequest(32, 5, 30, OverheadFactor: 1.0));

        Assert.NotNull(raw);
        Assert.Equal(51.84, raw!.Value.RequiredTb, precision: 2);

        // 加上預設 5% 餘裕才是要買的容量：54.43 TB；每天寫入 1.728 TB。
        var plan = RecordingCapacityPlanner.Plan(new CapacityPlanRequest(32, 5, 30));

        Assert.NotNull(plan);
        Assert.Equal(54.432, plan!.Value.RequiredTb, precision: 2);
        Assert.Equal(1.728, plan.Value.PerDayTb, precision: 3);
    }

    [Fact]
    public void 主含次都錄時需求是兩倍()
    {
        var single = RecordingCapacityPlanner.Plan(new CapacityPlanRequest(16, 4, 7, StreamsPerChannel: 1));
        var dual = RecordingCapacityPlanner.Plan(new CapacityPlanRequest(16, 4, 7, StreamsPerChannel: 2));

        Assert.NotNull(single);
        Assert.NotNull(dual);
        Assert.Equal(single!.Value.RequiredBytes * 2, dual!.Value.RequiredBytes);
    }

    [Fact]
    public void 餘裕預設百分之五且可調整()
    {
        var plain = RecordingCapacityPlanner.Plan(new CapacityPlanRequest(1, 8, 1, OverheadFactor: 1.0));
        var padded = RecordingCapacityPlanner.Plan(new CapacityPlanRequest(1, 8, 1));

        Assert.NotNull(plain);
        Assert.NotNull(padded);

        // 8 Mbps 一天＝86.4 GB；預設餘裕 5% → 90.72 GB。
        Assert.Equal(86_400_000_000, plain!.Value.ContentBytes);
        Assert.Equal(86_400_000_000, plain.Value.RequiredBytes);
        Assert.Equal(90_720_000_000, padded!.Value.RequiredBytes);
        Assert.Equal(RecordingCapacityPlanner.DefaultOverheadFactor, padded.Value.RequiredBytes / (double)plain.Value.RequiredBytes, precision: 6);
    }

    [Theory]
    [InlineData(0, 4, 30)]
    [InlineData(1, 0, 30)]
    [InlineData(1, 4, 0)]
    [InlineData(1, -1, 30)]
    public void 輸入不合理時回未知而不是編一個數字(int channels, double mbps, int days)
    {
        Assert.Null(RecordingCapacityPlanner.Plan(new CapacityPlanRequest(channels, mbps, days)));
    }

    [Fact]
    public void 鏡像的容錯碟數與資料碟相同()
    {
        // 需求 10 TB、單碟 4 TB → 3 顆資料碟；鏡像共 6 顆。
        var needed = RecordingCapacityPlanner.DisksRequired(10 * Tb, 4 * Tb, RaidLevel.Mirror);

        Assert.NotNull(needed);
        Assert.Equal(3, needed!.Value.DataDisks);
        Assert.Equal(6, needed.Value.TotalDisks);
    }

    [Fact]
    public void RAID5多一顆RAID6多兩顆且各有最少資料碟數()
    {
        var raid5 = RecordingCapacityPlanner.DisksRequired(10 * Tb, 4 * Tb, RaidLevel.Raid5);
        var raid6 = RecordingCapacityPlanner.DisksRequired(10 * Tb, 4 * Tb, RaidLevel.Raid6);

        Assert.Equal(3, raid5!.Value.DataDisks);
        Assert.Equal(4, raid5.Value.TotalDisks);
        Assert.Equal(3, raid6!.Value.DataDisks);
        Assert.Equal(5, raid6.Value.TotalDisks);

        // 需求量小到只要 1 顆碟時，RAID5 仍然要 2 顆資料碟（1 顆資料碟的「容錯」等於鏡像，不是 RAID5）。
        var small5 = RecordingCapacityPlanner.DisksRequired(1 * Tb, 4 * Tb, RaidLevel.Raid5);
        var small6 = RecordingCapacityPlanner.DisksRequired(1 * Tb, 4 * Tb, RaidLevel.Raid6);
        Assert.Equal(2, small5!.Value.DataDisks);
        Assert.Equal(3, small5.Value.TotalDisks);
        Assert.Equal(3, small6!.Value.DataDisks);
        Assert.Equal(5, small6.Value.TotalDisks);
    }

    [Fact]
    public void 已經有碟時算得出可用的夠不夠()
    {
        // 需求 10 TB、單碟 4 TB：RAID5 要 4 顆。手上 4 顆 → 3 顆資料碟＝12 TB，夠。
        var enough = RecordingCapacityPlanner.Evaluate(10 * Tb, 4 * Tb, availableDisks: 4, RaidLevel.Raid5);
        Assert.NotNull(enough);
        Assert.Equal(4, enough!.Value.TotalDisks);
        Assert.Equal(12 * Tb, enough.Value.UsableBytes);
        Assert.False(enough.Value.Shortfall);

        // 同樣 4 顆但走 RAID6：只剩 2 顆資料碟＝8 TB，不足——這正是最常被忘掉的容損耗。
        var shortByParity = RecordingCapacityPlanner.Evaluate(10 * Tb, 4 * Tb, availableDisks: 4, RaidLevel.Raid6);
        Assert.NotNull(shortByParity);
        Assert.Equal(8 * Tb, shortByParity!.Value.UsableBytes);
        Assert.True(shortByParity.Value.Shortfall, "買得不夠要說出來，不能只回一組看起來合理的碟數");

        // 鏡像：4 顆只有 2 顆可用。
        var mirror = RecordingCapacityPlanner.Evaluate(10 * Tb, 4 * Tb, availableDisks: 4, RaidLevel.Mirror);
        Assert.Equal(8 * Tb, mirror!.Value.UsableBytes);
        Assert.True(mirror.Value.Shortfall);

        // 不做容錯：4 顆全用得上（但也沒有容錯）。
        var plain = RecordingCapacityPlanner.Evaluate(10 * Tb, 4 * Tb, availableDisks: 4, RaidLevel.None);
        Assert.Equal(16 * Tb, plain!.Value.UsableBytes);
        Assert.False(plain.Value.Shortfall);
    }

    [Fact]
    public void 碟數或需求無效時回未知()
    {
        Assert.Null(RecordingCapacityPlanner.DisksRequired(0, 4 * Tb, RaidLevel.Raid5));
        Assert.Null(RecordingCapacityPlanner.DisksRequired(1 * Tb, 0, RaidLevel.Raid5));
        Assert.Null(RecordingCapacityPlanner.Evaluate(1 * Tb, 4 * Tb, availableDisks: 0, RaidLevel.Raid5));
    }
}
