using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 頻道預設命名（<see cref="ChannelNaming"/>）的行為契約。
/// </summary>
/// <remarks>
/// 預設名稱要能被操作員當成「第幾台」來認，所以序號必須唯一。
/// 舊做法「目前頻道數 + 1」在刪掉中間一台後再新增就會撞名
/// （先有攝影機01、02，刪 01 後新增又得到攝影機02），這裡鎖的是「找第一個空缺」。
/// </remarks>
public sealed class ChannelNamingTests
{
    private const string Hant = "攝影機{0:00}";

    [Fact]
    public void 空清單時預設為攝影機01()
    {
        Assert.Equal("攝影機01", ChannelNaming.NextDefault([], Hant));
    }

    [Fact]
    public void 依序遞增至攝影機02()
    {
        Assert.Equal("攝影機02", ChannelNaming.NextDefault(["攝影機01"], Hant));
    }

    [Fact]
    public void 序號補零至兩位()
    {
        var names = Enumerable.Range(1, 9).Select(n => $"攝影機{n:00}").ToList();

        Assert.Equal("攝影機10", ChannelNaming.NextDefault(names, Hant));
    }

    /// <summary>
    /// 刪掉中間一台後再新增，要補回那個空缺而不是接到尾巴而撞名。
    /// </summary>
    [Fact]
    public void 刪除中間頻道後新增會補回空缺()
    {
        // 原本是 01、02、03，刪掉 02 之後剩下 01、03。
        Assert.Equal("攝影機02", ChannelNaming.NextDefault(["攝影機01", "攝影機03"], Hant));
    }

    /// <summary>
    /// 序號只看同一語系的預設名稱；操作員自訂的名稱不影響編號。
    /// </summary>
    [Fact]
    public void 自訂名稱不影響預設序號()
    {
        Assert.Equal("攝影機01", ChannelNaming.NextDefault(["大門", "停車場"], Hant));
    }

    [Fact]
    public void 依語系格式產生英文名稱()
    {
        Assert.Equal("Camera 03", ChannelNaming.NextDefault(["Camera 01", "Camera 02"], "Camera {0:00}"));
    }

    [Theory]
    [InlineData("攝影機01")]
    [InlineData("攝影機36")]
    [InlineData("攝影機100")]
    public void 預設命名會被辨識為自動命名(string name)
    {
        Assert.True(ChannelNaming.MatchesDefault(name, Hant));
    }

    [Theory]
    [InlineData("大門")]
    [InlineData("攝影機")]      // 沒有序號
    [InlineData("攝影機A")]     // 序號不是數字
    [InlineData("攝影機01A")]   // 數字後面還有字
    [InlineData("我的攝影機01")] // 前綴不同
    [InlineData("")]
    public void 非預設命名不會被辨識為自動命名(string name)
    {
        // 指定監看格位時只改自動命名；自訂名稱若被誤判成自動命名就會被無預警覆蓋。
        Assert.False(ChannelNaming.MatchesDefault(name, Hant));
    }

    [Fact]
    public void 自動命名辨識依語系格式()
    {
        Assert.True(ChannelNaming.MatchesDefault("Camera 01", "Camera {0:00}"));
        Assert.False(ChannelNaming.MatchesDefault("攝影機01", "Camera {0:00}"));
    }

    [Fact]
    public void 預設命名依格位重新編號()
    {
        // 名稱跟著格位走：第一格＝攝影機01、第二格＝攝影機02…（來源編號沒對上格位才需改）
        var result = ChannelNaming.ReassignDefaultNames([("攝影機05", true), ("攝影機09", true)], Hant);
        Assert.Equal("攝影機01", result[0]);
        Assert.Equal("攝影機02", result[1]);
    }

    [Fact]
    public void 已符合格位的預設命名不回報變更()
    {
        // 沒有變更就不要回傳，避免每次載入都無謂地寫資料庫、觸發重新連線。
        var result = ChannelNaming.ReassignDefaultNames([("攝影機01", true), ("攝影機02", true)], Hant);
        Assert.Null(result[0]);
        Assert.Null(result[1]);
    }

    [Fact]
    public void 自訂名稱保留且不被預設命名撞用()
    {
        // 第2格是自訂名稱「攝影機02」，預設命名要避開它。
        var result = ChannelNaming.ReassignDefaultNames(
            [("攝影機09", true), ("攝影機02", false), ("攝影機09", true)], Hant);
        Assert.Equal("攝影機01", result[0]);
        Assert.Null(result[1]);
        Assert.Equal("攝影機03", result[2]);
    }

    [Fact]
    public void 預設命名撞到自訂名稱時改用下一個空號()
    {
        var result = ChannelNaming.ReassignDefaultNames([("Camera 1", true), ("攝影機01", false)], Hant);
        Assert.Equal("攝影機02", result[0]);
        Assert.Null(result[1]);
    }

    [Fact]
    public void 自訂名稱完全符合格位也不動()
    {
        var result = ChannelNaming.ReassignDefaultNames([("攝影機01", false), ("攝影機02", false)], Hant);
        Assert.Null(result[0]);
        Assert.Null(result[1]);
    }

    /// <summary>名稱仍等於綁定的裝置名稱＝這台從沒被命名過，才會被補上格位預設名。</summary>
    [Fact]
    public void 名稱仍等於裝置名稱時算沿用裝置名稱()
    {
        Assert.True(ChannelNaming.CarriesDeviceName("IPC-1234", "IPC-1234"));
    }

    /// <summary>已被補成「攝影機NN」的頻道，之後就屬於這台攝影機，不可再被自動重新編號。</summary>
    [Fact]
    public void 已改成格位預設名就不再視為沿用裝置名稱()
    {
        Assert.False(ChannelNaming.CarriesDeviceName("攝影機03", "IPC-1234"));
    }

    [Fact]
    public void 沒有裝置名稱時不算沿用()
    {
        // 手動輸入 RTSP 的頻道沒有綁定設備；空名稱不該被當成「沿用」而反覆改名。
        Assert.False(ChannelNaming.CarriesDeviceName("大門", null));
        Assert.False(ChannelNaming.CarriesDeviceName(string.Empty, string.Empty));
    }

    [Fact]
    public void 操作員自取的名稱不算沿用()
    {
        Assert.False(ChannelNaming.CarriesDeviceName("大門", "IPC-1234"));
    }
}
