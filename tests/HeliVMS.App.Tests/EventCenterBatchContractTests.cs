using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 事件中心批次處置契約（M248）。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試，批次多選的接線只能掃原始碼。這裡防的回歸是：
/// 使用者全選後按「標記為已確認」只改了第一列（抓不到 foreach 就會紅）、
/// 換 ItemsSource 時把選取集合清掉（重整打斷操作）、
/// 以及兩份檢視（清單／卡片）沒有互相同步，導致切換檢視後選取全部消失。
/// </para>
/// </summary>
public sealed class EventCenterBatchContractTests
{
    private const string EventXaml = "src/HeliVMS.App/EventCenterWindow.xaml";
    private const string EventCode = "src/HeliVMS.App/EventCenterWindow.xaml.cs";

    [Theory]
    [InlineData("EventList")]
    [InlineData("CardList")]
    public void 事件清單與卡片檢視都支援多選(string element)
    {
        var xaml = SourceContract.Read(EventXaml);
        var tag = Regex.Match(
            xaml,
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;

        Assert.NotEqual(string.Empty, tag);
        Assert.Contains("SelectionMode=\"Extended\"", tag);
    }

    [Fact]
    public void 事件中心有全選鈕且接線()
    {
        Assert.Equal("OnSelectAllClicked", WiredHandler(EventXaml, "SelectAllButton"));
    }

    [Fact]
    public void 批次確認會逐筆寫入所有選取事件()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(EventCode), "SetAck");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("foreach", body);
        Assert.Contains("_events.Acknowledge(id, acknowledged)", body);
    }

    [Fact]
    public void 批次處置會逐筆寫入所有選取事件()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(EventCode), "OnApplyDispositionClicked");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("foreach", body);
        Assert.Contains("_events.SetDisposition(id, status, assign, note, at)", body);
    }

    [Fact]
    public void 重新整理後保留同頁已選取事件()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(EventCode), "DoRefresh");
        Assert.NotEqual(string.Empty, body);
        // 以 Id 集合保存並在換 ItemsSource 後重新勾回；未移除不在本頁的 Id 會殘留、未勾回則選取消失。
        Assert.Contains("_selectedIds.RemoveWhere", body);
        Assert.Contains("EventList.SelectedItems.Add", body);
        Assert.Contains("CardList.SelectedItems.Add", body);
    }

    [Fact]
    public void 兩份檢視的選取會互相同步()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(EventCode), "MirrorSelection");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("source.SelectedItems", body);
        Assert.Contains("other.SelectedItems.Clear()", body);
        Assert.Contains("other.SelectedItems.Add", body);
    }

    [Fact]
    public void 未確認徽章入口會放寬時間範圍到全部()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(EventCode), "FocusUnacknowledged");
        Assert.NotEqual(string.Empty, body);
        // 徽章是全庫未確認總數；清單若仍停在預設 24 小時，舊的未確認事件看不到也清不掉，
        // 症狀就是徽章卡住不降。
        Assert.Contains("RangeCombo.SelectedIndex = AllRangeIndex", body);
    }

    [Fact]
    public void 清單查詢會套用僅未確認篩選()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(EventCode), "DoRefresh");
        Assert.NotEqual(string.Empty, body);
        // 「僅未確認」只寫進 CSV 的 BuildQuery、沒進清單查詢的話，勾選完全沒作用，
        // 徽章指的未確認事件會被已確認事件淹沒、分頁推到後面而全選不到。
        Assert.Contains(
            "Status = PendingOnlyCheck.IsChecked == true ? AlarmEventStatus.Pending : null",
            body);
    }

    private static string WiredHandler(string relativePath, string element)
    {
        var xaml = SourceContract.Read(relativePath);
        var tag = Regex.Match(
            xaml,
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;

        Assert.NotEqual(string.Empty, tag);
        var handler = Regex.Match(tag, @"\bClick=""(\w+)""");
        return handler.Success ? handler.Groups[1].Value : string.Empty;
    }
}
