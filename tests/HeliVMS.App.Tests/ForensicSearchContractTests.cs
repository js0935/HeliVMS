using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 法證跨來源檢索 UI 契約（M97，§14.7 #7）。
///
/// <para>
/// 後端 <c>UnifiedEventSearch</c> 早就把警報、門禁、POS、邊緣 AI 四個 FTS 索引併成
/// 一次查詢，桌面端卻只有事件中心（僅 <c>alarm_events</c>）——「一個關鍵字找遍全系統」
/// 這個能力在 WPF 完全沒有入口。這份契約把主視窗入口與檢索窗對後端的接線釘住。
/// </para>
/// </summary>
public sealed class ForensicSearchContractTests
{
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string MainXaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string SearchCode = "src/HeliVMS.App/SearchWindow.xaml.cs";
    private const string SearchXaml = "src/HeliVMS.App/SearchWindow.xaml";

    [Fact]
    public void 主視窗有法證檢索入口且接線()
    {
        Assert.Equal("OnSearchClicked", WiredHandler(MainXaml, "SearchButton"));

        var handler = SourceContract.BodyOf(SourceContract.Read(MainCode), "OnSearchClicked");
        Assert.Contains("OpenSearchWindow", handler);

        var open = SourceContract.BodyOf(SourceContract.Read(MainCode), "OpenSearchWindow");
        Assert.Contains("new SearchWindow(", open);
    }

    [Fact]
    public void 檢索窗走跨來源搜尋後端()
    {
        var code = SourceContract.Read(SearchCode);
        Assert.Contains("new UnifiedEventSearch(", code);
        Assert.Contains(".Search(", code);
    }

    [Fact]
    public void 檢索窗可重建索引()
    {
        var code = SourceContract.Read(SearchCode);
        var rebuild = SourceContract.BodyOf(code, "OnRebuildClicked");
        Assert.NotEqual(string.Empty, rebuild);
        Assert.Contains("RebuildAll()", rebuild);
        Assert.Equal("OnRebuildClicked", WiredHandler(SearchXaml, "SearchRebuildButton"));
    }

    [Fact]
    public void 四種來源都可個別勾選()
    {
        var xaml = SourceContract.Read(SearchXaml);
        foreach (var element in new[] { "SearchAlarmCheck", "SearchDoorCheck", "SearchPosCheck", "SearchEdgeCheck" })
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
