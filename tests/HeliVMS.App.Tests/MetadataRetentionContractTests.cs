using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 中繼資料保管期限（警報事件／事件錄音／稽核日誌）的桌面契約（M315）。
///
/// 清理邏輯本身在 <c>MetadataRetentionPruner</c>（Storage）有單元測試，WebApi 也有端到端測試；
/// 但桌面這條路若漏了任何一段，症狀是「設定改了、警報／稽核卻永遠不刪」——事件表只增不減，
/// 磁碟慢慢被撐爆，卻沒有任何錯誤訊息。WPF 視窗無法在測試中直接建立，故以原始碼契約釘住接線。
/// </summary>
public sealed class MetadataRetentionContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";

    [Fact]
    public void 設定中心有可套用的中繼資料保留欄位()
    {
        var xaml = SourceContract.Read(SettingsXaml);
        foreach (var element in new[]
                 {
                     "AlarmRetentionDaysLabel", "AlarmRetentionDaysTextBox",
                     "ApplyAlarmRetentionDaysButton", "AlarmRetentionDaysHintText", "AlarmRetentionDaysStatusText",
                     "AuditRetentionDaysLabel", "AuditRetentionDaysTextBox",
                     "ApplyAuditRetentionDaysButton", "AuditRetentionDaysHintText", "AuditRetentionDaysStatusText",
                 })
        {
            Assert.Contains($"x:Name=\"{element}\"", xaml);
        }

        Assert.Equal("OnApplyAlarmRetentionDaysClicked", WiredHandler(SettingsXaml, "ApplyAlarmRetentionDaysButton"));
        Assert.Equal("OnApplyAuditRetentionDaysClicked", WiredHandler(SettingsXaml, "ApplyAuditRetentionDaysButton"));
    }

    [Fact]
    public void 設定中心把中繼資料保留天數寫進共用設定並回讀()
    {
        var code = SourceContract.Read(SettingsCode);

        Assert.Contains("MetadataRetentionPruner.AlarmDaysKey", code);
        Assert.Contains("MetadataRetentionPruner.AuditDaysKey", code);

        var alarm = SourceContract.BodyOf(code, "OnApplyAlarmRetentionDaysClicked");
        Assert.Contains("AlarmRetentionDaysKey", alarm);

        var audit = SourceContract.BodyOf(code, "OnApplyAuditRetentionDaysClicked");
        Assert.Contains("AuditRetentionDaysKey", audit);

        var apply = SourceContract.BodyOf(code, "ApplyMetadataRetentionDays");
        Assert.Contains("_settings.Set(", apply);
        Assert.Contains("TryParse", apply);

        Assert.Contains("AlarmRetentionDaysKey", SourceContract.BodyOf(code, "ReloadAlarmRetentionDays"));
        Assert.Contains("AuditRetentionDaysKey", SourceContract.BodyOf(code, "ReloadAuditRetentionDays"));
    }

    [Fact]
    public void 桌面保留迴圈會清理中繼資料且讀同一組設定鍵()
    {
        var main = SourceContract.Read(MainCode);

        // 桌面不再自己逐一呼叫配額／快照／中繼資料清理，而是走與 Headless Agent 共用的
        // RetentionCoordinator.Run；若有人把中繼資料清理從協調器拔掉，這裡不會察覺，
        // 但 Recording.Tests 的協調器測試（行為）會紅。
        Assert.Contains("RetentionCoordinator.Run(", main);
        Assert.Contains("MetadataRetentionPruner.AlarmDaysKey", main);
        Assert.Contains("MetadataRetentionPruner.AuditDaysKey", main);
    }

    /// <summary>
    /// 桌面保留迴圈也要做錄影完整性自我檢查（M318，§11.1 寫盤保護）。
    ///
    /// 桌面模式內嵌的 Web 主機不掛背景服務，完整性迴圈只有這裡與 Headless Agent 兩條路；
    /// 漏掉桌面這條，症狀是「桌機部署下損壞的錄影永遠不會被標記」——而這正是多數現場的部署方式。
    /// 斷言錨在行首，因為單用 <c>Contains</c> 會被把呼叫註解掉蒙混過關。
    /// </summary>
    [Fact]
    public void 桌面保留迴圈也會做錄影完整性自我檢查()
    {
        var main = SourceContract.Read(MainCode);
        Assert.Matches(@"(?m)^\s*MediaIntegrityCoordinator\.RunConfigured\(", main);
    }

    /// <summary>
    /// 桌面也要回報磁碟壓力，且排程的「停新錄」閘門真的接上（M319，§11.1）。
    ///
    /// <para>
    /// 桌面模式內嵌的 Web 主機不掛背景服務，磁碟壓力回報只有這裡與 Headless Agent 兩條路；
    /// 漏掉桌面這條，症狀是「桌機部署下磁碟將滿永遠不回報、滿了也照樣開新錄影」。斷言錨在行首，
    /// 因為單用 <c>Contains</c> 會被把呼叫註解掉蒙混過關。
    /// </para>
    /// </summary>
    [Fact]
    public void 桌面保留迴圈會回報磁碟壓力且排程接上停新錄閘門()
    {
        var main = SourceContract.Read(MainCode);
        Assert.Matches(@"(?m)^\s*var level = DiskPressureCoordinator\.RunConfigured\(", main);
        Assert.Matches(@"(?m)^\s*diskFull: \(\) => DiskPressureCoordinator\.ShouldStopNewRecording\(", main);
    }

    /// <summary>
    /// 桌面的排程器也要寫錄影心跳到資料根目錄（M320，§11.1 看門狗）。
    ///
    /// <para>
    /// 心跳由「誰在錄」寫、由看門狗讀。桌面是最常見的錄影主體，漏掉這條，Headless Agent 的
    /// 看門狗在桌機部署下永遠看到 Missing（或誤判曾由 Agent 寫過的舊心跳過期），看門狗等於沒用。
    /// </para>
    /// </summary>
    [Fact]
    public void 桌面排程器寫入資料根目錄心跳()
    {
        var main = SourceContract.Read(MainCode);
        Assert.Matches(@"(?m)^\s*heartbeatRoot: ResolveDataRoot\(\)", main);
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
