namespace HeliVMS.App.Tests;

/// <summary>
/// 回放聲音在桌面端的接線契約。
/// </summary>
/// <remarks>
/// 回放的聲音是另一條獨立的 ffmpeg：影格走 <c>PlaybackSession</c>（rawvideo），聲音走
/// <c>AudioPullClient</c>（s16le）。這條契約釘住「起播、停止、變速、關窗」四個接點——
/// 漏掉任一個的症狀都不明顯：聲音比畫面晚一段、按了停止還在講話、2 倍速變成抖音，
/// 而畫面上一切正常。
/// </remarks>
public sealed class PlaybackAudioContractTests
{
    private const string Code = "src/HeliVMS.App/PlaybackWindow.xaml.cs";

    [Fact]
    public void 起播時會拉起同一段的聲音()
    {
        var source = SourceContract.Read(Code);

        // PlayFrom 是唯一的開播漏斗：所有跳轉／選段／續播都經過它，聲音掛在這裡才不會漏。
        Assert.Contains("StartAudio(seg, seekSeconds)", SourceContract.BodyOf(source, "PlayFrom"));

        var start = SourceContract.BodyOf(source, "StartAudio");

        // 聲音來源必須是「這一段的檔案」且是檔案模式（即時模式的參數會帶 RTSP 選項）。
        Assert.Contains("seg.FilePath", start);
        Assert.Contains("live: false", start);

        // 長度跟影像一致：播到段尾就收掉，不會跨到下一段還在講話。
        Assert.Contains("LengthOf(seg) - seekSeconds", start);
    }

    [Fact]
    public void 非一倍速不播聲音()
    {
        var source = SourceContract.Read(Code);

        // 變速需要 atempo 才不失真；播出快轉的抖音比安靜更糟。
        Assert.Contains("GetSpeed() - 1", SourceContract.BodyOf(source, "StartAudio"));

        // 播放中改速度要立刻重新對齊（1× 接回來、其他速度停掉）。
        Assert.Contains("StartAudio(_current, _posInside)", SourceContract.BodyOf(source, "OnSpeedChanged"));
    }

    [Fact]
    public void 停止與關窗都會停掉聲音()
    {
        var source = SourceContract.Read(Code);

        Assert.Contains("StopAudioAsync()", SourceContract.BodyOf(source, "StopPlayback"));
        Assert.Contains("audio.DisposeAsync()", SourceContract.BodyOf(source, "OnClosing"));
    }
}
