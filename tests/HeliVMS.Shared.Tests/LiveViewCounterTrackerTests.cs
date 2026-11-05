using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 診斷日誌的每秒率狀態。這裡守的是一個實際發生過的缺陷：把「清掉不再顯示的頻道」
/// 放在取樣之前，於是每一輪都像第一次取樣，日誌永遠是 <c>read=0/s</c>——
/// 那個 0 看起來只像「沒有影格」，而計數器其實一直在動。
/// </summary>
public class LiveViewCounterTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);

    private static LiveFrameRates.Counters Counters(long read, long emitted, long dropped)
        => new(read, emitted, dropped);

    [Fact]
    public void 第二次取樣才算得出率()
    {
        var tracker = new LiveViewCounterTracker();

        var first = tracker.Record(103, Counters(1000, 500, 500), T0);
        Assert.Equal(0, first.Read);

        var second = tracker.Record(103, Counters(1050, 520, 530), T0.AddSeconds(5));
        Assert.Equal(10, second.Read, 3);
        Assert.Equal(4, second.Emitted, 3);
        Assert.Equal(6, second.Dropped, 3);
    }

    [Fact]
    public void 每個頻道各自記住自己的上一筆()
    {
        var tracker = new LiveViewCounterTracker();
        tracker.Record(103, Counters(0, 0, 0), T0);
        tracker.Record(104, Counters(500, 500, 0), T0);

        var first = tracker.Record(103, Counters(100, 100, 0), T0.AddSeconds(2));
        var second = tracker.Record(104, Counters(520, 510, 10), T0.AddSeconds(2));

        Assert.Equal(50, first.Read, 3);
        Assert.Equal(10, second.Read, 3);
        Assert.Equal(5, second.Dropped, 3);
    }

    [Fact]
    public void 修剪之後回來的頻道重新開始()
    {
        var tracker = new LiveViewCounterTracker();
        tracker.Record(103, Counters(1000, 500, 500), T0);
        tracker.KeepOnly([104]);

        // 103 被修剪過：下一筆沒有可比的基準，回 0 而不是拿舊的相減（那會跨越整個空窗）。
        var again = tracker.Record(103, Counters(9000, 4000, 5000), T0.AddMinutes(5));
        Assert.Equal(0, again.Read);
    }

    [Fact]
    public void 停太久之後不再相減()
    {
        var tracker = new LiveViewCounterTracker();
        tracker.Record(103, Counters(0, 0, 0), T0);

        // 隔了兩分鐘（例如操作員切走再切回來）：平均率沒有意義，回 0 並重新立基準。
        var rates = tracker.Record(103, Counters(6000, 3000, 3000), T0.AddMinutes(2));

        Assert.Equal(0, rates.Read);
    }

    [Fact]
    public void 重連讓計數器歸零時不會出現負的率()
    {
        var tracker = new LiveViewCounterTracker();
        tracker.Record(103, Counters(9000, 4000, 5000), T0);

        var rates = tracker.Record(103, Counters(3, 2, 1), T0.AddSeconds(5));

        Assert.Equal(0, rates.Read);
        Assert.Equal(0, rates.Dropped);
    }
}
