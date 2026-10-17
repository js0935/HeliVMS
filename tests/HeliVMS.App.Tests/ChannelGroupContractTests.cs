using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 監看群組（監控中心頁簽）契約。
///
/// <para>
/// 監看群組原本夾在「頻道管理」視窗底部，現已抽成獨立的 <c>MonitorGroupWindow</c>，
/// 入口放在設定中心的「裝置與頻道」頁。桌面端沒有 WPF 執行期測試，群組的管理入口與頁簽切換
/// 只能靠原始碼契約把住。這裡防的是五種回歸：裝置與頻道頁的入口按鈕忘了接線或忘了擋非管理員
/// （群組按鈕建了群組卻沒反應／唯讀帳號能改頁簽）、群組視窗的按鈕忘了接線、
/// 成員異動後沒通知主視窗（頁簽成員改了但監看牆照舊）、同一台攝影機被加入第二個群組
/// （資料層允許改派，UI 忘了擋就會把該台從舊群組悄悄搬走）、
/// 以及切頁簽時誤用 <c>ConnectAsync</c> 重連（切個頁簽就中斷所有錄影）。
/// </para>
/// </summary>
public sealed class ChannelGroupContractTests
{
    private const string GroupCode = "src/HeliVMS.App/MonitorGroupWindow.xaml.cs";
    private const string GroupXaml = "src/HeliVMS.App/MonitorGroupWindow.xaml";
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string MainXaml = "src/HeliVMS.App/MainWindow.xaml";

    [Fact]
    public void 群組清單選取會接線到載入成員()
    {
        Assert.Equal("OnGroupSelected", WiredHandler(GroupXaml, "GroupList"));
    }

    [Fact]
    public void 建立改名刪除群組按鈕都有接線()
    {
        Assert.Equal("OnCreateGroupClicked", WiredHandler(GroupXaml, "CreateGroupButton"));
        Assert.Equal("OnRenameGroupClicked", WiredHandler(GroupXaml, "RenameGroupButton"));
        Assert.Equal("OnDeleteGroupClicked", WiredHandler(GroupXaml, "DeleteGroupButton"));
    }

    [Fact]
    public void 成員加入移除與上下移按鈕都有接線()
    {
        Assert.Equal("OnAddToGroupClicked", WiredHandler(GroupXaml, "AddToGroupButton"));
        Assert.Equal("OnRemoveFromGroupClicked", WiredHandler(GroupXaml, "RemoveFromGroupButton"));
        Assert.Equal("OnGroupUpClicked", WiredHandler(GroupXaml, "GroupUpButton"));
        Assert.Equal("OnGroupDownClicked", WiredHandler(GroupXaml, "GroupDownButton"));
    }

    [Fact]
    public void 刪除群組採兩段式確認且呼叫Delete()
    {
        var body = BodyOf(GroupCode, "OnDeleteGroupClicked");

        // 群組刪除會 CASCADE 清掉成員歸屬，誤刪無法復原，故要兩段式：
        // 第一次只佈署、「按第二次才真的刪」。
        Assert.Contains("_groupDeleteArm", body);
        Assert.Contains("\"Channels.GroupDeleteConfirm\"", body);
        Assert.Contains(".Delete(id)", body);
    }

    [Fact]
    public void 非管理員不得建立或異動群組()
    {
        // 每個會寫資料庫的 handler 都要先擋角色；沒有這道，唯讀帳號可以改監看牆的頁簽內容。
        foreach (var handler in new[]
        {
            "OnCreateGroupClicked", "OnRenameGroupClicked", "OnDeleteGroupClicked",
            "OnAddToGroupClicked", "OnRemoveFromGroupClicked", "MoveGroupMember",
        })
        {
            var body = BodyOf(GroupCode, handler);
            Assert.NotEqual(string.Empty, body);
            Assert.Contains("SessionContext.IsAdmin", body);
            Assert.Contains("\"Channels.GroupAdminOnly\"", body);
        }
    }

