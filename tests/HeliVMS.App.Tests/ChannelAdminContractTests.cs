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
    private const string Code = "src/HeliVMS.App/ChannelManagerWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/ChannelManagerWindow.xaml";

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
        // 確認文字已在地化為 Channels.DeleteConfirm 鍵（不硬編中文）。
        Assert.Contains("_channelDeleteArm", body);
        Assert.Contains("\"Channels.DeleteConfirm\"", body);
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

    [Fact]
    public void 監看格位下拉會寫進DisplayCell並換算成零基()
    {
        var body = BodyOf("OnApplyChannelEditClicked");

        // 這格若沒接到 Update 的 DisplayCell，使用者選的監看格位在重開後就跳回自動，
        // 而畫面當下看起來卻是成功的（提示文字照樣顯示「已更新」），回報時無跡可尋。
        Assert.Contains("ChannelEditCellCombo.SelectedItem", body);
        Assert.Contains("DisplayCell = displayCell", body);
        // 下拉是 1 基格號、儲存是 0 基索引；少減 1 整體會偏一格。
        Assert.Contains("cellNumber - 1", body);
    }

    [Fact]
    public void 監看格位下拉選項依授權路數產生且含自動()
    {
        var body = BodyOf("PopulateCellOptions");

        // 選項要跟著授權路數走（匯入新授權後路數會變），且一定要有「自動」可退。
        Assert.Contains("Channels.CellAuto", body);
        Assert.Contains("SelectableCells(", body);
        Assert.Contains(".MaxCameras", body);
    }

    [Fact]
    public void 已被其他頻道佔用的格位會反灰不可選()
    {
        var body = BodyOf("PopulateCellOptions");

        // 一個格子只能給一台攝影機；沒接上 TakenCells 就會出現兩台搶同一格。
        Assert.Contains("TakenCells(", body);
        // 反灰即不可選（WPF 以 IsEnabled 控制），故選項要以 busy 決定啟用狀態。
        Assert.Contains("busy", body);
        Assert.Contains("IsEnabled = !busy", BodyOf("AddCellOption"));
    }

    [Fact]
    public void 頻道清單與編輯列都有監看格位欄位()
    {
        var xaml = SourceContract.Read(Xaml);

        Assert.Contains("ChannelColCell", xaml);
        Assert.Contains("ChannelEditCellCombo", xaml);
    }

    [Fact]
    public void 手動新增未命名時依監看格位命名並寫入格位()
    {
        var body = BodyOf("OnAddChannelClicked");

        // 名稱留白時要依「這台頻道將佔用的格子」命名（攝影機06…），而不是隨便補第一個空缺；
        // 並在建立當下就把格位寫進 DisplayCell，否則之後刪掉別的頻道會整體往前擠，
        // 名稱與監看牆上的位置就對不上。
        Assert.Contains("NextFreeCell(", body);
        Assert.Contains("DefaultChannelNameForCell(", body);
        Assert.Contains("displayCell: cell", body);
    }

    [Fact]
    public void 依格命名撞名時退回第一個未使用序號()
    {
        // 若算出的名稱已被別的頻道佔用（例如曾被手動改名），退回 ChannelNaming.NextDefault，
        // 避免監看牆上出現兩個同名頻道。
        var body = BodyOf("DefaultChannelNameForCell");

        Assert.Contains("string.Format(", body);
        Assert.Contains("ChannelNaming.NextDefault(", body);
    }

    [Fact]
    public void 編輯指定監看格位時自動命名頻道會跟著格號()
    {
        // 把頻道排到別的格子後名稱若沒跟著走，畫面上「攝影機05」會出現在第3格，
        // 操作員靠名稱找機位時就會找錯。
        Assert.Contains("NameForAssignedCell(", BodyOf("OnApplyChannelEditClicked"));

        var helper = BodyOf("NameForAssignedCell");

        // 只改自動命名；自訂名稱要保留（否則操作員取的名字會被無預警覆蓋）。
        Assert.Contains("ChannelNaming.MatchesDefault(", helper);
        Assert.Contains("string.Format(", helper);
    }

    [Fact]
    public void 頻道新增修改刪除後會通知主視窗重載()
    {
        var code = SourceContract.Read(Code);

        // 沒有這個通知時，監看區只會在「設定中心」關閉後才更新；刪除頻道期間監控中心
        // 會一直殘留已刪除攝影機的畫面，得等使用者關掉設定中心才會清掉。
        foreach (var handler in new[] { "OnAddChannelClicked", "OnApplyChannelEditClicked", "OnDeleteChannelClicked" })
        {
            Assert.Contains("NotifyChannelsChanged();", SourceContract.BodyOf(code, handler));
        }
    }

    [Fact]
    public void 主視窗把頻道異動回呼一路傳到頻道管理()
    {
        var main = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");
        Assert.Contains("ReloadChannelsIfChangedAsync", SourceContract.BodyOf(main, "OpenSettingsWindow"));

        var settings = SourceContract.Read("src/HeliVMS.App/SettingsWindow.xaml.cs");
        var openChannel = SourceContract.BodyOf(settings, "OnOpenChannelManagerClicked");
        Assert.Contains("ChannelManagerWindow", openChannel);
        Assert.Contains("_channelsChanged", openChannel);
    }

    /// <summary>
    /// 「來源攝影機」下拉顯示 IP，且已加入頻道的攝影機不再出現。
    /// </summary>
    /// <remarks>
    /// 篩選與顯示規則在 <see cref="HeliVMS.Shared.CameraSourceOptions"/>（有行為測試）；
    /// 這裡只釘住接線——下拉若改回 <c>device.Name</c> 或忘了把已綁定清單傳進去，
    /// 行為測試照樣全綠，操作員卻會看到名稱、並且能把同一台攝影機加進第二條頻道。
    /// </remarks>
    [Fact]
    public void 來源攝影機下拉顯示IP並排除已綁定頻道者()
    {
        var body = BodyOf("ReloadDevices");

        Assert.Contains("CameraSourceOptions.Build(", body);
        Assert.Contains("c.DeviceId", body);
        Assert.Contains("option.Label", body);
        Assert.DoesNotContain("device.Name", body);
    }

    /// <summary>
    /// 新增頻道後要重載「來源攝影機」下拉，剛用掉的攝影機才會立刻消失。
    /// </summary>
    /// <remarks>
    /// 只重載頻道清單的話，清單看起來正確，但下拉仍留著剛綁定的那台；操作員順手再按一次
    /// 「新增」就得到第二條綁同一裝置的頻道，兩邊搶同一組串流。
    /// </remarks>
    [Fact]
    public void 新增頻道後會重載來源攝影機下拉()
    {
        Assert.Contains("ReloadDevices();", BodyOf("OnAddChannelClicked"));
    }

    /// <summary>
    /// 刪除頻道後，它的攝影機要回到「來源攝影機」下拉才選得到。
    /// </summary>
    /// <remarks>
    /// 下拉已排除「有頻道綁定」的攝影機；刪掉頻道若不重載下拉，那台機器就要等到
    /// 關掉再開設定中心才會出現，操作員會以為它被刪掉了（攝影機本身其實還在 devices 表）。
    /// </remarks>
    [Fact]
    public void 刪除頻道後攝影機會回到來源下拉()
    {
        Assert.Contains("ReloadDevices();", BodyOf("OnDeleteChannelClicked"));
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
