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

    [Fact]
    public void 框選搜索對候選快照以同一個相對框重算雜湊()
    {
        // 現場症狀：框了畫面裡的一個門，搜出來的卻是完全不相干的事件（或什麼都沒有）。
        // 原因是索引的 snapshot_hash 是「整張圖」的指紋，與框內樣本是兩種東西。
        // 所以這裡守兩件事：候選必須是「有快照的列」，且雜湊必須用同一個 roi 重算。
        var code = SourceContract.Read(Service);
        var region = SourceContract.BodyOf(code, "SearchInRegionAsync");
        var hash = SourceContract.BodyOf(code, "TryHashRegion");

        Assert.Contains("ListSnapshotCandidates(", region);
        Assert.Contains("HashRegions(", region);
        Assert.Contains("_decodeScaled(", hash);
        Assert.Contains("VisualHash.FromBgra(", hash);
        Assert.Contains("roi", hash);
    }

    [Fact]
    public void 事件中心把框選區域交給搜索服務()
    {
        // 只加了框選視窗而沒把 roi 傳進 SearchAsync，等於按了框選卻還是搜整張圖：
        // 畫面看起來完全正常，結果卻與框選無關。
        var code = SourceContract.Read("src/HeliVMS.App/EventCenterWindow.xaml.cs");
        var click = SourceContract.BodyOf(code, "OnVisualRoiClicked");
        var search = SourceContract.BodyOf(code, "OnVisualSearchClicked");

        Assert.Contains("new VisualRoiWindow(", click);
        // 呼叫本身必須把框當第三個參數傳進去：只宣告 roi 而不傳，等於按了框選仍搜整張圖。
        Assert.Matches(@"SearchAsync\s*\(\s*sample\s*,\s*[^,)]+,\s*roi\s*\)", search);
    }

    [Fact]
    public void 框選視窗以影像像素換算並畫回顯示座標()
    {
        // 影像用 Stretch=Uniform（有黑邊）：把顯示座標直接當像素用時，非原生解析度或
        // 非等比例視窗會框到完全不同的一塊，而框看起來就畫在操作員拖的位置上。
        var code = SourceContract.Read("src/HeliVMS.App/VisualRoiWindow.xaml.cs");

        Assert.Contains("VisualHash.FromPixels(", code);
        Assert.Contains("VisualHash.PixelRect(", code);
        Assert.Contains("Stretch=\"Uniform\"", SourceContract.Read("src/HeliVMS.App/VisualRoiWindow.xaml"));
    }
}
