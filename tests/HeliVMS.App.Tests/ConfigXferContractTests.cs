namespace HeliVMS.App.Tests;

/// <summary>
/// 設定檔匯出／匯入的接線契約：設定中心的備份頁要提供匯出／匯入、匯入前要有摘要確認、
/// 匯入後要問重啟；重啟必須在單一實體守衛釋放「之後」拉起新行程（太早拉會被守衛
/// 當成重複啟動，喚醒一個正在關閉的行程，結果兩邊都不在）。來源掃描是既有慣例。
/// </summary>
public sealed class ConfigXferContractTests
{
    private const string Settings = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string App = "src/HeliVMS.App/App.xaml.cs";
    private const string Service = "src/HeliVMS.Storage/SettingsBackupService.cs";

    [Fact]
    public void 設定中心提供設定檔的匯出與匯入()
    {
        var source = SourceContract.Read(Settings);
        Assert.Contains("OnConfigExportClicked", source);
        Assert.Contains("OnConfigImportClicked", source);
        Assert.Contains("new SettingsBackupService(", source);
        Assert.Contains("ExportTo(", source);
        Assert.Contains("ImportFrom(", source);

        // 位置契約：區塊在「關於」頁（PageAbout 之後），不在備份頁——備份頁管存檔複製，
        // 設定遷移是另一件事，混在一起操作員會把「備份錄影」與「取代設定」搞混。
        var xaml = SourceContract.Read("src/HeliVMS.App/SettingsWindow.xaml");
        var aboutAt = xaml.IndexOf("x:Name=\"PageAbout\"", StringComparison.Ordinal);
        var configAt = xaml.IndexOf("ConfigXferHeading", StringComparison.Ordinal);
        var backupEndAt = xaml.IndexOf("x:Name=\"PageNetwork\"", StringComparison.Ordinal);
        Assert.True(aboutAt >= 0 && configAt > aboutAt, "設定匯出／匯入區塊應在關於頁");
        Assert.True(backupEndAt >= 0 && configAt > backupEndAt, "設定匯出／匯入區塊不應留在備份頁");
    }

    [Fact]
    public void 匯入前先讀摘要讓操作員確認()
    {
        // 沒有確認框的話，點錯檔案會直接清掉現場設定——這是整個功能裡唯一會破壞現況的操作。
        var body = SourceContract.BodyOf(SourceContract.Read(Settings), "OnConfigImportClicked");
        Assert.Contains("ReadSummary", body);
        Assert.Contains("MessageBox.Show", body);
        Assert.Contains("ConfigImportConfirm", body);
    }

    [Fact]
    public void 匯入後會詢問重啟且走既有的收尾路徑()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Settings), "OnConfigImportClicked");
        Assert.Contains("RestartOnExit", body);
        Assert.Contains("MainWindow.Current?.Close()", body);
    }

    [Fact]
    public void 重啟在釋放守衛與孤兒清掃之後才拉起新行程()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(App), "OnExit");
        var disposeAt = body.IndexOf("_singleInstance?.Dispose()", StringComparison.Ordinal);
        var sweepAt = body.IndexOf("KillOrphanChildren()", StringComparison.Ordinal);
        var restartAt = body.IndexOf("RestartOnExit", StringComparison.Ordinal);
        Assert.True(disposeAt >= 0 && restartAt > disposeAt, "重啟必須在守衛釋放之後");
        Assert.True(sweepAt >= 0 && restartAt > sweepAt, "重啟必須在孤兒清掃之後");
    }

    [Fact]
    public void 匯出集合不含憑證授權與歷史資料()
    {
        // 這份清單是安全邊界：users／auth_providers 是憑證、license 機器綁定、
        // segments／alarm_events／audit_log 是資料不是設定。新增表時只准加設定表。
        var source = SourceContract.Read(Service);
        var start = source.IndexOf("internal static readonly string[] ExportTables", StringComparison.Ordinal);
        var end = source.IndexOf("];", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到 ExportTables 清單");
        var list = source[start..end];
        foreach (var forbidden in new[] { "users", "auth_providers", "license", "login_sessions", "audit_log", "segments", "alarm_events" })
        {
            Assert.DoesNotContain($"\"{forbidden}\"", list);
        }
    }
}
