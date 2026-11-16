using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 「內嵌 Web 管理介面要不要綁到區域網路」的政策。
/// </summary>
/// <remarks>
/// 這個介面沒有 TLS，而且可以看影像、操作 PTZ、匯出錄影、改設定。預設只綁迴圈位址；
/// 開放區域網路的唯一拒絕理由刻意只有一個：<b>還開著「允許無金鑰存取」</b>——那個組合的後果
/// 不是「少一層驗證」，而是任何連得到這個埠的人都能看畫面、轉鏡頭、下載錄影。
/// </remarks>
public class WebBindPolicyTests
{
    [Fact]
    public void 沒有要求時就是只綁本機()
    {
        var decision = WebBindPolicy.Resolve(requested: false, allowNoKey: "1");

        Assert.False(decision.AllowLan);
        // 沒要求就沒有「被拒絕」可言：設定頁不該在沒勾選時顯示一句紅字。
        Assert.Equal(WebBindDenial.None, decision.Denial);
    }

    [Fact]
    public void 要求開放且已關閉無金鑰時才開放()
    {
        var decision = WebBindPolicy.Resolve(requested: true, allowNoKey: "0");

        Assert.True(decision.AllowLan);
        Assert.Equal(WebBindDenial.None, decision.Denial);
    }

    [Fact]
    public void 還允許無金鑰時不開放並說出原因()
    {
        var decision = WebBindPolicy.Resolve(requested: true, allowNoKey: "1");

        Assert.False(decision.AllowLan);
        Assert.Equal(WebBindDenial.NoKey, decision.Denial);
    }

    [Fact]
    public void 設定鍵就是設定頁與設定庫共用的那兩個()
    {
        Assert.Equal("web.allow_lan", WebBindPolicy.AllowLanKey);
        Assert.Equal("web.allow_no_key", WebBindPolicy.NoKeyKey);
    }
}
