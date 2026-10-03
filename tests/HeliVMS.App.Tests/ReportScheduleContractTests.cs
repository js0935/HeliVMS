using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 報表排程與趨勢圖契約（§14.1 #9）。
///
/// <para>
/// 排程判斷本身在 <c>HeliVMS.Storage.ReportSchedule</c>／<c>HeliVMS.Alarms.ReportMailer</c>
/// 以單元測試驗證；這份契約只釘住桌面端是否真的把它接起來：
/// 設定 UI、寫回 app_settings、背景計時器、以及匯出／寄送共用 <c>ReportCsv</c>。
/// 少了任一接線，功能會「邏輯正確但沒人呼叫」，不會有任何既有測試變紅。
/// </para>
/// </summary>
public sealed class ReportScheduleContractTests
{
    private const string ReportsXaml = "src/HeliVMS.App/ReportsWindow.xaml";
    private const string ReportsCode = "src/HeliVMS.App/ReportsWindow.xaml.cs";
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";

    [Fact]
    public void 報表視窗有排程設定與趨勢圖控制項()
    {
        var xaml = SourceContract.Read(ReportsXaml);

        foreach (var element in new[]
                 {
                     "ReportScheduleCheck", "ReportCadenceCombo", "ReportWeeklyDayCombo",
                     "ReportScheduleTimeBox", "ReportScheduleSaveButton",
                     "ReportScheduleStatusText", "ReportTrendCanvas",
                 })
        {
            Assert.Contains($"x:Name=\"{element}\"", xaml);
        }

        Assert.Equal("OnSaveScheduleClicked", WiredHandler(ReportsXaml, "ReportScheduleSaveButton"));
    }

    [Fact]
    public void 儲存排程會寫回設定鍵()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(ReportsCode), "OnSaveScheduleClicked");

        Assert.Contains("ReportSchedule.EnabledKey", handler);
        Assert.Contains("ReportSchedule.CadenceKey", handler);
        Assert.Contains("ReportSchedule.TimeKey", handler);
        Assert.Contains("_settings.Set", handler);
    }

    [Fact]
    public void 匯出與排程共用ReportCsv產生器()
    {
        // 手動匯出／寄送（UI）與背景排程（Alarms）都必須呼叫同一個 ReportCsv.Build，
        // 否則兩邊欄位順序會各自漂移。
        Assert.Contains(
            "ReportCsv.Build",
            SourceContract.BodyOf(SourceContract.Read(ReportsCode), "WriteReportCsv"));
        Assert.Contains(
            "ReportCsv.Build",
            SourceContract.Read("src/HeliVMS.Alarms/ReportMailer.cs"));
    }

    [Fact]
    public void 主視窗以計時器觸發排程寄送()
    {
        var code = SourceContract.Read(MainCode);

        Assert.Contains("_reportMailTimer = new DispatcherTimer", code);
        Assert.Contains("RunReportMailDue", code);

        var due = SourceContract.BodyOf(code, "RunReportMailDue");
        Assert.Contains("new ReportMailer(", due);
        Assert.Contains("RunIfDueAsync", due);
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