    [Fact]
    public void 群組異動後會通知主視窗重載()
    {
        // 沒有這個通知時，主視窗的頁簽只會在設定中心關閉後才更新；在視窗內加／改／刪群組
        // 期間，監控中心的頁簽與成員會與資料庫不一致。
        foreach (var handler in new[]
        {
            "OnCreateGroupClicked", "OnRenameGroupClicked", "OnDeleteGroupClicked",
            "OnAddToGroupClicked", "OnRemoveFromGroupClicked", "MoveGroupMember",
        })
        {
            Assert.Contains("NotifyChannelsChanged();", BodyOf(GroupCode, handler));
        }
    }

    [Fact]
    public void 加入成員是依群組重新指定歸屬()
    {
        // 一台攝影機最多屬一個群組；加入＝Assign（資料層負責先清掉舊歸屬再排到尾端），
        // 而不是自己在 UI 疊一筆，否則同一台會出現在兩個頁簽。
        var body = BodyOf(GroupCode, "OnAddToGroupClicked");
        Assert.Contains("_groups.Assign(row.Id, groupId)", body);
    }

    [Fact]
    public void 已歸屬其他群組的攝影機加入時會被擋下()
    {
        // 使用者要求「不同的群組不能加入相同的攝影機」。資料層雖只允許一台一組，但直接
        // Assign 會把該台從舊群組悄悄搬過來——使用者看到的是舊群組莫名空了。故加入前先查
        // GroupOf，已歸屬就提示所屬群組並 return，不執行 Assign（要改派得先移除）。
        var body = BodyOf(GroupCode, "OnAddToGroupClicked");

        Assert.Contains("_groups.GroupOf(row.Id)", body);
        Assert.Contains("\"Channels.GroupAlreadyAssigned\"", body);
        Assert.Contains("return;", body);

        // 擋下的判斷要在 Assign 之前，否則就只是先搬再提示。
        Assert.True(
            body.IndexOf("GroupOf(row.Id)", StringComparison.Ordinal)
                < body.IndexOf("_groups.Assign(", StringComparison.Ordinal),
            "歸屬檢查必須發生在 Assign 之前。");
    }

    [Fact]
    public void 來源清單標示已歸屬頻道所屬群組()
    {
        // 已加到某群組的攝影機仍留在來源清單，但名稱後要標「（群組：…）」，讓操作者一眼
        // 看出它已被佔用；少了這個標示，清單看起來每台都還能加。
        var body = BodyOf(GroupCode, "ReloadSourceChannels");

        Assert.Contains("Channels.GroupAssignedLabel", body);
        Assert.Contains("ListChannelIds", body);
        Assert.Contains("Label", SourceContract.Read(GroupXaml));
    }

    [Theory]
    [InlineData("OnAddToGroupClicked")]
    [InlineData("OnRemoveFromGroupClicked")]
    [InlineData("OnDeleteGroupClicked")]
    [InlineData("OnRenameGroupClicked")]
    public void 群組歸屬或名稱改變後會刷新來源清單(string handler)
    {
        // 來源清單顯示的是「頻道→所屬群組」；加入／移除／刪群組／改名都會改變這個對應，
        // 沒重載就會出現「已移出卻仍標示群組」或標到舊名稱。
        Assert.Contains("ReloadSourceChannels();", BodyOf(GroupCode, handler));
    }

    [Fact]
    public void 監控中心頁簽選取會接線到切換處理()
    {
        Assert.Equal("OnGroupTabChanged", WiredHandler(MainXaml, "GroupTabs"));
    }

    [Fact]
    public void 頁簽只渲染標題列不複製整面監看牆()
    {
        // 若頁簽樣板內含內容區，就會複製整個監看牆；切頁簽放大成「重建所有格位」。
        // 樣板刻意只留 TabPanel，全體共用下方同一個 MonitorArea。
        var template = TemplateOf(MainXaml, "TabControl");
        Assert.Contains("TabPanel IsItemsHost", template);
        Assert.DoesNotContain("MonitorArea", template);
    }

    [Fact]
    public void 切頁簽只改派既有工作階段不重連()
    {
        var body = BodyOf(MainCode, "ApplyWallAsync");

        // 重新連線（ConnectAsync 先拆再建）會中斷所有攝影機錄影；切頁簽只該改派格位。
        Assert.Contains("ReconcileAsync(", body);
        Assert.DoesNotContain("ConnectAsync(", body);
        // 群組看不到的頻道其最後一張影格要清掉，否則頁簽上會殘留別頁的畫面。
        Assert.Contains("ClearCellsBeyondChannels(wall.Count)", body);
    }

