using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// POS 交易視窗契約（M93/M104/M113，§14.7 #8）。
///
/// <para>
/// 後端已有去重匯入（<c>InsertDedupe</c>）、收銀機查詢（<c>QueryByRegister</c>）與對帳
/// （<c>PosReconciliation</c>），但全 repo 沒有任何產品呼叫端——POS 能力在桌面等於不存在。
/// 這份契約把主視窗入口與 POS 窗對這些後端 API 的接線釘住。
/// </para>
/// </summary>
public sealed class PosContractTests
{
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string MainXaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string PosCode = "src/HeliVMS.App/PosWindow.xaml.cs";
    private const string PosXaml = "src/HeliVMS.App/PosWindow.xaml";

    [Fact]
    public void 主視窗有POS入口且接線()
    {
        Assert.Equal("OnPosClicked", WiredHandler(MainXaml, "PosButton"));

        var handler = SourceContract.BodyOf(SourceContract.Read(MainCode), "OnPosClicked");
        Assert.Contains("OpenPosWindow", handler);

        var open = SourceContract.BodyOf(SourceContract.Read(MainCode), "OpenPosWindow");
        Assert.Contains("new PosWindow(", open);
    }

    [Fact]
    public void POS窗走去重匯入與對帳後端()
    {
        var code = SourceContract.Read(PosCode);
        Assert.Contains("new POSEventRepository(", code);
        Assert.Contains(".InsertDedupe(", code);
        Assert.Contains(".QueryByRegister(", code);
        Assert.Contains("PosReconciliation.Compute(", code);
    }

    [Fact]
    public void POS窗有查詢匯入對帳控制項()
    {
        var xaml = SourceContract.Read(PosXaml);
        foreach (var element in new[] { "PosList", "PosQueryButton", "PosImportButton", "PosReconcileButton" })
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
