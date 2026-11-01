namespace HeliVMS.App.Tests;

/// <summary>
/// 回放聲音的接線契約（與即時監看同一套：解碼行程的第二個輸出＋逐台喇叭）。
/// </summary>
/// <remarks>
/// 回放與即時共用 Media 的 <c>AudioOutputArguments</c> 那份參數，所以這裡只釘
/// 「視窗這一側」的四個接點：埠要在 ffmpeg 啟動前綁好、圖示只在有音訊軌的段上出現、
/// 非 1× 不播、停止與關窗要收。參數本身由 Media 的實跑測試驗證。
/// </remarks>
public sealed class PlaybackAudioContractTests
{
    private const string Code = "src/HeliVMS.App/PlaybackWindow.xaml.cs";
    private const string Session = "src/HeliVMS.Recording/PlaybackSession.cs";

    [Fact]
    public void 起播前就綁好收音的埠()
    {
        var source = SourceContract.Read(Code);

        // PlayFrom 是唯一的開播漏斗：埠必須在 PlayAsync 之前交給 session，
        // 因為參數是啟動 ffmpeg 時寫定的（事後改要重連，等於畫面閃一下）。
        var playFrom = SourceContract.BodyOf(source, "PlayFrom");
        Assert.Contains("ArmPlaybackAudio(session)", playFrom);
        Assert.Contains("_ = session.PlayAsync(seekSeconds);", playFrom);
        Assert.True(
            playFrom.IndexOf("ArmPlaybackAudio(session)", StringComparison.Ordinal)
            < playFrom.IndexOf("_ = session.PlayAsync(seekSeconds);", StringComparison.Ordinal),
            "收音埠必須在啟動播放之前設好。");

        var arm = SourceContract.BodyOf(source, "ArmPlaybackAudio");
        Assert.Contains("new AudioStreamReceiver(", arm);
        Assert.Contains("session.AudioPort = _audioReceiver.Port", arm);
    }

    [Fact]
    public void 回放聲音與即時監看共用同一份輸出參數()
    {
        var session = SourceContract.Read(Session);

        // 有音訊軌才掛：舊錄影（或當初沒開音訊的攝影機）的檔案沒有音訊串流，
        // 對它下 -map 0:a:0 會讓 ffmpeg 直接失敗，連畫面都沒有。
        Assert.Contains("AudioOutputArguments.ResolvePort(AudioPort, info.AudioCodec is not null)", session);
        Assert.Contains("AudioOutputArguments.Build(audioPort, audioSampleRate)", session);
        Assert.Contains("SourceHasAudio = info.AudioCodec is not null", session);
    }

    [Fact]
    public void 喇叭圖示只在這一段有音訊軌時出現()
    {
        var source = SourceContract.Read(Code);
        var icon = SourceContract.BodyOf(source, "UpdatePlaybackAudioIcon");

        // 沒有音訊軌的段（舊錄影）不顯示圖示：比「按了沒反應」好。
        Assert.Contains("SourceHasAudio == true", icon);
        Assert.Contains("Visibility.Collapsed", icon);

        // 探測結果在解碼執行緒上回來，圖示跟著第一幀出現。
        Assert.Contains("UpdatePlaybackAudioIcon()", SourceContract.BodyOf(source, "OnFrame"));
    }

    [Fact]
    public void 非一倍速不播聲音()
    {
        var source = SourceContract.Read(Code);

        // 變速需要 atempo 才不失真；播出快轉的抖音比安靜更糟。
        Assert.Contains("GetSpeed() - 1", SourceContract.BodyOf(source, "TogglePlaybackAudioAsync"));

        // 播放中改速度要立刻把聲音停掉，圖示也要跟著退回白色。
        var speed = SourceContract.BodyOf(source, "OnSpeedChanged");
        Assert.Contains("StopAudioAsync()", speed);
        Assert.Contains("UpdatePlaybackAudioIcon()", speed);
    }

    [Fact]
    public void 停止與關窗都會收掉聲音()
    {
        var source = SourceContract.Read(Code);

        Assert.Contains("StopAudioAsync()", SourceContract.BodyOf(source, "StopPlayback"));
        Assert.Contains("UpdatePlaybackAudioIcon()", SourceContract.BodyOf(source, "StopPlayback"));

        var close = SourceContract.BodyOf(source, "OnClosing");
        Assert.Contains("audio.DisposeAsync()", close);
        Assert.Contains("_audioReceiver?.Dispose()", close);
    }
}
