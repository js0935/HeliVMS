using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 多磁碟錄影的桌面契約（M283，§2.1 儲存）。
///
/// 錄影磁碟政策（<c>RecordingStorage</c>）本身有 Storage 層的行為測試，但如果設定中心沒有
/// 把「磁碟清單」接上、或寫入端沒有把選擇器接上，多磁碟能力等於不存在：畫面只有一顆碟、
/// 錄影永遠寫在同一個根目錄，主要碟滿了就無聲停錄。
/// </summary>
public sealed class MultiDiskRecordingContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";

    [Fact]
    public void 設定中心有可新增移除的多磁碟清單()
    {
        var xaml = SourceContract.Read(SettingsXaml);
        foreach (var element in new[]
                 {
                     "RecordingsDiskCombo", "AddRecordingsDiskButton", "RecordingsDiskList",
                     "SetPrimaryRecordingsDiskButton", "RemoveRecordingsDiskButton", "ApplyRecordingsDiskButton",
                 })
        {
            Assert.Contains($"x:Name=\"{element}\"", xaml);
        }

        Assert.Equal("OnAddRecordingsDiskClicked", WiredHandler(SettingsXaml, "AddRecordingsDiskButton"));
        Assert.Equal("OnRemoveRecordingsDiskClicked", WiredHandler(SettingsXaml, "RemoveRecordingsDiskButton"));
        Assert.Equal("OnSetPrimaryRecordingsDiskClicked", WiredHandler(SettingsXaml, "SetPrimaryRecordingsDiskButton"));
        Assert.Equal("OnApplyRecordingsDiskClicked", WiredHandler(SettingsXaml, "ApplyRecordingsDiskButton"));
    }

    [Fact]
    public void 設定中心把磁碟清單寫進共用設定並通知套用()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnApplyRecordingsDiskClicked");
        Assert.Contains("DisksSettingKey", handler);
        Assert.Contains("KnownRootsSettingKey", handler);
        Assert.Contains("RootSettingKey", handler);
        Assert.Contains("_recordingsRootChanged", handler);
    }

    [Fact]
    public void 設定中心從共用設定讀回磁碟清單()
    {
        var reload = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "ReloadRecordingsDisk");
        Assert.Contains("ParseDisks(", reload);
        Assert.Contains("DisksSettingKey", reload);
    }

    [Fact]
    public void 桌面與Agent的錄影都經過多磁碟選擇器()
    {
        Assert.Contains(
            "CreateRootSelector(",
            SourceContract.Read("src/HeliVMS.App/Services/ChannelSession.cs"));
        Assert.Contains(
            "CreateRootSelector(",
            SourceContract.Read("src/HeliVMS.Recording/RecordingScheduler.cs"));
    }

    [Fact]
    public void 遠端回放政策涵蓋所有錄影磁碟()
    {
        Assert.Contains(
            "ConfiguredRoots(",
            SourceContract.Read("src/HeliVMS.WebApi/Program.cs"));
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
