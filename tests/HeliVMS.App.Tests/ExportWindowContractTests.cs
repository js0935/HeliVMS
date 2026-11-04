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

        // 起迄 2×2：日期欄 170、時間欄 116。日期欄曾用星號吃滿左欄（看起來很大），
        // 時間框只剩 72px，「23:59:59」被裁掉；連 92px 在預設系統字級下都還很緊
        //（現場回報「時間欄位文字顯示不完整」），所以時間欄再放寬到 116。
        // 欄位定義與元素寬度一起釘：只釘元素寬度時，欄寬回歸星號不會被發現。
        Assert.Contains("<ColumnDefinition Width=\"170\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"116\"/>", xaml, StringComparison.Ordinal);
        Assert.Matches(@"x:Name=""StartDate""[^>]*Width=""170""", xaml);
        Assert.Matches(@"x:Name=""EndDate""[^>]*Width=""170""", xaml);
        Assert.Matches(@"x:Name=""StartTime""[^>]*Width=""116""", xaml);
        Assert.Matches(@"x:Name=""EndTime""[^>]*Width=""116""", xaml);

        // 更窄的舊寬度都不得回歸：72 是原始值、92 是「還是太緊」的那一版。
        Assert.DoesNotMatch(@"x:Name=""StartTime""[^>]*Width=""72""", xaml);
        Assert.DoesNotMatch(@"x:Name=""StartTime""[^>]*Width=""92""", xaml);
    }

    /// <summary>
    /// 時刻讀不懂就擋下來，**不得**默默退回一整天。
    /// </summary>
    /// <remarks>
    /// 現場回報：設定 11040725～11040730（要 5 秒）卻匯出 50 分鐘的檔案。原因是舊版用
    /// <c>TimeSpan.TryParse</c> 解析，讀不懂時默默把起訖當成 00:00–23:59:59，於是整天的錄影
    /// 都被併進成品——而成品是有紀錄價值的檔案，操作員不會知道內容不是他要的。
    /// </remarks>
    [Fact]
    public void 時刻讀不懂會擋下來而不是默默匯出一整天()
    {
        var source = SourceContract.Read(Code);
        var body = SourceContract.BodyOf(source, "OnExportClicked");

        // 讀得懂現場寫法（11040725＝11:04:07.25，與錄影檔名同一種）。
        Assert.Contains("ClockTime.TryParse(StartTime.Text", body);
        Assert.Contains("ClockTime.TryParse(EndTime.Text", body);
        // 讀不懂要說出來。
        Assert.Contains("Export.TimeInvalid", body);
        // 舊版的靜默退路不得回來。
        Assert.DoesNotContain("TimeSpan.Zero", body);
        Assert.DoesNotContain("new TimeSpan(23, 59, 59)", body);
    }

    /// <summary>由回放帶入的區間精確到秒（只寫到分會讓兩端各伸縮最多 59 秒）。</summary>
    [Fact]
    public void 由回放帶入的區間精確到秒()
    {
        var source = SourceContract.Read(Code);
        var ctor = SourceContract.BodyOf(source, "ExportWindow");

        Assert.Contains("HH:mm:ss", ctor);
        Assert.DoesNotContain("\"HH:mm\"", ctor);

        // 時刻欄位要說明可接受的寫法（標籤只有「起」「迄」，塞不下格式）。
        var i18n = SourceContract.BodyOf(source, "ApplyI18n");
        Assert.Contains("Export.TimeTip", i18n);
    }

    /// <summary>新增的時刻文案三語都在，且錯誤訊息帶得出打錯的字串。</summary>
    [Fact]
    public void 時刻文案三語都在且錯誤訊息帶得出輸入值()
    {
        var i18n = SourceContract.Read("src/HeliVMS.Storage/I18n.cs");

        foreach (var key in new[] { "Export.TimeInvalid", "Export.TimeTip" })
        {
            var lines = i18n.Split('\n')
                .Where(line => line.Contains($"\"{key}\"", StringComparison.Ordinal))
                .ToList();
            Assert.True(lines.Count == 3, $"{key} 應該有三語，實際 {lines.Count}");
        }

        var invalid = i18n.Split('\n')
            .Where(line => line.Contains("\"Export.TimeInvalid\"", StringComparison.Ordinal));
        Assert.All(invalid, line => Assert.Contains("{0}", line));
    }
}
