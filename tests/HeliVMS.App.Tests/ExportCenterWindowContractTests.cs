using Xunit;

namespace HeliVMS.App.Tests;

/// <summary>
/// 匯出中心（<c>ExportCenterWindow</c>）的版面契約：與匯出精靈同一套規則——
/// 無邊框最大化必須自帶出口，輸入欄位不得用固定寬度把翻譯文字或時間裁掉。
/// </summary>
public sealed class ExportCenterWindowContractTests
{
    private const string Code = "src/HeliVMS.App/ExportCenterWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/ExportCenterWindow.xaml";

    /// <summary>
    /// 全畫面視窗必須有畫面上的出口：右上角 ✕、按鈕列「關閉」、Esc 三條路。
    /// </summary>
    /// <remarks>
    /// 原本是 760×640 的系統標題列視窗；改成全畫面（WindowStyle=None）之後標題列消失，
    /// 沒有關閉鈕就只能 Alt+F4。另外關閉時必須停掉每 2 秒刷新的計時器——那是既有行為，
    /// 一併釘住（否則每開一次就留一個視窗實體在 UI 執行緒上刷新已關閉的視窗）。
    /// </remarks>
    [Fact]
    public void 匯出中心全畫面且有三條離開路徑()
    {
        var xaml = SourceContract.Read(Xaml);
        var source = SourceContract.Read(Code);

        Assert.Contains("WindowStyle=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WindowState=\"Maximized\"", xaml, StringComparison.Ordinal);

        Assert.Matches(
            @"<Button[^>]*x:Name=""HeaderCloseButton""[^>]*Click=""OnCloseClicked""",
            xaml);
        Assert.Matches(
            @"<Button[^>]*x:Name=""CloseButton""[^>]*IsCancel=""True""",
            xaml);
        Assert.Contains("Close()", SourceContract.BodyOf(source, "OnCloseClicked"));

        var key = SourceContract.BodyOf(source, "OnPreviewKeyDown");
        Assert.Contains("Key.Escape", key);
        Assert.Contains("Close()", key);
        Assert.Contains(@"PreviewKeyDown=""OnPreviewKeyDown""", xaml, StringComparison.Ordinal);

        Assert.Contains("_timer.Stop()", source);
    }

    /// <summary>
    /// 時間欄必須放得下「23:59:59」：舊版起迄時間框只有 64px，一定裁字。
    /// </summary>
    /// <remarks>
    /// 與匯出精靈同一組尺寸（日期 170、時間 116），而且日期與時間都要有「起／迄」標籤——
    /// 舊版靠中間的「～」分隔，欄位一縮就分不清哪邊是起點。
    /// </remarks>
    [Fact]
    public void 匯出中心的時間欄位不會裁掉文字()
    {
        var xaml = SourceContract.Read(Xaml);

        Assert.Contains("<ColumnDefinition Width=\"170\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"116\"/>", xaml, StringComparison.Ordinal);
        Assert.Matches(@"x:Name=""StartTime""[^>]*Width=""116""", xaml);
        Assert.Matches(@"x:Name=""EndTime""[^>]*Width=""116""", xaml);
        Assert.Matches(@"x:Name=""StartDate""[^>]*Width=""170""", xaml);
        Assert.Matches(@"x:Name=""EndDate""[^>]*Width=""170""", xaml);

        // 更窄的舊值不得回歸（64 是原始值）。
        Assert.DoesNotMatch(@"x:Name=""StartTime""[^>]*Width=""64""", xaml);
        Assert.DoesNotMatch(@"x:Name=""EndTime""[^>]*Width=""64""", xaml);

        // 起迄各有標籤，不再靠「～」分隔。
        Assert.Contains("x:Name=\"ExportCenterStartLabel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ExportCenterEndLabel\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("～", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// 按鈕與輸出資料夾欄位不得用固定寬度：翻譯後的標籤比固定寬度長時會被裁掉
    /// （例：開啟資料夾、簽章金鑰）。
    /// </summary>
    [Fact]
    public void 匯出中心的按鈕與欄位不再固定寬度()
    {
        var xaml = SourceContract.Read(Xaml);

        foreach (var name in new[]
                 {
                     "CenterRefreshButton", "CenterRunButton", "CenterVerifyButton",
                     "CenterSigningKeysButton", "CenterDeleteButton", "CenterRetryButton",
                     "CenterOpenFolderButton", "CenterPurgeButton", "ShareFromExportButton",
                     "AddExportButton", "CenterBrowseButton",
                 })
        {
            // 只用 MinWidth（跟著內容長大）；（?<!Min) 讓 MinWidth 不算違規。
            Assert.DoesNotMatch($@"x:Name=""{name}""[^>]*(?<!Min)Width=""", xaml);
        }

        // 輸出資料夾欄位填滿列寬（原本固定 420）。
        Assert.DoesNotMatch(@"x:Name=""CenterOutputBox""[^>]*(?<!Min)Width=""", xaml);
    }
}
