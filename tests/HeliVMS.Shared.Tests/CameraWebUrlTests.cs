using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 攝影機管理「開啟網頁」的位址組裝。
///
/// <para>
/// 兩件事在現場都會讓按鈕看起來壞掉：<c>devices.port</c> 是 ONVIF 服務埠（實機常見
/// 2020／8000／8899），拿它開瀏覽器只會得到 SOAP 錯誤頁；而位址欄又可能是已經帶了
/// scheme 或埠的舊值。這裡把兩種情況釘成行為，不必真的開瀏覽器才能驗。
/// </para>
/// </summary>
public class CameraWebUrlTests
{
    [Fact]
    public void 設定值是80時開http的80埠且省略埠號()
    {
        Assert.Equal("http://192.168.1.10/", CameraWebUrl.Build("192.168.1.10", 80));
    }

    [Fact]
    public void ONVIF服務埠不是網頁埠時回到80而不是開那個埠()
    {
        // 實機：ONVIF 在 2020／8000／8899／34567／5000，網頁面一律在 80。
        // 舊行為（直接組 http://ip:2020/）會停在 SOAP 錯誤頁，操作員只會說「按鈕壞了」。
        foreach (var onvifPort in new[] { 2020, 8000, 8899, 34567, 5000 })
        {
            Assert.Equal("http://192.168.1.10/", CameraWebUrl.Build("192.168.1.10", onvifPort));
        }
    }

    [Fact]
    public void 設定值是8080時直接開8080()
    {
        // 有些攝影機的網頁介面就設在 8080；那時設定值本身就是答案。
        Assert.Equal("http://192.168.1.10:8080/", CameraWebUrl.Build("192.168.1.10", 8080));
    }

    [Fact]
    public void 設定值是443時用https且省略埠號()
    {
        Assert.Equal("https://cam.example/", CameraWebUrl.Build("cam.example", 443));
    }

    [Fact]
    public void 位址已帶埠或scheme時沿用而不是再接一層()
    {
        // 舊資料／手動輸入可能是 192.168.1.10:8080；再接一次 http:// 會變成無法解析的字串。
        Assert.Equal("http://192.168.1.10:8080/", CameraWebUrl.Build("192.168.1.10:8080", 80));
        Assert.Equal("https://cam.example/", CameraWebUrl.Build("https://cam.example", 80));
        Assert.Equal("http://cam.example/live", CameraWebUrl.Build("http://cam.example/live", 80));
    }

    [Fact]
    public void IPv6位址會加上方括號()
    {
        // 少了方括號，http://fe80::1/ 會被解讀成「主機 fe80、埠 :1」。
        Assert.Equal("http://[fe80::1]/", CameraWebUrl.Build("fe80::1", 80));
        Assert.Equal("http://[::1]/", CameraWebUrl.Build("[::1]", 80));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.1.10/live")]
    [InlineData("192.168.1.10\\live")]
    [InlineData("ftp://cam.example")]
    [InlineData("192.168.1.10 8080")]
    public void 解析不出來的位址回空值讓呼叫端說明原因(string? host)
    {
        // 回空值而不是硬組一個網址：呼叫端要能說出「哪個位址讀不懂」，
        // 否則操作員只看到按了沒反應。
        Assert.Null(CameraWebUrl.Build(host, 80));
    }
}
