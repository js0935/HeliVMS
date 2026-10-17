using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 錄影分段長度的桌面契約（M312）。
///
/// 政策（<c>RecordingSegmentPolicy</c>）本身有單元測試，但設定中心若沒有把值寫進共用設定、
/// 或錄影端沒有把設定接上，操作員在畫面上改的秒數永遠不會有任何效果——檔數不會變，
/// 卻也沒有任何錯誤，等到磁碟被檔數塞爆才會發現。
/// </summary>
public sealed class SegmentSecondsContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string SchedulerCode = "src/HeliVMS.Recording/RecordingScheduler.cs";
    private const string ChannelSessionCode = "src/HeliVMS.App/Services/ChannelSession.cs";

    [Fact]
    public void 設定中心有可套用的錄影分段長度欄位()
    {
        var xaml = SourceContract.Read(SettingsXaml);
        foreach (var element in new[]
                 {
                     "SegmentSecondsLabel", "SegmentSecondsTextBox",
                     "ApplySegmentSecondsButton", "SegmentSecondsHintText", "SegmentSecondsStatusText",
                 })
        {
            Assert.Contains($"x:Name=\"{element}\"", xaml);
        }

        Assert.Equal("OnApplySegmentSecondsClicked", WiredHandler(SettingsXaml, "ApplySegmentSecondsButton"));
    }

    [Fact]
    public void 設定中心把分段長度寫進共用設定並回讀()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnApplySegmentSecondsClicked");
        Assert.Contains("RecordingSegmentPolicy.SettingKey", handler);
        Assert.Contains("_settings.Set(", handler);
        Assert.Contains("ReloadSegmentSeconds", handler);
        Assert.Contains("TryParse", handler);

        var reload = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "ReloadSegmentSeconds");
        Assert.Contains("RecordingSegmentPolicy.Read", reload);
    }

    [Fact]
    public void 排程端會採用設定的分段長度()
    {
        var scheduler = SourceContract.Read(SchedulerCode);
        Assert.Contains("RecordingSegmentPolicy.Read(", scheduler);
        Assert.Contains("segmentSeconds: segmentSeconds", scheduler);
        // 不得再有任何寫死的分段秒數（例如 segmentSeconds: 15）。
        Assert.DoesNotContain("segmentSeconds: 15", scheduler);
    }

    [Fact]
    public void 監看牆手動錄影也用同一套分段長度()
    {
        var session = SourceContract.Read(ChannelSessionCode);
        Assert.Contains("RecordingSegmentPolicy.Read(_store)", session);
        Assert.DoesNotContain("segmentSeconds: 15", session);
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
