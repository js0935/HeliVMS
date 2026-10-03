using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 智慧牆視窗契約（M105/M106，§14.7 #14）。
///
/// <para>
/// 版面倉儲、網格校驗與警報牆快照引擎都已完成，Web 也已有 /api/smartwall/board，
/// 但桌面端先前完全沒有入口——「視訊牆 UI/派送掛載待續」指的就是這個。
/// 這份契約把主視窗入口與智慧牆窗對倉儲、看板引擎的接線釘住。
/// </para>
/// </summary>
public sealed class SmartwallContractTests
{
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string MainXaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string WallCode = "src/HeliVMS.App/SmartwallWindow.xaml.cs";
    private const string WallXaml = "src/HeliVMS.App/SmartwallWindow.xaml";

    [Fact]
    public void 主視窗有智慧牆入口且接線()
    {
        Assert.Equal("OnSmartwallClicked", WiredHandler(MainXaml, "SmartwallButton"));

        var handler = SourceContract.BodyOf(SourceContract.Read(MainCode), "OnSmartwallClicked");
        Assert.Contains("OpenSmartwallWindow", handler);

        var open = SourceContract.BodyOf(SourceContract.Read(MainCode), "OpenSmartwallWindow");
        Assert.Contains("new SmartwallWindow(", open);
    }

    [Fact]
    public void 智慧牆窗走版面倉儲與看板引擎()
    {
        var code = SourceContract.Read(WallCode);
        Assert.Contains("new SmartwallLayoutRepository(", code);
        Assert.Contains("SmartwallAlertBoard.Snapshot(", code);
        Assert.Contains(".AddTile(", code);
        Assert.Contains(".RemoveTile(", code);
    }

    [Fact]
    public void 智慧牆窗有版面與看板控制項()
    {
        var xaml = SourceContract.Read(WallXaml);
        foreach (var element in new[]
        {
            "LayoutList", "SmartwallGrid", "BoardList",
            "CreateLayoutButton", "AddTileButton", "RemoveTileButton",
        })
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
