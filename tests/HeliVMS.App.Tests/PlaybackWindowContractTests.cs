using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 回放視窗時間軸與跳轉的整合契約（M84／ARCHITECTURE §8.5）。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試環境，時間軸帶是畫在 Canvas 上的，行為驗不到；
/// 這裡守住兩件曾經真實出錯、而且光看畫面不一定發現的事：
/// </para>
/// <para>
/// （1）時間軸點擊／事件跳轉／跨段跳轉是靠「程式化改選取」帶動左側清單高亮的。
/// 若 <c>OnSegmentSelected</c> 沒有被抑制，每次跳轉都會先從 0 播一段、再被外部指定的秒數打斷，
/// 現場症狀是每跳一次就閃一下畫面並多開一個 ffmpeg 行程（<see cref="SourceContract.BodyOf"/>
/// 掃原始碼，因為這個重複啟動在畫面上一閃而過）。
/// </para>
/// <para>
/// （2）點到「無錄影」的灰底區間以前會默默跳到最後一段，操作員會誤以為那裡有錄影；
/// §8.5 驗收要求無錄影區間點擊必須有明確回饋。
/// </para>
/// </summary>
public sealed class PlaybackWindowContractTests
{
    private const string Code = "src/HeliVMS.App/PlaybackWindow.xaml.cs";

    [Fact]
    public void 程式化選取區段不會觸發自動播放()
    {
        var source = SourceContract.Read(Code);

        var select = SourceContract.BodyOf(source, "SelectSegment");
        Assert.Contains("_suppressSegmentAutoplay = true", select);
        Assert.Contains("SegmentList.SelectedItem", select);

        var onSelected = SourceContract.BodyOf(source, "OnSegmentSelected");
        Assert.Contains("_suppressSegmentAutoplay", onSelected);
    }

    [Theory]
    [InlineData("OnLoaded")]
    [InlineData("OnEventSelected")]
    [InlineData("SeekBy")]
    [InlineData("OnBandClicked")]
    public void 跳轉路徑以SelectSegment改選取狀態(string method)
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), method);
        Assert.Contains("SelectSegment(", body);

        // 直接指派 SelectedItem 會再觸發 OnSegmentSelected，等於多播一段；
        // 跳轉路徑一律走 SelectSegment（會抑制自動播放）。
        Assert.DoesNotContain("SegmentList.SelectedItem =", body);
    }

    [Fact]
    public void 點擊無錄影區間會給出明確回饋而非跳到最後一段()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnBandClicked");
        Assert.Contains("Playback.NoRecordingHere", body);
        Assert.DoesNotContain("_bandSegments[^1]", body);
    }

    [Fact]
    public void 時間軸帶有無錄影灰底且事件標記另闢一列()
    {
        var source = SourceContract.Read(Code);
        Assert.Contains("s_bandEmptyFill", SourceContract.BodyOf(source, "RenderBlocks"));
        Assert.Contains("BandMarkerSize", SourceContract.BodyOf(source, "RenderMarkers"));
    }

    [Fact]
    public void 整天無錄影時時間軸仍鋪灰底且點擊有回饋()
    {
        var source = SourceContract.Read(Code);

        // 灰底必須畫在「有資料才繼續」的判斷之前。否則整天沒錄影時帶子會是空的，
        // 操作員看不出是「這天沒錄」還是「還沒載入」。
        var render = SourceContract.BodyOf(source, "RenderBlocks");
        Assert.True(
            render.IndexOf("s_bandEmptyFill") < render.IndexOf("hasData"),
            "灰底（s_bandEmptyFill）必須先於 hasData 的提前 return 繪製。");

        // 完全沒有 segments 時，點擊也要回報無錄影，而不是直接 return 讓操作員以為點歪了。
        // 只看「方法裡有沒有 NoRecordingHere」是不夠的——灰底區間那條路徑本來就會用到同一個字串；
        // 這裡直接盯住 Count == 0 的那個 if 區塊，確保回饋真的在裡面。
        var click = SourceContract.BodyOf(source, "OnBandClicked");
        var guard = Regex.Match(click, @"_bandSegments\.Count == 0\)\s*\{([^}]*)\}");
        Assert.True(
            guard.Success && guard.Groups[1].Value.Contains("Playback.NoRecordingHere"),
            "OnBandClicked 在 _bandSegments.Count == 0 時必須回報 Playback.NoRecordingHere，而不是直接 return。");
    }

    [Fact]
    public void 回放牆格數切換重建次畫面並重新對齊母鐘()
    {
        var source = SourceContract.Read(Code);

        var onSplit = SourceContract.BodyOf(source, "OnSplitChanged");
        Assert.Contains("RebuildPanes(", onSplit);
        Assert.Contains("SyncPanes(", onSplit);

        // 1 路時整條路徑要與舊的單窗行為等價（不可誤刪成 0 格或預設成 4 格）。
        var split = SourceContract.BodyOf(source, "SplitCount");
        Assert.Contains("1 => 2", split);
        Assert.Contains("2 => 4", split);
        Assert.Contains("_ => 1", split);
    }

    [Fact]
    public void 次畫面以母鐘解算且空隙不硬跳別段()
    {
        var source = SourceContract.Read(Code);

        // 逐格用 PlaybackSyncPlanner 依絕對時刻解算；落在空隙要標示無錄影，不可自行找下一段跳過去
        // ——那會讓兩台看似同步卻在播不同時刻（多路比對最危險的假同步）。
        var sync = SourceContract.BodyOf(source, "SyncPanes");
        Assert.Contains("PlaybackSyncPlanner.Resolve(", sync);
        Assert.Contains("Playback.NoRecordingHere", sync);
        Assert.DoesNotContain("FindNextSegment", sync);

        // 次畫面播到尾不自己跨段，交由母鐘決定（否則每格各跳各的，時間軸就散了）。
        var ended = SourceContract.BodyOf(source, "OnPaneEnded");
        Assert.Contains("pane.Session = null", ended);
        Assert.DoesNotContain("PlayPane(", ended);
    }

    [Fact]
    public void 點次畫面會把該頻道切成主焦點()
    {
        var source = SourceContract.Read(Code);

        Assert.Contains("MouseLeftButtonDown", SourceContract.BodyOf(source, "CreatePane"));
        Assert.Contains("FocusPane(", SourceContract.BodyOf(source, "CreatePane"));

        var focus = SourceContract.BodyOf(source, "FocusPane");
        Assert.Contains("ChannelCombo.SelectedItem", focus);
    }

    [Fact]
    public void 回放牆下拉有接線且不硬編中文()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Contains("x:Name=\"SplitCombo\"", xaml);
        Assert.Contains("SelectionChanged=\"OnSplitChanged\"", xaml);
        Assert.DoesNotMatch(@"[\u4e00-\u9fff]", xaml);
    }
}
