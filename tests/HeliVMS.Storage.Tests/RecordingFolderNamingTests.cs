using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 錄影資料夾命名規則（M311）。
///
/// <para>
/// 這些規則同時被「錄影端寫入」與「舊資料夾搬移」使用；兩邊只要算出不同名稱，
/// 已經搬好的目錄會被當成還需要搬，或新錄的區段寫進另一個目錄，同一段錄影就散在兩處。
/// 因此名稱必須是頻道名稱的純函式，且消歧結果在頻道集合相同時永遠一致（依頻道編號排序）。
/// </para>
/// </summary>
public sealed class RecordingFolderNamingTests
{
    [Fact]
    public void 資料夾名稱使用清理後的頻道名稱()
    {
        Assert.Equal("前門", RecordingFolderNaming.Sanitize("前門", 3));
    }

    [Fact]
    public void 沒有名稱時退回頻道編號目錄()
    {
        Assert.Equal("ch007", RecordingFolderNaming.Sanitize(null, 7));
        Assert.Equal("ch007", RecordingFolderNaming.Sanitize("   ", 7));
    }

    [Fact]
    public void 路徑非法字元會被置換成底線()
    {
        var invalid = Path.GetInvalidFileNameChars();
        var polluted = "a" + string.Join(string.Empty, invalid) + "b";

        var cleaned = RecordingFolderNaming.Sanitize(polluted, 1);

        Assert.DoesNotContain(cleaned, ch => Array.IndexOf(invalid, ch) >= 0);
        Assert.StartsWith("a", cleaned, StringComparison.Ordinal);
        Assert.EndsWith("b", cleaned, StringComparison.Ordinal);
    }

    /// <summary>
    /// 尾端的句點與空白在 Windows 上會被默默剝除，若不清掉，磁碟上的目錄名會和資料庫記錄不一致，
    /// 之後的搬移與回放對不上。
    /// </summary>
    [Fact]
    public void 尾端的句點與空白會被移除()
    {
        Assert.Equal("前門", RecordingFolderNaming.Sanitize("前門...", 1));
        Assert.Equal("前門", RecordingFolderNaming.Sanitize("  前門  ", 1));
    }

    /// <summary>Windows 保留裝置名稱（CON、COM1…）不能當目錄名，否則建立目錄會直接失敗。</summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void 保留裝置名稱會加上前置底線(string reserved)
    {
        Assert.Equal("_" + reserved, RecordingFolderNaming.Sanitize(reserved, 1));
    }

    /// <summary>名稱長度要有上限，否則整條路徑逼近 MAX_PATH，錄影會間歇性失敗。</summary>
    [Fact]
    public void 名稱過長會被截斷()
    {
        var cleaned = RecordingFolderNaming.Sanitize(new string('a', 200), 1);

        Assert.Equal(80, cleaned.Length);
    }

    [Fact]
    public void 同名頻道會附加頻道編號消歧()
    {
        var map = RecordingFolderNaming.Resolve(
        [
            new ChannelInfo { Id = 1, Name = "大門" },
            new ChannelInfo { Id = 2, Name = "大門" },
        ]);

        Assert.Equal("大門", map[1]);
        Assert.Equal("大門_ch002", map[2]);
    }

    /// <summary>
    /// 消歧必須依頻道編號排序且與輸入順序無關；否則錄影端與搬移端可能一個把「大門」給 ch1、
    /// 另一個給 ch2，導致目錄互相搬來搬去。
    /// </summary>
    [Fact]
    public void 消歧結果與輸入順序無關()
    {
        var forward = RecordingFolderNaming.Resolve(
        [
            new ChannelInfo { Id = 1, Name = "大門" },
            new ChannelInfo { Id = 2, Name = "大門" },
        ]);
        var reversed = RecordingFolderNaming.Resolve(
        [
            new ChannelInfo { Id = 2, Name = "大門" },
            new ChannelInfo { Id = 1, Name = "大門" },
        ]);

        Assert.Equal(forward, reversed);
    }

    /// <summary>消歧後的名稱本身若又和其他頻道的名稱碰撞，必須再加序號直到唯一。</summary>
    [Fact]
    public void 消歧後仍然撞名時會再加序號()
    {
        var map = RecordingFolderNaming.Resolve(
        [
            new ChannelInfo { Id = 1, Name = "大門" },
            new ChannelInfo { Id = 2, Name = "大門" },
            new ChannelInfo { Id = 3, Name = "大門_ch002" },
        ]);

        Assert.Equal(3, map.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
