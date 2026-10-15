using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 裝置群組（設備樹／分區，§7.4）契約。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試，裝置樹只能靠原始碼契約把住。這裡防的是幾種回歸：
/// 群組按鈕或樹的事件忘了接線（建了分區卻拖不動、按了沒反應）、
/// 非管理員帳號能改設備歸屬、刪群組時連帶把底下整批攝影機刪掉（連錄影索引一起清）、
/// 以及拖放只認裝置節點（否則拖一個群組節點會意外把整棵子樹搬走）。
/// </para>
/// </summary>
public sealed class DeviceGroupContractTests
{
    private const string Code = "src/HeliVMS.App/CameraManagerWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/CameraManagerWindow.xaml";

    [Fact]
    public void 裝置樹的五個事件都有接線()
    {
        var tag = ElementTag("DeviceTree");

        // 少了任何一個，樹就只是看的：選取不會同步清單、無法拖曳、也放不下去。
        Assert.Contains("AllowDrop=\"True\"", tag);
        Assert.Contains("SelectedItemChanged=\"OnDeviceTreeNodeSelected\"", tag);
        Assert.Contains("PreviewMouseLeftButtonDown=\"OnDeviceTreeMouseDown\"", tag);
        Assert.Contains("MouseMove=\"OnDeviceTreeMouseMove\"", tag);
        Assert.Contains("DragOver=\"OnDeviceTreeDragOver\"", tag);
        Assert.Contains("Drop=\"OnDeviceTreeDrop\"", tag);
    }

    [Fact]
    public void 群組建立改名刪除與指派按鈕都有接線()
    {
        Assert.Equal("OnAddDeviceGroupClicked", WiredHandler("AddDeviceGroupButton"));
        Assert.Equal("OnRenameDeviceGroupClicked", WiredHandler("RenameDeviceGroupButton"));
        Assert.Equal("OnDeleteDeviceGroupClicked", WiredHandler("DeleteDeviceGroupButton"));
        Assert.Equal("OnAssignDeviceToGroupClicked", WiredHandler("AssignDeviceToGroupButton"));
    }

    [Fact]
    public void 視窗有群組編輯區塊()
    {
        var xaml = SourceContract.Read(Xaml);

        Assert.Contains("DeviceTree", xaml);
        Assert.Contains("DeviceGroupNameBox", xaml);
        Assert.Contains("DeviceGroupHintText", xaml);
    }

    [Fact]
    public void 裝置樹以程式碼建立節點而非資料樣板()
    {
        // 刻意用程式碼逐一建立 TreeViewItem 並把身分放進 Tag，換取拖放時不必解繫結即可
        // 判斷節點種類；若哪天改回 HierarchicalDataTemplate，這裡會提醒要一併重寫拖放。
        var xaml = SourceContract.Read(Xaml);
        Assert.DoesNotContain("<HierarchicalDataTemplate", xaml);

        var body = BodyOf("ReloadDeviceTree");
        Assert.Contains("TreeViewItem", body);
        Assert.Contains("DeviceTree.Items.Clear()", body);
        Assert.Contains("DeviceTree.Items.Add", body);
    }

    [Fact]
    public void 樹節點以Tag攜帶身分()
    {
        var source = SourceContract.Read(Code);

        // 拖放與選取都靠 Tag 判斷，不比對在地化後的顯示文字（換語系就不會失效）。
        Assert.Contains("record DeviceNode(", source);
        Assert.Contains("Tag = new DeviceNode(", source);
    }

    [Fact]
    public void 非管理員不得異動裝置群組()
    {
        // 每個會寫資料庫的 handler 都要先擋角色，否則唯讀帳號可以改設備分區與歸屬。
        foreach (var handler in new[]
        {
            "OnAddDeviceGroupClicked", "OnRenameDeviceGroupClicked", "OnDeleteDeviceGroupClicked",
            "OnAssignDeviceToGroupClicked", "OnDeviceTreeMouseMove", "OnDeviceTreeDrop",
        })
        {
            var body = BodyOf(handler);
            Assert.Contains("SessionContext.IsAdmin", body);
            Assert.Contains("\"Camera.GroupAdminOnly\"", body);
        }
    }

