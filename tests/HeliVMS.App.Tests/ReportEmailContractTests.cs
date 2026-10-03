using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 報表寄送契約（§14.1 #9）。
///
/// <para>
/// 報表視窗原本只能匯出 CSV 到本機。通知中心早已有可用的 SMTP 設定與 <c>SmtpNotifier</c>，
/// 但兩者從未接上——營運得手動把 CSV 寄給主管。這份契約把「報表 → SMTP」的接線釘住，
/// 並確認匯出與寄送走同一個 CSV 產生器，避免兩份欄位順序日後各自漂移。
/// </para>
/// </summary>
public sealed class ReportEmailContractTests
{
    private const string ReportsXaml = "src/HeliVMS.App/ReportsWindow.xaml";
    private const string ReportsCode = "src/HeliVMS.App/ReportsWindow.xaml.cs";

    [Fact]
    public void 報表視窗有寄送按鈕且接線()
    {
        Assert.Equal("OnEmailClicked", WiredHandler(ReportsXaml, "ReportEmailButton"));
    }

    [Fact]
    public void 寄送走通知中心的SMTP設定與寄送器()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(ReportsCode), "OnEmailClicked");

        Assert.Contains("NotificationSettings.Load", handler);
        Assert.Contains("SendReportAsync", handler);
        Assert.Contains("new SmtpNotifier()", handler);
    }

    [Fact]
    public void 匯出與寄送共用同一個CSV產生器()
    {
        var code = SourceContract.Read(ReportsCode);

        Assert.Contains("WriteReportCsv", SourceContract.BodyOf(code, "OnExportClicked"));
        Assert.Contains("WriteReportCsv", SourceContract.BodyOf(code, "OnEmailClicked"));
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
