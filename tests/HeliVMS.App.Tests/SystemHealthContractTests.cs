using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 系統健康視窗契約（M147，§14.7 #2）。
///
/// <para>
/// <c>SystemMetricsService</c> 早已由 Web API（/api/system-metrics）曝光，桌面卻沒有任何
/// 入口——維運在機器上只能看工作管理員。這份契約把設定中心（工具頁）入口與對量測服務的呼叫釘住。
/// </para>
/// </summary>
public sealed class SystemHealthContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string SettingsXaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string HealthCode = "src/HeliVMS.App/SystemHealthWindow.xaml.cs";

    [Fact]
    public void 設定中心有系統健康入口且接線()
    {
        Assert.Equal("OnSystemHealthClicked", WiredHandler(SettingsXaml, "SystemHealthButton"));

        var handler = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OnSystemHealthClicked");
        Assert.Contains("OpenSystemHealthWindow", handler);

        var open = SourceContract.BodyOf(SourceContract.Read(SettingsCode), "OpenSystemHealthWindow");
        Assert.Contains("new SystemHealthWindow(", open);
    }

    [Fact]
    public void 健康窗走本機量測服務()
    {
        var code = SourceContract.Read(HealthCode);
        Assert.Contains("SystemMetricsService.Capture()", code);
        Assert.Contains("CaptureHistory()", code);
    }

    /// <summary>
    /// §13-2 行程治理：健康窗必須顯示 ffmpeg 子行程的彙總（數量、分類、逾量告警）。
    /// 缺了這一塊，維運仍只能逐視窗拼湊「這台開著幾條 ffmpeg」，治理等於沒做。
    /// </summary>
    [Fact]
    public void 健康窗顯示ffmpeg子行程彙總()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/SystemHealthWindow.xaml");
        Assert.Contains("HealthFfmpegText", xaml);

        var refresh = SourceContract.BodyOf(SourceContract.Read(HealthCode), "Refresh");
        Assert.Contains("HealthFfmpegText.Text", refresh);
        Assert.Contains("FfmpegRunning", refresh);
        Assert.Contains("FfmpegOverCapacity", refresh);
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
