using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 邊緣補抓契約（M94，§14.7 #10）。
///
/// <para>
/// <c>EdgeBackfillJobRepository</c>／<c>EdgeBackfillExecutor</c>／<c>EdgeFfmpegBackfillRunner</c>
/// 全無產品呼叫端，且 <c>DeviceRecord</c> 沒有 SD 網址欄位，runner 根本無從解析來源。
/// 這份契約把主視窗入口、SD 網址設定、建立工作與「下載＋回灌登記」的執行鏈釘住。
/// </para>
/// </summary>
public sealed class EdgeBackfillContractTests
{
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string MainXaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string EdgeCode = "src/HeliVMS.App/EdgeBackfillWindow.xaml.cs";
    private const string EdgeXaml = "src/HeliVMS.App/EdgeBackfillWindow.xaml";

    [Fact]
    public void 主視窗有邊緣補抓入口且接線()
    {
        Assert.Equal("OnEdgeBackfillClicked", WiredHandler(MainXaml, "EdgeBackfillButton"));

        var handler = SourceContract.BodyOf(SourceContract.Read(MainCode), "OnEdgeBackfillClicked");
        Assert.Contains("OpenEdgeBackfillWindow", handler);

        var open = SourceContract.BodyOf(SourceContract.Read(MainCode), "OpenEdgeBackfillWindow");
        Assert.Contains("new EdgeBackfillWindow(", open);
    }

    [Fact]
    public void 邊緣補抓窗設定SD網址並以回灌runner執行()
    {
        var code = SourceContract.Read(EdgeCode);
        Assert.Contains("new EdgeBackfillJobRepository(", code);
        Assert.Contains(".SetSdUrl(", code);
        Assert.Contains("_jobs.Create(", code);
        Assert.Contains("new EdgeFfmpegBackfillRunner(", code);
        Assert.Contains("new SegmentRegisteringEdgeBackfillRunner(", code);
        Assert.Contains("new EdgeBackfillExecutor(", code);
    }

    [Fact]
    public void 邊緣補抓窗有SD欄位與工作清單控制項()
    {
        var xaml = SourceContract.Read(EdgeXaml);
        foreach (var element in new[] { "EdgeDeviceCombo", "EdgeChannelCombo", "EdgeSdUrlBox", "EdgeJobList", "EdgeCreateJobButton", "EdgeRunDueButton" })
        {
            Assert.Contains($"x:Name=\"{element}\"", xaml);
        }
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