    [Fact]
    public void 切頁簽前會記住原頁簽的格數()
    {
        var body = BodyOf(MainCode, "OnGroupTabChanged");

        // 每個頁簽各自記格數：離開前先存，切過去才還原得回來。少了這步，
        // 4 台攝影機的群組會沿用「全部」的 16 格，畫面大半空白。
        Assert.Contains("_layoutPrefs.Remember(_activeGroupId, CurrentCellCount())", body);
        Assert.Contains("_activeGroupId = groupId", body);
    }

    [Fact]
    public void 全部頁簽是第一個且顯示所有頻道()
    {
        var body = BodyOf(MainCode, "ReloadGroupTabs");

        // 第一個頁簽固定是「全部」（Tag 為 null），永遠顯示整份清單。
        Assert.Contains("\"Main.GroupAll\"", body);
        Assert.Contains("Tag = (long?)null", body);
        Assert.Contains("GroupTabs.Items.Add(all)", body);

        // 目前群組被刪除時要退回「全部」，否則 WallChannels 會永遠回傳空清單、監看牆空白。
        Assert.Contains("?? all", body);
    }

    [Fact]
    public void 無指定群組時監看牆顯示全部頻道()
    {
        var body = BodyOf(MainCode, "WallChannels");

        // _activeGroupId 為 null（「全部」頁簽）時直接回整份清單；
        // 有群組時才依群組順序挑成員。
        Assert.Contains("return _channelList", body);
        Assert.Contains("ListChannelIds(groupId)", body);
    }

    [Fact]
    public void 群組頁簽拖曳只改群組內順序()
    {
        var body = BodyOf(MainCode, "ReorderCells");

        // 在群組頁簽拖曳改的是「群組內」順序，不動全域 display_cell 與頻道名稱，
        // 否則會連帶影響「全部」頁簽的機位。
        Assert.Contains("_groups.Move(groupId, wall[from].Id, to - from)", body);
    }

    [Fact]
    public void 監看群組視窗有群組編輯區塊()
    {
        var xaml = SourceContract.Read(GroupXaml);

        Assert.Contains("GroupMemberList", xaml);
        Assert.Contains("GroupNameBox", xaml);
    }

    [Fact]
    public void 裝置與頻道頁的監看群組入口有接線且擋非管理員()
    {
        // 入口從頻道管理搬到「裝置與頻道」頁後，按鈕若忘了接線就整個功能無入口；
        // 忘了擋非管理員則唯讀帳號能直接開視窗改頁簽內容。
        Assert.Equal("OnOpenMonitorGroupsClicked", WiredHandler(SettingsXaml, "OpenMonitorGroupsButton"));

        var body = BodyOf(SettingsCode, "OnOpenMonitorGroupsClicked");
        Assert.Contains("SessionContext.IsAdmin", body);
        Assert.Contains("MonitorGroupWindow", body);
    }

    private static string BodyOf(string path, string method)
    {
        var body = SourceContract.BodyOf(SourceContract.Read(path), method);
        Assert.NotEqual(string.Empty, body);
        return body;
    }

    /// <summary>取得具名元素的 <c>Click</c>／<c>SelectionChanged</c> 處理器。</summary>
    private static string WiredHandler(string xamlPath, string element)
    {
        var xaml = SourceContract.Read(xamlPath);
        var tag = Regex.Match(
            xaml,
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;

        Assert.NotEqual(string.Empty, tag);
        var handler = Regex.Match(tag, @"\b(?:Click|SelectionChanged)=""(\w+)""");
        return handler.Success ? handler.Groups[1].Value : string.Empty;
    }

    /// <summary>取出指定 <c>TargetType</c> 的 <c>ControlTemplate</c> 內容。</summary>
    private static string TemplateOf(string xamlPath, string targetType)
    {
        var xaml = SourceContract.Read(xamlPath);
        var match = Regex.Match(
            xaml,
            $@"<ControlTemplate\s+TargetType=""{Regex.Escape(targetType)}"".*?</ControlTemplate>",
            RegexOptions.Singleline);

        Assert.True(match.Success, $"找不到 TargetType=\"{targetType}\" 的 ControlTemplate。");
        return match.Value;
    }
}
