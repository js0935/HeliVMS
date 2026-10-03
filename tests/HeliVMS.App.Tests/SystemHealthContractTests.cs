using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 系統健康視窗契約（M147，§14.7 #2）。
///
/// <para>
/// <c>SystemMetricsService</c> 早已由 Web API（/api/system-metrics）曝光，桌面卻沒有任何
/// 入口——維運在機器上只能看工作管理員。這份契約把主視窗入口與對量測服務的呼叫釘住。
/// </para>
/// </summary>
public sealed class SystemHealthContractTests
{
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string MainXaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string HealthCode = "src/HeliVMS.App/SystemHealthWindow.xaml.cs";

    [Fact]
    public void 主視窗有系統健康入口且接線()
    {
        Assert.Equal("OnSystemHealthClicked", WiredHandler(MainXaml, "SystemHealthButton"));

        var handler = SourceContract.BodyOf(SourceContract.Read(MainCode), "OnSystemHealthClicked");
        Assert.Contains("OpenSystemHealthWindow", handler);

        var open = SourceContract.BodyOf(SourceContract.Read(MainCode), "OpenSystemHealthWindow");
        Assert.Contains("new SystemHealthWindow(", open);
    }

    [Fact]
    public void 健康窗走本機量測服務()
    {
        var code = SourceContract.Read(HealthCode);
        Assert.Contains("SystemMetricsService.Capture()", code);
        Assert.Contains("CaptureHistory()", code);
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
