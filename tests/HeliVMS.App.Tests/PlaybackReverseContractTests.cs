using System.Text.RegularExpressions;
using HeliVMS.Storage;

namespace HeliVMS.App.Tests;

/// <summary>
/// 倒轉播放的接線契約（QCV8001 回放引擎把「快／慢／倒／逐格」列為同一組能力）。
/// </summary>
/// <remarks>
/// <para>
/// 倒轉的失敗幾乎都不會報錯：落點自己算一份（在空隙與接縫上與方向鍵不同）、節拍器在使用者
/// 做別的事時還在拉畫面（看起來像「回放自己亂跳」）、到當日開頭繞回最後一段（看起來像跳時間）。
/// 所以這裡釘的是**接線與單一規則**；落點與收斂本身在 <c>PlaybackReversePumpTests</c>。
/// </para>
/// </remarks>
public sealed class PlaybackReverseContractTests
{
    private const string Code = "src/HeliVMS.App/PlaybackWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/PlaybackWindow.xaml";

    private static readonly string[] Keys =
    [
        "Playback.Reverse",
        "Playback.ReverseTip",
        "Playback.ReverseAtStart",
    ];

    [Fact]
    public void 倒轉鈕存在且有文案()
    {
        var xaml = SourceContract.Read(Xaml);
        Assert.Matches(@"<Button[^>]*x:Name=""ReverseButton""[^>]*Click=""OnReverseClicked""", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"PlaybackReverseButton\"", xaml);

        var i18n = SourceContract.BodyOf(SourceContract.Read(Code), "ApplyI18n");
        Assert.Contains("Playback.Reverse", i18n);
        Assert.Contains("Playback.ReverseTip", i18n);
    }

    [Fact]
    public void 倒轉走純函式的下一步且不自己算落點()
    {
        var tick = SourceContract.BodyOf(SourceContract.Read(Code), "OnReverseTick");

        Assert.Contains("PlaybackReversePump.Next", tick);

        // 落點規則只有一份（PlaybackSeekPlanner）：倒轉直接呼叫它就會有第二份「空隙該去哪裡」
        // 的答案，而兩份遲早不一致——時間軸上明明有那段錄影，倒轉卻跳過它。
        Assert.DoesNotContain("PlaybackSeekPlanner.Resolve", tick);
    }

    [Fact]
    public void 節拍與顯示路徑都與既有接縫共用()
    {
        var source = SourceContract.Read(Code);

        // 節拍長度取自純函式的常數，不寫死毫秒數。
        Assert.Contains("PlaybackReversePump.TickSeconds", source);

        // 顯示走「起播＋首幀後暫停」那條逐幀路徑：倒轉不新增解碼路徑。
        var tick = SourceContract.BodyOf(source, "OnReverseTick");
        Assert.Contains("PlayFrom(step.Landing.Segment, step.Landing.OffsetSeconds)", tick);
        Assert.Contains("_pendingFramePause = true", tick);
    }

    [Fact]
    public void 到當日開頭會停住而不是繞回最後一段()
    {
        var tick = SourceContract.BodyOf(SourceContract.Read(Code), "OnReverseTick");
        var reachedStart = tick.IndexOf("ReachedStart", StringComparison.Ordinal);
        Assert.True(reachedStart >= 0, "倒轉沒有處理『已回到當日開頭』");

        // 早退分支本來就會 StopReverse()，所以要找「ReachedStart 之後」的那一次。
        var stop = tick.IndexOf("StopReverse();", reachedStart, StringComparison.Ordinal);
        Assert.True(stop > reachedStart, "到開頭要停掉倒轉，否則會繞回當天最後一段");
        Assert.Contains("Playback.ReverseAtStart", tick);
    }

    [Theory]
    [InlineData("OnPlayPauseClicked")]
    [InlineData("OnStopClicked")]
    [InlineData("OnBandScrubStart")]
    [InlineData("OnBandClicked")]
    [InlineData("ReloadForPlaybackAsync")]
    [InlineData("OnClosing")]
    public void 使用者操作會中止倒轉(string method)
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), method);

        Assert.NotEqual(string.Empty, body);
        Assert.Contains("StopReverse()", body);
    }

    [Fact]
    public void 倒轉文案三語齊備()
    {
        foreach (var lang in I18n.Languages)
        {
            foreach (var key in Keys)
            {
                Assert.NotEqual(key, I18n.Get(lang, key));
            }
        }
    }
}
