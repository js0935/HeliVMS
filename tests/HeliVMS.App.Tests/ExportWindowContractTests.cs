using Xunit;

namespace HeliVMS.App.Tests;

/// <summary>
/// 匯出精靈（ExportWindow）的版面契約：無邊框最大化視窗必須自帶出口，
/// 輸入欄位不得用固定寬度把翻譯文字／攝影機名稱裁掉。
/// </summary>
public sealed class ExportWindowContractTests
{
    private const string Code = "src/HeliVMS.App/ExportWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/ExportWindow.xaml";

    /// <summary>
    /// 全畫面視窗必須有畫面上的出口：右上角 ✕、按鈕列「關閉」、Esc 三條路。
    /// </summary>
    /// <remarks>
    /// 改成全畫面（WindowStyle=None）之後系統標題列消失，原本 640×560 對話框右下角的
    /// 「關閉」成了唯一出口；無邊框視窗的慣例（Live／EventCenter／排程）是右上角 ✕ ＋ Esc。
    /// 匯出進行中按關閉不得讓 ffmpeg 變孤兒：OnClosing 必須取消 CTS。
    /// </remarks>
    [Fact]
    public void 匯出精靈全畫面且有三條離開路徑()
    {
        var xaml = SourceContract.Read(Xaml);
        var source = SourceContract.Read(Code);

        // 全畫面：無邊框＋最大化（與其他主作業視窗同一版式）。
        Assert.Contains("WindowStyle=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WindowState=\"Maximized\"", xaml, StringComparison.Ordinal);

        // 右上角 ✕ 掛 OnCloseClicked；按鈕列保留標籤版關閉鈕（IsCancel，Esc 也會觸發）。
        Assert.Matches(
            @"<Button[^>]*x:Name=""HeaderCloseButton""[^>]*Click=""OnCloseClicked""",
            xaml);
        Assert.Matches(
            @"<Button[^>]*x:Name=""CloseButton""[^>]*IsCancel=""True""",
            xaml);
        Assert.Contains("Close()", SourceContract.BodyOf(source, "OnCloseClicked"));

        // Esc：PreviewKeyDown 明確處理，聚焦在輸入欄時也離得開。
        var key = SourceContract.BodyOf(source, "OnPreviewKeyDown");
        Assert.Contains("Key.Escape", key);
        Assert.Contains("Close()", key);
        Assert.Contains(@"PreviewKeyDown=""OnPreviewKeyDown""", xaml, StringComparison.Ordinal);

        // 匯出進行中關閉＝取消，不是留一個跑不完的 ffmpeg。
        Assert.Contains("_cts.Cancel()", SourceContract.BodyOf(source, "OnClosing"));
    }

    /// <summary>
    /// 輸入欄位不得再掛固定寬度：頻道名稱與翻譯標籤比 280px 長時，舊版直接裁掉字尾，
    /// 操作員看不出自己選的是哪台攝影機。
    /// </summary>
    /// <remarks>
    /// 版面改為全畫面雙欄之後，欄位一律填滿左欄（距離表單邊界 16px）；
    /// 時間範圍用「起／迄」兩列的 2×2 日期＋時間，不再讓四個控制項擠同一列。
    /// </remarks>
    [Fact]
    public void 匯出欄位不再用固定寬度裁掉文字()
    {
        var xaml = SourceContract.Read(Xaml);

        // 頻道下拉與浮水印輸入：跟著左欄伸展，不得回歸固定寬度。
        Assert.DoesNotMatch(@"x:Name=""ChannelCombo""[^>]*Width=""", xaml);
        Assert.DoesNotMatch(@"x:Name=""WatermarkBox""[^>]*Width=""", xaml);
        Assert.DoesNotMatch(@"x:Name=""BundlePasswordBox""[^>]*Width=""", xaml);
        Assert.DoesNotMatch(@"x:Name=""BundleExpiryBox""[^>]*Width=""", xaml);

        // 起迄各自一行、有標籤（舊版靠「～」分隔，縮起來就分不清哪邊是起點）。
        Assert.Contains("x:Name=\"ExportStartLabel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ExportEndLabel\"", xaml, StringComparison.Ordinal);

        // 狀態／結果搬到右欄狀態面板：長的 SHA-256 與證據包訊息有完整的換行空間。
        Assert.Contains("x:Name=\"ExportStatusHeader\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ResultText\"", xaml, StringComparison.Ordinal);

        // 起迄 2×2：日期欄 150、時間欄 92。日期欄曾用星號吃滿左欄（看起來很大），
        // 時間框只剩 72px，「23:59:59」被裁掉——現場回報「日期很大、時間太小」。
        // 欄位定義與元素寬度一起釘：只釘元素寬度時，欄寬回歸星號不會被發現。
        Assert.Contains("<ColumnDefinition Width=\"150\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"92\"/>", xaml, StringComparison.Ordinal);
        Assert.Matches(@"x:Name=""StartDate""[^>]*Width=""150""", xaml);
        Assert.Matches(@"x:Name=""EndDate""[^>]*Width=""150""", xaml);
        Assert.Matches(@"x:Name=""StartTime""[^>]*Width=""92""", xaml);
        Assert.Matches(@"x:Name=""EndTime""[^>]*Width=""92""", xaml);
        Assert.DoesNotMatch(@"x:Name=""StartTime""[^>]*Width=""72""", xaml);
    }
}
