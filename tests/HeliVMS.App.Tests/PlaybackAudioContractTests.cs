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
    private const string Xaml = "src/HeliVMS.App/PlaybackWindow.xaml";
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

    /// <summary>
    /// 回放速度預設 1×：0.5× 的預設讓「回放怎麼慢慢的」，也讓喇叭按不下去（非 1× 不播聲音）。
    /// </summary>
    /// <remarks>
    /// 現場回報的修正：速度下拉預設落在第 0 項（0.5×），操作員看到的回放是慢動作，
    /// 點喇叭只會改狀態列、圖示不會變藍——看起來就像「聲音符號壞了」。預設必須是 1×，
    /// XAML 與建構子各設一次（XAML 的 SelectedIndex 在項目還沒建立時可能被忽略）。
    /// </remarks>
    [Fact]
    public void 回放速度預設一倍()
    {
        // XAML：速度下拉的預設選項是第 1 項（1×），不是第 0 項（0.5×）。
        var xaml = SourceContract.Read(Xaml);
        var comboStart = xaml.IndexOf("x:Name=\"SpeedCombo\"", StringComparison.Ordinal);
        Assert.True(comboStart >= 0, "找不到速度下拉。");
        var tagEnd = xaml.IndexOf('>', comboStart);
        var tag = xaml[comboStart..tagEnd];
        Assert.Contains("SelectedIndex=\"1\"", tag);

        // 建構子再設一次（項目尚未建立時 XAML 的選擇可能不生效）。
        // 釘在建構子本體而非整份檔案：喇叭的自動切回也寫同一句，抓整份檔案會空轉全綠。
        Assert.Contains("SpeedCombo.SelectedIndex = 1;", SourceContract.BodyOf(SourceContract.Read(Code), "PlaybackWindow"));

        // 對照：1× 的速度值必須真的是 1（改對應表時不能把 1× 弄丟）。
        Assert.Contains("1 => 1", SourceContract.BodyOf(SourceContract.Read(Code), "GetSpeed"));
    }

    /// <summary>
    /// 非 1× 時點喇叭：切回 1× 並從目前位置重新對齊，而不是叫操作員自己去速度下拉改。
    /// </summary>
    /// <remarks>
    /// 速度一變，原本的音訊窗口（從起播那一刻開始的 PCM）就跟畫面時間對不上了；
    /// 不重新起播的話，聲音會比畫面快或慢，而且越差越多——所以必須重播，
    /// 這也是為什麼這條契約要釘住「切速度」與「重新起播」兩件事一起發生。
    /// </remarks>
    [Fact]
    public void 非一倍速點喇叭會切回一倍並重新對齊()
    {
        var source = SourceContract.Read(Code);
        var toggle = SourceContract.BodyOf(source, "TogglePlaybackAudioAsync");

        Assert.Contains("GetSpeed() - 1", toggle);
        Assert.Contains("SpeedCombo.SelectedIndex = 1", toggle);
        Assert.Contains("PlayFrom(_current, _posInside)", toggle);

        // 播放中改速度仍要把聲音停掉（速度 ≠ 1 不播聲音），圖示也要跟著退回白色。
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