    [Fact]
    public void 刪除群組採兩段式確認且不刪除底下裝置()
    {
        var body = BodyOf("OnDeleteDeviceGroupClicked");

        // 誤刪分區不該無法復原，故兩段式：第一次只改按鈕文字，第二次才真的刪。
        Assert.Contains("_deviceGroupDeleteArm", body);
        Assert.Contains("\"Camera.GroupDeleteConfirm\"", body);
        Assert.Contains(".Delete(id, Actor())", body);

        // 刪群組只把裝置移到未分組（資料層為 SET NULL）；UI 絕不可自行刪掉裝置，
        // 否則會連帶 CASCADE 清掉頻道與錄影索引。
        Assert.DoesNotContain("_devices.Delete", body);
    }

    [Fact]
    public void 指派只改裝置歸屬且未分組視為移出()
    {
        var clickBody = BodyOf("OnAssignDeviceToGroupClicked");
        // 目標未選時要擋下（含「未分組」節點也必須先被選取），避免手誤把裝置移出群組。
        Assert.Contains("_deviceGroupTargetChosen", clickBody);
        Assert.Contains("AssignDeviceToGroup(row, _selectedDeviceGroupId)", clickBody);

        var assignBody = BodyOf("AssignDeviceToGroup");
        Assert.Contains("_deviceGroups.AssignDevice(row.Id, groupId, Actor())", assignBody);
        // groupId 為 null＝移出到未分組，提示文字也分開，操作者才知道發生了什麼。
        Assert.Contains("\"Camera.GroupMovedUngrouped\"", assignBody);
        Assert.Contains("\"Camera.GroupAssigned\"", assignBody);
    }

    [Fact]
    public void 拖放只接受裝置節點且放到群組或未分組()
    {
        var moveBody = BodyOf("OnDeviceTreeMouseMove");
        Assert.Contains("DeviceNodeKind.Device", moveBody);
        Assert.Contains("DragDrop.DoDragDrop", moveBody);
        // 未超過系統門檻不啟動拖放，否則單純點選裝置也會被判成拖曳。
        Assert.Contains("SystemParameters.MinimumHorizontalDragDistance", moveBody);

        var dropBody = BodyOf("OnDeviceTreeDrop");
        Assert.Contains("TryGroupTarget", dropBody);
        Assert.Contains("AssignDeviceToGroup", dropBody);

        var targetBody = BodyOf("TryGroupTarget");
        Assert.Contains("DeviceNodeKind.Group", targetBody);
        Assert.Contains("DeviceNodeKind.Ungrouped", targetBody);
    }

    [Fact]
    public void 群組操作失敗會回報而非靜默()
    {
        // 名稱重複、資料庫鎖定等都會擲例外；吞掉會讓操作者以為成功但樹沒變。
        foreach (var handler in new[]
        {
            "OnAddDeviceGroupClicked", "OnRenameDeviceGroupClicked", "OnDeleteDeviceGroupClicked", "AssignDeviceToGroup",
        })
        {
            Assert.Contains("\"Camera.GroupFailed\"", BodyOf(handler));
        }
    }

    [Fact]
    public void 點樹上的裝置會同步選取清單同一列()
    {
        var body = BodyOf("OnDeviceTreeNodeSelected");

        // 否則在樹上點裝置時，下方編輯表單仍停在上一台，儲存就改錯台。
        Assert.Contains("DeviceList.SelectedItem = match", body);
        Assert.Contains("DeviceNodeKind.Device", body);
    }

    private static string BodyOf(string method)
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), method);
        Assert.NotEqual(string.Empty, body);
        return body;
    }

    /// <summary>取得具名元素的完整開標籤（含所有事件屬性）。</summary>
    private static string ElementTag(string element)
    {
        var xaml = SourceContract.Read(Xaml);
        var tag = Regex.Match(
            xaml,
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;

        Assert.NotEqual(string.Empty, tag);
        return tag;
    }

    /// <summary>取得具名元素的 <c>Click</c>／<c>SelectionChanged</c> 處理器。</summary>
    private static string WiredHandler(string element)
    {
        var handler = Regex.Match(ElementTag(element), @"\b(?:Click|SelectionChanged)=""(\w+)""");
        return handler.Success ? handler.Groups[1].Value : string.Empty;
    }
}
