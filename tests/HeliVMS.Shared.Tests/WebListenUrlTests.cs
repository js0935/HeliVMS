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
}
