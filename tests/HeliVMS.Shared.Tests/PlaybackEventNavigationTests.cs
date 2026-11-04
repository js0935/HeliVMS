using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 事件清單的上下一個導覽。現場用 N／P 一段一段跳事件；邊界要能分辨「還沒選取」與
/// 「已經到底」——前者要從頭／從尾開始，後者要讓呼叫端說「沒有更多事件了」。
/// </summary>
public class PlaybackEventNavigationTests
{
    [Fact]
    public void 下一個往上加而上一往下減()
    {
        Assert.Equal(2, PlaybackEventNavigation.NextIndex(1, count: 5, direction: +1));
        Assert.Equal(0, PlaybackEventNavigation.NextIndex(1, count: 5, direction: -1));
    }

    [Fact]
    public void 還沒選取時下一從第一筆上一從最後一筆()
    {
        Assert.Equal(0, PlaybackEventNavigation.NextIndex(-1, count: 5, direction: +1));
        Assert.Equal(4, PlaybackEventNavigation.NextIndex(-1, count: 5, direction: -1));
    }

    [Fact]
    public void 已經在兩端時原地不動讓呼叫端提示沒有更多()
    {
        // 回傳與目前相同＝沒有可移動的目標；呼叫端據此顯示訊息而不是靜默不動。
        Assert.Equal(4, PlaybackEventNavigation.NextIndex(4, count: 5, direction: +1));
        Assert.Equal(0, PlaybackEventNavigation.NextIndex(0, count: 5, direction: -1));
    }

    [Fact]
    public void 空清單不會產生索引()
    {
        Assert.Equal(-1, PlaybackEventNavigation.NextIndex(-1, count: 0, direction: +1));
        Assert.Equal(-1, PlaybackEventNavigation.NextIndex(-1, count: 0, direction: -1));
    }

    [Fact]
    public void 只剩一筆時兩個方向都停在它身上()
    {
        Assert.Equal(0, PlaybackEventNavigation.NextIndex(0, count: 1, direction: +1));
        Assert.Equal(0, PlaybackEventNavigation.NextIndex(0, count: 1, direction: -1));
    }
}
