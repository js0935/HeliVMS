namespace HeliVMS.App.Tests;

/// <summary>
/// 影片摘要外部縫的桌面接線契約（M294 §14.1 #8）：<c>SynopsisWindow</c> 只依賴
/// <c>IVideoSummaryProvider</c>，不得直接呼叫 <c>SynopsisBuilder.Build</c>——否則真機換上模型實作時，
/// 這條捷徑會讓摘要仍走舊拼貼，而其他測試不會有任何反應。
/// </summary>
public sealed class SynopsisProviderContractTests
{
    [Fact]
    public void 影片摘要視窗只依賴供應商抽象()
    {
        var source = SourceContract.Read("src/HeliVMS.App/SynopsisWindow.xaml.cs");

        Assert.Contains("IVideoSummaryProvider? summaryProvider = null", source);
        Assert.Contains("_summaryProvider.Generate(", source);
        Assert.DoesNotContain("SynopsisBuilder.Build(", source);
    }

    [Fact]
    public void 影片摘要視窗預設使用拼貼後備()
    {
        var source = SourceContract.Read("src/HeliVMS.App/SynopsisWindow.xaml.cs");
        Assert.Contains("CollageVideoSummaryProvider.Default", source);
    }
}
