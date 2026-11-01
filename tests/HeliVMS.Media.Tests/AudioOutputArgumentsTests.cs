namespace HeliVMS.Media.Tests;

/// <summary>
/// 「解碼行程多吐一份 PCM 到本機 UDP」的參數契約。
/// </summary>
/// <remarks>
/// 這段參數是收音能不能用、以及<b>畫面會不會被連累</b>的分界：少了 <c>-map 0:a:0</c>
/// 就沒有音訊；<c>udp://</c> 寫錯就送到別人家的埠；取樣率與播放裝置不一致就會忽快忽慢、
/// 音調偏移，而畫面上完全看不出來。
/// </remarks>
public sealed class AudioOutputArgumentsTests
{
    [Fact]
    public void 只挑音訊軌並送到指定的本機埠()
    {
        var args = AudioOutputArguments.Build(41234, 16000);

        Assert.Equal(["0:a:0"], After(args, "-map"));
        Assert.Equal(["1"], After(args, "-ac"));
        Assert.Equal(["16000"], After(args, "-ar"));
        Assert.Equal(["pcm_s16le"], After(args, "-acodec"));
        Assert.Equal(["s16le"], After(args, "-f"));
        Assert.Equal(["1"], After(args, "-flush_packets"));

        // 最後一項是輸出路徑：本機回送，不會外流；埠要跟接收端綁定的一致。
        Assert.Equal($"udp://127.0.0.1:41234", args[^1]);
        Assert.True(AudioOutputArguments.IsAudioOutputUrl(args[^1]));

        // 沒有 flush_packets 時 rawaudio 會積在 AVIO 緩衝，現場收音會慢上將近一秒。
        Assert.DoesNotContain("-an", args);
    }

    [Fact]
    public void 取樣率會反映在參數上()
    {
        var args = AudioOutputArguments.Build(40000, 48000);

        Assert.Equal(["48000"], After(args, "-ar"));
        Assert.Contains("48000", args);
    }

    [Fact]
    public void 不合法的埠會被擋下()
    {
        // 埠 0＝交給系統挑，但接收端必須先綁定才拿得到埠號，所以 0 一定是用錯了。
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioOutputArguments.Build(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioOutputArguments.Build(70000));
    }

    /// <summary>
    /// 有沒有音訊軌決定要不要掛音訊輸出：猜錯的兩種下場都沒有畫面上的線索，
    /// 該掛沒掛是「明明有麥克風卻聽不到」，亂掛是整個 ffmpeg 行程失敗（連畫面都沒有）。
    /// </summary>
    [Theory]
    [InlineData(0, true, 0)]        // 沒設定收音：就算有麥克風也不掛
    [InlineData(41000, true, 41000)]
    [InlineData(41000, false, 0)]   // 設定了但這台沒有麥克風：絕不能掛
    [InlineData(41000, null, 0)]    // 還沒探測到：寧願晚一點也先不掛
    public void 只有確定有音訊軌時才掛音訊輸出(int configured, bool? hasAudio, int expected) =>
        Assert.Equal(expected, RtspClient.ResolveAudioPort(configured, hasAudio));

    [Fact]
    public void 監看解碼參數在指定埠時才附上音訊()
    {
        var without = RtspClient.BuildDecodeArguments("rtsp://cam/live");
        var with = RtspClient.BuildDecodeArguments("rtsp://cam/live", audioPort: 41000, audioSampleRate: 16000);

        Assert.DoesNotContain(without, arg => AudioOutputArguments.IsAudioOutputUrl(arg));
        Assert.Contains(with, arg => AudioOutputArguments.IsAudioOutputUrl(arg));

        // 影像輸出必須留在原位（-an 只作用於 stdout 那一個輸出），否則連畫面都沒了。
        Assert.Equal(with.Take(without.Count), without);
        Assert.Contains("-an", without);
    }

    private static string[] After(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == option)
            {
                return [args[i + 1]];
            }
        }

        return [];
    }
}
