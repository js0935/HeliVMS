namespace HeliVMS.App.Tests;

/// <summary>
/// 以圖搜索的接線：樣本與事件快照要走<b>同一條</b>雜湊管線，且索引的補算不能卡住。
/// </summary>
/// <remarks>
/// <para>
/// 桌面端沒有 WPF 執行期測試，所以這裡掃原始碼；管線本身是行為測試
/// （<c>HeliVMS.Shared.Tests.VisualHashTests</c>）。
/// </para>
/// <para>
/// 這裡守的是兩件在現場看不出來的事：①服務若自己再寫一份灰階公式，樣本與快照的指紋就會用
/// 兩套定義算出來，漢明距離變成亂數——搜索結果「有東西」但都不像；②解不開的快照若沒有被標成 0，
/// 它會永遠留在補算清單最前面，每一批 200 筆都花在同一個壞檔上，新事件永遠補不到。
/// </para>
/// </remarks>
public sealed class VisualSearchContractTests
{
    private const string Service = "src/HeliVMS.App/Services/VisualSearchService.cs";

    [Fact]
    public void 灰階與雜湊都走共用的VisualHash管線()
    {
        var code = SourceContract.Read(Service);

        Assert.Contains("VisualHash.FromBgra(", code);
        Assert.Contains("VisualHash.IndexWidth", code);
        Assert.Contains("VisualHash.IndexHeight", code);

        // 自己再算一次灰階就是第二份定義；係數或四捨五入只要差一點，指紋就整組不同。
        Assert.DoesNotContain("299", code);
        Assert.DoesNotContain("587", code);
        Assert.DoesNotContain("FromGrayImage(", code);
    }

    [Fact]
    public void 解不開的快照會標成零並退出補算清單()
    {
        var backfill = SourceContract.BodyOf(SourceContract.Read(Service), "BackfillAsync");

        Assert.Contains("SetSnapshotHash(id, 0)", backfill);
        Assert.Contains("SetSnapshotHash(id, h)", backfill);
    }

    [Fact]
    public void 搜尋前會先補算一批索引()
    {
        // 索引是懶計算的：不先補算，剛寫進來的事件（也就是操作員最想找的「剛剛那個人」）
        // 永遠不在候選集合裡，而以圖搜索看起來「只能找到舊畫面」。
        var search = SourceContract.BodyOf(SourceContract.Read(Service), "SearchAsync");

        Assert.Contains("BackfillAsync(", search);
        Assert.Contains("ListHashCandidates(", search);
        Assert.Contains("VisualSearchRank.Rank(", search);
    }
}
