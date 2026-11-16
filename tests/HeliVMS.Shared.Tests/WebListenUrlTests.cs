using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 內嵌 Web 主機的 Kestrel 監聽網址。共用埠時公開埠要留給 QCTek 多工器，
/// 所以 Kestrel 只能綁系統指派的臨時埠。
/// </summary>
public class WebListenUrlTests
{
    [Fact]
    public void 共用埠時Kestrel綁明確的迴環位址而不是localhost()
    {
        var url = WebListenUrl.For(80, sharePort: true);

        // Kestrel 對 localhost:0 直接擲 InvalidOperationException（實測：
        // Dynamic port binding is not supported when binding to localhost）。
        // 那句話被啟動失敗處理吞掉之後，QCC 只會看到「連線中斷」。
        Assert.DoesNotContain("localhost", url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("http://127.0.0.1:0", url);
    }

    [Fact]
    public void 共用埠時監聽網址不帶公開埠號()
    {
        // 帶了公開埠就等於兩個服務搶同一個埠：Web 先綁走，多工器只能啟動失敗。
        Assert.DoesNotContain(":80", WebListenUrl.For(80, sharePort: true), StringComparison.Ordinal);
    }

    [Fact]
    public void 不共用埠時Kestrel綁設定頁填的公開埠()
    {
        Assert.Equal("http://localhost:5080", WebListenUrl.For(5080, sharePort: false));
    }

    [Fact]
    public void 預設只綁迴圈位址()
    {
        // 這個介面沒有 TLS，而且可以看影像、操作 PTZ、匯出錄影——開放區域網路必須是明講的選擇。
        var url = WebListenUrl.For(80, sharePort: false);

        Assert.Equal("http://localhost:80", url);
        Assert.DoesNotContain("*", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 允許區域網路時綁所有介面()
    {
        // 「在辦公室連進來」的唯一實作方式：綁所有介面。少了這個參數，設定頁勾了也沒用。
        Assert.Equal("http://*:80", WebListenUrl.For(80, sharePort: false, allowLan: true));
    }

    [Fact]
    public void 共用埠時以多工器為準_不因開放區域網路而改綁公開埠()
    {
        // 共用埠模式下公開埠由多工器擁有（它自己決定綁哪個位址，見 QctekCompatHost.StartMux）。
        // Kestrel 仍然只能綁明確的迴圈位址 + 系統指派埠，否則兩個服務搶同一個埠。
        Assert.Equal("http://127.0.0.1:0", WebListenUrl.For(80, sharePort: true, allowLan: true));
    }
}
