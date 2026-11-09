using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 監看牆輪巡（QC_Live AutoScan 對齊）的落點規劃。
/// 行為依反編譯事實：每個週期把畫面往前推一個分割量，尾頁不足一個分割量時夾在
/// <c>count - cells</c>；分割量蓋滿全部頻道時不動。
/// 守門原因：輪巡算錯的症狀是「畫面一直跳、或有幾台永遠輪不到」，現場只說得出
/// 「輪巡怪怪的」；純函式把這件事釘死，改壞任何一個邊界都會紅。
/// </summary>
public sealed class WallAutoScanPlannerTests
{
    [Fact]
    public void 輪巡每次推進一個分割量並循環繞回()
    {
        // 10 台、4 分割：0→4→(4+4=8 越尾)→夾在 6→(6+4)%10=0。循環 0,4,6 覆蓋全部 10 台
        // （尾頁 6-9 與前頁 4-7 重疊 6、7 兩台——QC_Live 的算術就是如此，照實對齊）。
        Assert.Equal(4, WallAutoScanPlanner.NextOffset(10, 4, 0));
        Assert.Equal(6, WallAutoScanPlanner.NextOffset(10, 4, 4));
        Assert.Equal(0, WallAutoScanPlanner.NextOffset(10, 4, 6));
    }

    [Fact]
    public void 推進即越尾時直接落在最後一頁()
    {
        // 10 台、6 分割：從頭推進 (0+6)=6，但 6+6=12 已經越過尾頁 → 夾在 10-6=4。
        // 之後最後一頁 (4+6)%10=0 繞回第一頁，輪巡就在「前頁／尾頁」之間來回且不漏台。
        Assert.Equal(4, WallAutoScanPlanner.NextOffset(10, 6, 0));
        Assert.Equal(0, WallAutoScanPlanner.NextOffset(10, 6, 4));
        Assert.Equal(4, WallAutoScanPlanner.NextOffset(10, 6, 8));
    }

    [Fact]
    public void 位移已落在尾段時下一頁夾住而不是繞回()
    {
        // 3 → (3+4)%10=7 → 7+4=11 越尾 → 夾在 6。QC_Live 的 clamp 分支就在這裡，
        // 少了它輪巡會在一頁只畫出 1～4 台然後突然繞回頭。
        Assert.Equal(6, WallAutoScanPlanner.NextOffset(10, 4, 3));
        Assert.Equal(6, WallAutoScanPlanner.NextOffset(10, 4, 5));
    }

    [Fact]
    public void 分割量蓋滿全部頻道時輪巡不動()
    {
        // 畫面已經是全部頻道，翻頁沒有意義；QC_Live 的條件是 cells < count 才推進。
        Assert.Equal(3, WallAutoScanPlanner.NextOffset(9, 9, 3));
        Assert.Equal(3, WallAutoScanPlanner.NextOffset(9, 12, 3));
    }

    [Fact]
    public void 分割量或頻道數不合法時原地不動()
    {
        Assert.Equal(2, WallAutoScanPlanner.NextOffset(0, 4, 2));
        Assert.Equal(2, WallAutoScanPlanner.NextOffset(9, 0, 2));
        Assert.Equal(2, WallAutoScanPlanner.NextOffset(9, -1, 2));
    }

    [Fact]
    public void 負位移視為從頭起步()
    {
        Assert.Equal(3, WallAutoScanPlanner.NextOffset(9, 3, -1));
    }

    [Fact]
    public void 週期壞值回預設並夾在上下限()
    {
        Assert.Equal(WallAutoScanPlanner.DefaultPeriodSeconds, WallAutoScanPlanner.NormalizePeriodSeconds(null));
        Assert.Equal(WallAutoScanPlanner.DefaultPeriodSeconds, WallAutoScanPlanner.NormalizePeriodSeconds(0));
        Assert.Equal(WallAutoScanPlanner.DefaultPeriodSeconds, WallAutoScanPlanner.NormalizePeriodSeconds(-5));
        Assert.Equal(WallAutoScanPlanner.MinPeriodSeconds, WallAutoScanPlanner.NormalizePeriodSeconds(1));
        Assert.Equal(WallAutoScanPlanner.MaxPeriodSeconds, WallAutoScanPlanner.NormalizePeriodSeconds(7200));
        Assert.Equal(15, WallAutoScanPlanner.NormalizePeriodSeconds(15));
    }
}
