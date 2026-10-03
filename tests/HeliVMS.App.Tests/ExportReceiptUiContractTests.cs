using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 匯出收據／簽章驗證 UI 契約（M241，§14.3(2)）。
///
/// <para>
/// 後端 <c>ExportReceiptService</c> 早就有簽章收據與金鑰換發，桌面端卻完全沒有入口：
/// 匯出中心的「驗證」只比 SHA-256，簽章與簽署者指紋從未在 UI 呈現。這裡把關三件事：
/// 驗證要一併驗收據／簽章、要有簽章金鑰的入口、且換發金鑰只有管理員能碰。
/// </para>
/// </summary>
public sealed class ExportReceiptUiContractTests
{
    private const string CenterCode = "src/HeliVMS.App/ExportCenterWindow.xaml.cs";
    private const string CenterXaml = "src/HeliVMS.App/ExportCenterWindow.xaml";
    private const string KeysCode = "src/HeliVMS.App/SigningKeysWindow.xaml.cs";
    private const string KeysXaml = "src/HeliVMS.App/SigningKeysWindow.xaml";

    [Fact]
    public void 匯出中心驗證會一併驗簽章收據()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(CenterCode), "OnVerifyClicked");
        Assert.NotEqual(string.Empty, body);

        // 只比 SHA-256 擋不住「用另一把金鑰重簽」；一定要走 ExportReceiptService.Verify。
        Assert.Contains("new ExportReceiptService(_store).Verify(", body);
    }

    [Fact]
    public void 匯出中心有簽章金鑰入口且接線()
    {
        Assert.Equal("OnSigningKeysClicked", WiredHandler(CenterXaml, "CenterSigningKeysButton"));
        Assert.Contains("new SigningKeysWindow(_store)", SourceContract.Read(CenterCode));
    }

    [Fact]
    public void 簽章金鑰入口僅限管理員()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(CenterCode), "OnSigningKeysClicked");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("SessionContext.IsAdmin", body);
    }

    [Fact]
    public void 簽章金鑰視窗列出歷史並採兩段式換發()
    {
        var code = SourceContract.Read(KeysCode);
        Assert.Contains("SigningKeyHistory()", code);

        var rotate = SourceContract.BodyOf(code, "OnRotateClicked");
        Assert.NotEqual(string.Empty, rotate);
        // 換發不可逆：第一次只佈署，第二次才真的換。
        Assert.Contains("_rotateArm", rotate);
        Assert.Contains("RotateSigningKey(", rotate);
        Assert.Equal("OnRotateClicked", WiredHandler(KeysXaml, "RotateKeyButton"));
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
