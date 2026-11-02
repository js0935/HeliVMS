using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 設定版本快照與一鍵回滾（§9.5）契約。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試，而「回滾設定」是會整份覆寫 <c>app_settings</c> 的操作：
/// 按鈕漏接線功能等於不存在；還原到錯的版本之後若沒有退路，現場只能手工修資料庫。
/// 這些契約把住幾種代價很高的回歸：破壞性操作少了管理員把關或明確確認、回滾沒先選版本、
/// 以及版本清單沒有實際載入資料。
/// </para>
/// <para>
/// 舊版這區還有「設定 JSON 匯出／匯入」按鈕（只含 app_settings）；設定檔遷移改由
/// 關於頁的全量匯出／匯入負責（見 <c>ConfigXferContractTests</c>），這裡只剩
/// 快照與還原——同一頁出現兩組「匯出設定／匯入設定」是操作員回報的功能重複。
/// 匯入前的自動快照保障轉移到設定檔匯入（OnConfigImportClicked）。
/// </para>
/// </summary>
public sealed class SettingsVersionContractTests
{
    private const string Code = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/SettingsWindow.xaml";

    [Fact]
    public void 快照與還原按鈕都有接線且舊的設定檔按鈕已移除()
    {
        Assert.Equal("OnSnapshotSettingsClicked", WiredHandler("SnapshotSettingsButton"));
        Assert.Equal("OnRestoreSettingsClicked", WiredHandler("RestoreSettingsButton"));

        // 舊的 app_settings 匯出／匯入按鈕已由關於頁的全量設定匯出／匯入取代：
        // 留著就是同一頁兩組同名功能。
        Assert.DoesNotContain("ExportSettingsButton", SourceContract.Read(Xaml));
        Assert.DoesNotContain("ImportSettingsButton", SourceContract.Read(Xaml));
        Assert.DoesNotContain("OnExportSettingsClicked", SourceContract.Read(Code));
        Assert.DoesNotContain("OnImportSettingsClicked", SourceContract.Read(Code));
    }

    [Fact]
    public void 回滾與設定檔匯入需管理員()
    {
        // 回滾與設定檔匯入會整份覆寫設定，唯讀帳號絕不能執行。
        var gate = BodyOf("VersionRequireAdmin");
        Assert.Contains("SessionContext.IsAdmin", gate);
        Assert.Contains("Settings.AdminOnly", gate);

        Assert.Contains("VersionRequireAdmin()", BodyOf("OnRestoreSettingsClicked"));
        Assert.Contains("VersionRequireAdmin()", BodyOf("OnConfigImportClicked"));
    }

    [Fact]
    public void 回滾前明確確認()
    {
        var restore = BodyOf("OnRestoreSettingsClicked");
        Assert.Contains("MessageBox.Show", restore);
        Assert.Contains("Settings.VersionRestoreConfirm", restore);
        Assert.True(
            restore.IndexOf("Settings.VersionRestoreConfirm", StringComparison.Ordinal)
            < restore.IndexOf(".Restore(", StringComparison.Ordinal),
            "確認對話框必須在 Restore 之前");
    }

    [Fact]
    public void 回滾與設定檔匯入前都自動快照()
    {
        // 匯錯檔或還原到錯的版本之後，要能從版本清單一鍵回到操作前——
        // 忘了自動快照等於沒有 undo。
        var restore = BodyOf("OnRestoreSettingsClicked");
        Assert.Contains(".Snapshot(", restore);
        Assert.True(
            restore.IndexOf(".Snapshot(", StringComparison.Ordinal)
            < restore.IndexOf(".Restore(", StringComparison.Ordinal),
            "自動快照必須在 Restore 之前");

        var import = BodyOf("OnConfigImportClicked");
        Assert.Contains(".Snapshot(", import);
        Assert.True(
            import.IndexOf(".Snapshot(", StringComparison.Ordinal)
            < import.IndexOf(".ImportFrom(", StringComparison.Ordinal),
            "設定檔匯入的自動快照必須在 ImportFrom 之前");
    }

    [Fact]
    public void 回滾沒先選版本會被擋下()
    {
        var restore = BodyOf("OnRestoreSettingsClicked");
        Assert.Contains("SelectedVersionId()", restore);
        Assert.Contains("Settings.VersionSelectFirst", restore);

        // 沒選版本時 Restore 不該被呼叫：null 的版本 Id 對不出任何一列。
        Assert.True(
            restore.IndexOf("Settings.VersionSelectFirst", StringComparison.Ordinal)
            < restore.IndexOf(".Restore(", StringComparison.Ordinal),
            "沒選版本的檢查必須在 Restore 之前");
    }

    [Fact]
    public void 版本清單有實際載入資料()
    {
        var reload = BodyOf("ReloadSettingsVersions");
        Assert.Contains("ListVersions(", reload);
        Assert.Contains("SettingsVersionList.ItemsSource", reload);

        // 一般備份操作也要刷新版本清單（ReloadBackup 內有呼叫 ReloadSettingsVersions）。
        Assert.Contains("ReloadSettingsVersions()", BodyOf("ReloadBackup"));
    }

    private static string BodyOf(string method)
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), method);
        Assert.NotEqual(string.Empty, body);
        return body;
    }

    private static string WiredHandler(string element)
    {
        var tag = Regex.Match(
            SourceContract.Read(Xaml),
            $@"<[A-Za-z][^<>]*?x:Name=""{Regex.Escape(element)}""[^<>]*?>",
            RegexOptions.Singleline).Value;
        Assert.NotEqual(string.Empty, tag);
        var match = Regex.Match(tag, @"\bClick=""(\w+)""");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
