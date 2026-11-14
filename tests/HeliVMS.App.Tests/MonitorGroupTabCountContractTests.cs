namespace HeliVMS.App.Tests;

/// <summary>
/// 監控中心頁籤必須顯示<b>台數</b>。
///
/// <para>
/// 症狀：切頁籤時不知道每個群組有幾台；「64 分割看 2 台的群組」是浪費，而且正是
/// 先前「群組 2 台用 4 分割、切換後空格串著上一頁簽的畫面」案例的溫床——台數
/// 讓「該用幾分割」在切過去之前就看得到。
/// </para>
/// <para>
/// 釘兩件容易做錯的事：台數的來源是<b>資料庫</b>而不是 <c>_channelList</c>
/// （載入初期頁簽先於頻道清單建立，用後者每個頁籤都會是 0），以及成員要過濾
/// 已刪除的頻道（與 <c>WallChannels</c> 同一口徑，否則頁籤的數字會比實際能看的路數多）。
/// </para>
/// </summary>
public sealed class MonitorGroupTabCountContractTests
{
    private const string Code = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string I18n = "src/HeliVMS.Storage/I18n.cs";

    [Fact]
    public void 頁籤標題帶台數且組字經過共用格式()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "ReloadGroupTabs");

        Assert.Contains("TabHeader(", body);
        Assert.Contains("Main.TabCountFormat", SourceContract.BodyOf(SourceContract.Read(Code), "TabHeader"));
        // 「全部」與群組都要有台數：只給群組加的話，操作員最常看的「全部」反而沒有。
        Assert.Contains("TabHeader(Localizer.T(\"Main.GroupAll\")", body);
        Assert.Contains("ListChannelIds(group.Id).Count(", body);
    }

    [Fact]
    public void 台數取自資料庫而不是載入初期的空清單()
    {
        // ReloadGroupTabs 在 OnLoaded 裡跑在 RefreshChannelList 之前：那時 _channelList
        // 還是空的，拿它算台數會讓每個頁籤都顯示（0），而且重開每次都如此。
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "ReloadGroupTabs");

        Assert.Contains("_channels?.List()", body);
        // 成員過濾與 WallChannels 同一口徑：群組指到已刪除頻道時，數字不得比能看的路數多。
        Assert.Contains("knownIds.Contains", body);
    }

    [Fact]
    public void 台數格式三語都在且帶位數()
    {
        var i18n = SourceContract.Read(I18n);

        var lines = i18n.Split('\n')
            .Where(line => line.Contains("\"Main.TabCountFormat\"", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.Contains("{0}", line));
    }
}
