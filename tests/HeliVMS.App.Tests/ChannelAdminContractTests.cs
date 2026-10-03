using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 頻道編輯／刪除入口契約（M60 設定頁「頻道」）。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試，設定頁的按鈕接線只能靠原始碼契約把住。
/// 這裡防的是三種回歸：<c>ChannelList</c> 忘了接 <c>SelectionChanged</c>（選了頻道卻沒載入欄位）、
/// 套用時用 <c>Update</c> 但沒帶回原 <c>Id</c>（會新增一列或改錯列）、
/// 以及刪除少了兩段式確認（<c>channels</c> 是 CASCADE 父表，誤刪會連帶清掉錄影索引與事件）。
/// </para>
/// </summary>
public sealed class ChannelAdminContractTests
{
    private const string Code = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/SettingsWindow.xaml";

    [Fact]
    public void 頻道清單選取會接線到編輯載入()
    {
        Assert.Equal("OnChannelSelected", WiredHandler("ChannelList"));
    }

    [Fact]
    public void 套用變更與刪除按鈕有接線到處理器()
    {
        Assert.Equal("OnApplyChannelEditClicked", WiredHandler("ApplyChannelEditButton"));
        Assert.Equal("OnDeleteChannelClicked", WiredHandler("DeleteChannelButton"));
    }

    [Fact]
    public void 套用變更會呼叫Update並保留頻道識別()
    {
        var body = BodyOf("OnApplyChannelEditClicked");
        Assert.NotEqual(string.Empty, body);

        Assert.Contains(".Update(", body);
        // Update 是整列覆寫；少了 Id 會改到別的列（或 0 列），行為完全靜默。
        Assert.Matches(@"Id\s*=\s*id", body);
    }

    [Fact]
    public void 刪除頻道採兩段式確認且呼叫Delete()
    {
        var body = BodyOf("OnDeleteChannelClicked");
        Assert.NotEqual(string.Empty, body);

        // 兩段式：第一次只佈署、「確認刪除？」只在第二次才真的刪。
        Assert.Contains("_channelDeleteArm", body);
        Assert.Contains("確認刪除？", body);
        Assert.Contains(".Delete(id)", body);
    }

    [Fact]
    public void 重新載入頻道會清空編輯狀態()
    {
        var body = BodyOf("ReloadChannels");
        Assert.NotEqual(string.Empty, body);

        // 不清空的話，使用者會以為畫面上的欄位對應到清單第一列而誤改。
        Assert.Contains("_channelEditId = null", body);
        Assert.Contains("ChannelEditNameBox.Text = string.Empty", body);
        Assert.Contains("_channelDeleteArm = false", body);
    }

    private static string BodyOf(string method)
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), method);
        Assert.NotEqual(string.Empty, body);
        return body;
    }

    private static string WiredHandler(string element)
    {
        var xaml = SourceContract.Read(Xaml);
        var tag = Regex.Match(
            xaml,
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;

        Assert.NotEqual(string.Empty, tag);
        var handler = Regex.Match(tag, @"\b(?:Click|SelectionChanged)=""(\w+)""");
        return handler.Success ? handler.Groups[1].Value : string.Empty;
    }
}
