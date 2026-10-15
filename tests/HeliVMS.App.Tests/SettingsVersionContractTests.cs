using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 設定版本快照與一鍵回滾（§9.5）契約。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試，而「匯入／回滾設定」是會整份覆寫 <c>app_settings</c> 的操作：
/// 按鈕漏接線功能等於不存在；匯錯檔或還原到錯的版本之後若沒有退路，現場只能手工修資料庫。
/// 這些契約把住幾種代價很高的回歸：破壞性操作少了管理員把關或明確確認、匯入／回滾前
/// 忘了自動快照（等於沒有 undo）、回滾沒先選版本、以及版本清單沒有實際載入資料。
/// </para>
/// </summary>
public sealed class SettingsVersionContractTests
{
    private const string Code = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/SettingsWindow.xaml";

    [Fact]
    public void 四個設定版本按鈕都有接線()
    {
        Assert.Equal("OnSnapshotSettingsClicked", WiredHandler("SnapshotSettingsButton"));
        Assert.Equal("OnExportSettingsClicked", WiredHandler("ExportSettingsButton"));
        Assert.Equal("OnImportSettingsClicked", WiredHandler("ImportSettingsButton"));
        Assert.Equal("OnRestoreSettingsClicked", WiredHandler("RestoreSettingsButton"));
    }

    [Fact]
    public void 匯入與回滾需管理員()
    {
        // 匯入與回滾會整份覆寫設定，唯讀帳號絕不能執行。
        var gate = BodyOf("VersionRequireAdmin");
        Assert.Contains("SessionContext.IsAdmin", gate);
        Assert.Contains("\"Settings.AdminOnly\"", gate);

        Assert.Contains("VersionRequireAdmin()", BodyOf("OnImportSettingsClicked"));
        Assert.Contains("VersionRequireAdmin()", BodyOf("OnRestoreSettingsClicked"));
    }

    [Fact]
    public void 匯入與回滾前明確確認()
    {
        var import = BodyOf("OnImportSettingsClicked");
        Assert.Contains("MessageBox.Show", import);
        Assert.Contains("\"Settings.VersionImportConfirm\"", import);
        Assert.True(
            import.IndexOf("\"Settings.VersionImportConfirm\"", StringComparison.Ordinal)
            < import.IndexOf(".ImportJson(", StringComparison.Ordinal),
            "確認對話框必須在 ImportJson 之前");

        var restore = BodyOf("OnRestoreSettingsClicked");
        Assert.Contains("MessageBox.Show", restore);
        Assert.Contains("\"Settings.VersionRestoreConfirm\"", restore);
        Assert.True(
            restore.IndexOf("\"Settings.VersionRestoreConfirm\"", StringComparison.Ordinal)
            < restore.IndexOf(".Restore(", StringComparison.Ordinal),
            "確認對話框必須在 Restore 之前");
    }

    [Fact]
    public void 匯入與回滾前先自動快照當作退路()
    {
        // 匯錯檔、還原到錯的版本之後，唯一能退回的就是匯入／回滾前的那一份快照。
        var import = BodyOf("OnImportSettingsClicked");
        Assert.Contains(".Snapshot(", import);
        Assert.True(
            import.IndexOf(".Snapshot(", StringComparison.Ordinal)
            < import.IndexOf(".ImportJson(", StringComparison.Ordinal),
            "必須先快照才匯入");

        var restore = BodyOf("OnRestoreSettingsClicked");
        Assert.Contains(".Snapshot(", restore);
        Assert.True(
            restore.IndexOf(".Snapshot(", StringComparison.Ordinal)
            < restore.IndexOf(".Restore(", StringComparison.Ordinal),
            "必須先快照才回滾");
    }

    [Fact]
    public void 回滾需先選取版本()
    {
        var restore = BodyOf("OnRestoreSettingsClicked");
        Assert.Contains("SelectedVersionId()", restore);
        Assert.Contains("\"Settings.VersionSelectFirst\"", restore);

        // 沒選版本時必須在真的 Restore 之前就返回，不能拿 null 去還原。
        Assert.True(
            restore.IndexOf("\"Settings.VersionSelectFirst\"", StringComparison.Ordinal)
            < restore.IndexOf(".Restore(", StringComparison.Ordinal),
            "未選版本必須在 Restore 之前返回");
    }

    [Fact]
    public void 版本清單會實際載入並在進入備份頁時刷新()
    {
        var reload = BodyOf("ReloadSettingsVersions");
        Assert.Contains("ListVersions(", reload);
        Assert.Contains("SettingsVersionList.ItemsSource", reload);

        // 進入備份頁（ReloadBackup）時要順帶刷新版本清單，否則清單永遠是空的。
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
