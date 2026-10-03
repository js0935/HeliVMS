using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 稽核日誌視窗契約（M109／§14.1）。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試，這裡把住三件事：匯出必須走共用的 <c>AuditLogCsv.Render</c>
/// （與 <c>/api/audit/export.csv</c> 同一份，否則兩邊格式會漂移而無法對帳）、
/// 匯出必須以 UTF-8 BOM 寫檔（Excel 才不會把中文顯示成亂碼）、
/// 以及查詢與匯出都真的呼叫 <c>AuditLogRepository</c>（不是只渲染空清單）。
/// </para>
/// </summary>
public sealed class AuditWindowContractTests
{
    private const string Code = "src/HeliVMS.App/AuditLogWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/AuditLogWindow.xaml";

    [Fact]
    public void 查詢與匯出按鈕有接線到處理器()
    {
        Assert.Equal("OnQueryClicked", WiredHandler("AuditQueryButton"));
        Assert.Equal("OnExportClicked", WiredHandler("AuditExportButton"));
    }

    [Fact]
    public void 清單與狀態文字有具名控制項()
    {
        Assert.Contains("x:Name=\"AuditList\"", SourceContract.Read(Xaml));
        Assert.Contains("x:Name=\"AuditStatusText\"", SourceContract.Read(Xaml));
    }

    [Fact]
    public void 匯出使用共用CSV產生器()
    {
        var body = BodyOf("OnExportClicked");

        Assert.Contains("AuditLogCsv.Render(", body);
        Assert.Contains("UTF8Encoding(true)", body);
    }

    [Fact]
    public void 查詢會走稽核倉儲並顯示筆數()
    {
        var body = BodyOf("Query");

        Assert.Contains("_audit.List(", body);
        Assert.Contains("_audit.Count(", body);
    }

    private static string BodyOf(string method)
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), method);
        Assert.NotEqual(string.Empty, body);
        return body;
    }

    private static string WiredHandler(string element)
    {
        var xaml = SourceContract.Read(Xaml);
        var tag = Regex.Match(
            xaml,
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;

        Assert.NotEqual(string.Empty, tag);
        var handler = Regex.Match(tag, @"\bClick=""(\w+)""");
        return handler.Success ? handler.Groups[1].Value : string.Empty;
    }
}
