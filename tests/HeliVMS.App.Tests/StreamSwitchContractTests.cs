namespace HeliVMS.App.Tests;

/// <summary>
/// 切流整合契約（M76，§15.2）。桌面端沒有 WPF 執行期測試，只能靠原始碼契約把住：
/// 切流視窗要跟實際連線共用同一份狀態（不再各開各的 in-memory 合成）、採納決策要持久化、
/// 而且切顯示串流時錄影必須仍走主流——這條最要緊，漏了會安靜地把錄影畫質換成次流且無錯誤。
/// </summary>
public sealed class StreamSwitchContractTests
{
    private const string Window = "src/HeliVMS.App/StreamSwitchWindow.xaml.cs";
    private const string Manager = "src/HeliVMS.App/Services/ChannelManager.cs";
    private const string Session = "src/HeliVMS.App/Services/ChannelSession.cs";
    private const string MainWindow = "src/HeliVMS.App/MainWindow.xaml.cs";

    [Fact]
    public void 切流視窗與頻道管理器共用切流狀態並即時套用()
    {
        var code = SourceContract.Read(Window);
        Assert.Contains("ChannelManager? manager", code);
        Assert.Contains("manager?.Switcher", code);

        var apply = SourceContract.BodyOf(code, "OnApplyClicked");
        Assert.NotEqual(string.Empty, apply);
        Assert.Contains("await _manager.ApplyStreamKindAsync", apply);
    }

    [Fact]
    public void 沒有管理器時切流視窗仍會持久化()
    {
        var apply = SourceContract.BodyOf(SourceContract.Read(Window), "OnApplyClicked");
        Assert.NotEqual(string.Empty, apply);
        Assert.Contains("_prefs.Save", apply);
    }

    [Fact]
    public void 頻道管理器啟動時會以資料庫恢復切流狀態()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Manager), "ChannelManager");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("Switcher.Prime", body);
        Assert.Contains("_streamPrefs.GetKind", body);
    }

    [Fact]
    public void 採納切流會持久化並即時切換顯示串流()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Manager), "ApplyStreamKindAsync");
        Assert.NotEqual(string.Empty, body);
        Assert.Contains("Switcher.ApplySwitch", body);
        Assert.Contains("_streamPrefs.Save", body);
        Assert.Contains("SwitchDisplayStreamAsync", body);
    }

    [Fact]
    public void 錄影固定用主流而顯示依碼流選擇()
    {
        var manager = SourceContract.Read(Manager);
        Assert.Contains("StreamRouting.RecordingUrl(channel.MainStreamUrl)", manager);
        Assert.Contains("StreamRouting.DisplayUrl(", manager);

        // 錄影器起點必須是 RecordingUrl；若傳 Url，切到次流後錄影會跟著降畫質。
        var recording = SourceContract.BodyOf(SourceContract.Read(Session), "SetRecordingAsync");
        Assert.NotEqual(string.Empty, recording);
        Assert.Contains("StartAsync(ChannelId, RecordingUrl", recording);
        Assert.DoesNotContain("StartAsync(ChannelId, Url", recording);
    }

    [Fact]
    public void 主視窗開啟切流視窗時會帶入管理器()
    {
        Assert.Contains("new StreamSwitchWindow(_store!, _manager)", SourceContract.Read(MainWindow));
    }
}
