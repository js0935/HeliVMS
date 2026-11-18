namespace HeliVMS.App.Tests;

/// <summary>
/// 回放左側從「錄影片段清單」改成「攝影機勾選清單」的契約。
///
/// <para>
/// 操作員不再點片段選時間（起播時刻由時間軸／事件／書籤決定），而是勾選要同時看哪幾台：
/// 勾一台單路播放、勾兩台兩台同時播放。桌面端沒有 WPF 執行期測試環境，這裡守住幾件
/// 改壞了畫面照樣動、但語意全錯的事：路數的來源、焦點的轉移，以及路數只能有一個入口。
/// </para>
/// </summary>
public sealed class PlaybackCameraSelectionContractTests
{
    private const string Code = "src/HeliVMS.App/PlaybackWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/PlaybackWindow.xaml";

    [Fact]
    public void 左側片段清單已改為攝影機勾選清單()
    {
        var xaml = SourceContract.Read(Xaml);
        var source = SourceContract.Read(Code);

        Assert.Contains("x:Name=\"CameraCheckList\"", xaml);
        Assert.Contains("OnCameraPickChanged", xaml);
        Assert.Contains("PlaybackCameraSelectionLabel", xaml);

        // 舊的片段清單與它的三個欄位全部消失，避免「點片段選時間」的入口留在畫面上。
        Assert.DoesNotContain("x:Name=\"SegmentList\"", xaml);
        Assert.DoesNotContain("SegColStart", xaml);
        Assert.DoesNotContain("x:Name=\"PlaybackSegmentsLabel\"", xaml);
        Assert.DoesNotContain("SegmentList.ItemsSource", source);
    }

    [Fact]
    public void 回放牆路數等於勾選的攝影機台數()
    {
        var source = SourceContract.Read(Code);

        // SplitCount 與 RebuildPanes 都只能問勾選清單，不能再回頭讀分割下拉。
        var splitCount = SourceContract.BodyOf(source, "SplitCount");
        Assert.Contains("_cameraPicks.Count(p => p.IsChecked)", splitCount);
        Assert.DoesNotContain("SplitCombo", splitCount);

        var rebuild = SourceContract.BodyOf(source, "RebuildPanes");
        Assert.Contains("CheckedCameraIds()", rebuild);
        Assert.Contains("WallChannelIds(pickedIds", rebuild);
        Assert.DoesNotContain("channels.Select(c => c.Id)", rebuild);
    }

    [Fact]
    public void 勾選變動會重建回放牆並重新對時()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(Code), "OnCameraPickChanged");

        Assert.Contains("RebuildPanes()", handler);
        Assert.Contains("SyncPanes(force: true)", handler);
        Assert.Contains("SavePlaybackPreferences()", handler);

        // 程式自己改勾選（換台收斂、分割同步）時不能再次觸發這個非同步事件。
        Assert.Contains("_suppressCameraPickReload", handler);
    }

    [Fact]
    public void 不允許把所有攝影機都取消勾選()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(Code), "OnCameraPickChanged");
        Assert.Contains("checkedIds.Count == 0", handler);
        Assert.Contains("SetCheckedCameras([fallback])", handler);
    }

    [Fact]
    public void 焦點台被取消時改到第一台勾選的攝影機()
    {
        var handler = SourceContract.BodyOf(SourceContract.Read(Code), "OnCameraPickChanged");
        Assert.Contains("!checkedIds.Contains(focus.Id)", handler);
        Assert.Contains("checkedIds[0]", handler);
        Assert.Contains("ChannelCombo.SelectedItem = newFocus", handler);
        Assert.Contains("await ReloadForPlaybackAsync()", handler);
    }

    [Fact]
    public void 分割下拉已移除且路數只由勾選決定()
    {
        var xaml = SourceContract.Read(Xaml);
        var source = SourceContract.Read(Code);

        // 「要播幾路」只能有一個入口：下拉與勾選清單並存時，改了勾選下拉不會跟著動
        // （反之亦然），而兩者都不會報錯——操作員只會覺得牆「有時對、有時不對」。
        Assert.DoesNotContain("x:Name=\"SplitCombo\"", xaml);
        Assert.DoesNotContain("OnSplitChanged", xaml);
        Assert.DoesNotContain("OnSplitChanged", source);
        Assert.DoesNotContain("SplitCombo", source);

        // 勾選清單是路數與成員的唯一來源。
        Assert.Contains("_cameraPicks.Count(p => p.IsChecked)", SourceContract.BodyOf(source, "SplitCount"));
        Assert.Contains("x:Name=\"CameraCheckList\"", xaml);
    }

    [Fact]
    public void 換台不會清掉已經勾好的多路()
    {
        var picks = SourceContract.BodyOf(SourceContract.Read(Code), "RebuildCameraPicks");

        // 已有勾選狀態按 Id 保留；只有第一次開窗才用記住的勾選台數決定初始勾選。
        // 第一次開窗的初始勾選數來自記住的偏好（台數），不是任何下拉。
        Assert.Contains("_initialPickCount", picks);
        Assert.Contains("previous.TryGetValue(channel.Id, out var wasChecked)", picks);
        Assert.Contains("channel.Id == focusId", picks);
    }

    [Fact]
    public void 點主畫面與放大次畫面都收斂成只勾一台()
    {
        var source = SourceContract.Read(Code);

        Assert.Contains("SetCheckedCameras([focusId])", SourceContract.BodyOf(source, "OnMainPlayerClicked"));
        Assert.Contains("SetCheckedCameras([pane.Channel.Id])", SourceContract.BodyOf(source, "FocusPaneAsync"));
    }

    [Fact]
    public void 攝影機選擇標題三語都在()
    {
        var i18n = SourceContract.Read("src/HeliVMS.Storage/I18n.cs");
        var lines = i18n.Split('\n')
            .Where(l => l.Contains("[\"Playback.CameraSelection\"]"))
            .ToList();

        Assert.True(lines.Count == 3, $"Playback.CameraSelection 應該有三語，實際 {lines.Count}");
    }
}
