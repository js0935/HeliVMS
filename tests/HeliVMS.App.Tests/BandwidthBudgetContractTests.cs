using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 錄影總頻寬上限／跨設備品質協調的桌面契約（M285，§14.7 #12）。
///
/// 協調器（<c>RecordingQualityCoordinator</c>）與排程端的串流選擇本身有行為測試；
/// 但如果設定中心沒有把上限寫進共用設定、或排程端沒有把決策接上，跨設備協調就等於不存在——
/// 使用者設定的上限永遠不會被任何一條錄影讀到，而畫面上看起來一切正常。
/// </summary>
public sealed class BandwidthBudgetContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string SchedulerCode = "src/HeliVMS.Recording/RecordingScheduler.cs";

    [Fact]
    public void 設定中心有可套用的錄影總頻寬上限欄位()
    {
        var xaml = SourceContract.Read(SettingsXaml);
        foreach (var element in new[]
                 {
                     "BandwidthBudgetLabel", "BandwidthBudgetTextBox",
                     "ApplyBandwidthBudgetButton", "BandwidthBudgetHintText", "BandwidthBudgetStatusText",
                 })
        {
            Assert.Contains($"x:Name=\"{element}\"", xaml);
        }

        Assert.Equal("OnApplyBandwidthBudgetClicked", WiredHandler(SettingsXaml, "ApplyBandwidthBudgetButton"));
    }

    [Fact]
    public void 設定中心把上限寫進共用設定並回讀()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnApplyBandwidthBudgetClicked");
        Assert.Contains("RecordingQualityCoordinator.BudgetSettingKey", handler);
        Assert.Contains("_settings.Set(", handler);
        Assert.Contains("ReloadBandwidthBudget", handler);

        var reload = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "ReloadBandwidthBudget");
        Assert.Contains("RecordingQualityCoordinator.ReadBudget", reload);
    }

    [Fact]
    public void 排程端會依上限協調並選擇主或子碼流()
    {
        var scheduler = SourceContract.Read(SchedulerCode);
        Assert.Contains("RecordingQualityCoordinator.ReadBudget(", scheduler);
        Assert.Contains("RecordingQualityCoordinator.Coordinate(", scheduler);
        Assert.Contains("RecordingQualityCoordinator.StreamFor(", scheduler);
        Assert.Contains("RecordingStream.Sub", scheduler);
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
