namespace HeliVMS.App.Tests;

/// <summary>
/// 每攝影機收音音量（QC_Live 的 CameraVolume 對齊）的接線契約。桌面端沒有 WPF 執行期測試：
/// 夾值與位準換算由 <c>HeliVMS.Shared.Tests.AudioVolumeTests</c> 行為測試保護、
/// DB 往返由 <c>ChannelRepositoryTests</c> 保護；這裡確認「DB → 點喇叭 → waveOut」
/// 這條鏈每一節都真的把音量接上——漏一節的症狀是「設了音量卻永遠全音量」，日誌乾淨。
/// </summary>
public sealed class ChannelVolumeContractTests
{
    [Fact]
    public void 音量從輸出端一路到裝置都有接()
    {
        var waveOutCode = SourceContract.Read("src/HeliVMS.App/Services/WaveOutPlayer.cs");
        var waveOut = SourceContract.BodyOf(waveOutCode, "SetVolume");
        Assert.Contains("AudioVolume.Clamp", waveOut);
        // 真正寫裝置的在 ApplyVolumeLocked（Start 開好裝置也走同一份，避免「開了但沒套」的窗口）。
        Assert.Contains("ApplyVolumeLocked()", waveOut);
        Assert.Contains("waveOutSetVolume", waveOutCode);

        var audioOutput = SourceContract.Read("src/HeliVMS.App/Services/AudioOutput.cs");
        Assert.Contains("public void SetVolume(int percent)", audioOutput);
        Assert.Contains("_player?.SetVolume(_volumePercent)", audioOutput);
        // 開機就帶上音量：裝置開了但音量還沒套的窗口，會讓「100→50 之後重新點喇叭」的前幾百毫秒回到全音量。
        Assert.Contains("volumePercent: AudioVolume.Clamp(_volumePercent)", audioOutput);

        var controller = SourceContract.Read("src/HeliVMS.App/Services/LiveAudioController.cs");
        Assert.Contains("public void SetVolume(int percent) => _output.SetVolume(percent)", controller);
    }

    [Fact]
    public void 點喇叭時套用該台的音量()
    {
        var click = SourceContract.BodyOf(SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs"), "OnCellAudioIconClicked");
        Assert.Contains("SetVolume(", click);
        Assert.Contains("ListenVolume", click);
    }

    [Fact]
    public void 頻道管理會存音量且讀不懂回預設()
    {
        var code = SourceContract.Read("src/HeliVMS.App/ChannelManagerWindow.xaml.cs");

        Assert.Contains("ChannelEditVolumeBox", code);
        Assert.Contains("AudioVolume.Clamp(vol)", code);
        // 清空欄位再套用，不該把整台變成靜音。
        Assert.Contains(": AudioVolume.Default", code);
    }
}
