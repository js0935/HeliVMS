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

    [Fact]
    public void 書籤可用B鍵與播放器右鍵加入()
    {
        var source = SourceContract.Read(Code);

        // B 鍵與右鍵都要走到同一個加入路徑；少一個（例如只留右鍵）操作員就少了全鍵盤那條（§8.5）。
        Assert.Contains("Key.B", SourceContract.BodyOf(source, "OnPreviewKeyDown"));
        Assert.Contains("OnAddBookmarkClicked", SourceContract.BodyOf(source, "OnPreviewKeyDown"));

        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Matches(@"<MenuItem[^>]*x:Name=""AddBookmarkMenuItem""[^>]*Click=""OnAddBookmarkClicked""", xaml);
        Assert.Contains("<Border.ContextMenu>", xaml);
    }

    [Fact]
    public void 沒有正在播放的片段時不會靜默新增書籤()
    {
        var source = SourceContract.Read(Code);
        var add = SourceContract.BodyOf(source, "OnAddBookmarkClicked");

        // 沒有 _current 卻仍往下走，會標出一個 00:00 的假書籤；必須先擋住並回報。
        var guard = Regex.Match(add, @"_current is not SegmentRecord seg\)\s*\{([^}]*)\}");
        Assert.True(
            guard.Success && guard.Groups[1].Value.Contains("Playback.BookmarkNoSegment"),
            "OnAddBookmarkClicked 在沒有可播放片段時必須回報 Playback.BookmarkNoSegment 後 return。");
        Assert.Contains("_bookmarkRepo.Add(", add);
    }

    [Fact]
    public void 點書籤會依SelectSegment跳轉且不自行改選取()
    {
        var source = SourceContract.Read(Code);
        var jump = SourceContract.BodyOf(source, "OnBookmarkSelected");

        Assert.Contains("SelectSegment(", jump);
        Assert.DoesNotContain("SegmentList.SelectedItem =", jump);
    }

    [Fact]
    public void 書籤清單可重命名與刪除()
    {
        var source = SourceContract.Read(Code);

        Assert.Contains("UpdateNote(", SourceContract.BodyOf(source, "OnRenameBookmarkClicked"));
        Assert.Contains("Remove(", SourceContract.BodyOf(source, "OnDeleteBookmarkClicked"));

        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Matches(@"<MenuItem[^>]*x:Name=""RenameBookmarkMenuItem""[^>]*Click=""OnRenameBookmarkClicked""", xaml);
        Assert.Matches(@"<MenuItem[^>]*x:Name=""DeleteBookmarkMenuItem""[^>]*Click=""OnDeleteBookmarkClicked""", xaml);
    }

    [Fact]
    public void 時間軸拖曳期間只搬播放頭並在放開時才跳一段()
    {
        var source = SourceContract.Read(Code);

        // 拖曳預覽絕不啟動解碼：每移動一格就多開一個 ffmpeg，手感會直接爛掉，
        // 也讓「拖拽跳轉 < 300ms 出畫」的預算花在幾十個用不到的行程上（§8.5 驗收）。
        var preview = SourceContract.BodyOf(source, "PreviewBandAt");
        Assert.Contains("_cursor", preview);
        Assert.DoesNotContain("PlayFrom(", preview);
        Assert.DoesNotContain("new PlaybackSession", preview);

        Assert.Contains("PreviewBandAt(", SourceContract.BodyOf(source, "OnBandMouseMove"));

        // 真正的跳轉（含跨段）只在放開滑鼠時發生一次。
        var commit = SourceContract.BodyOf(source, "OnBandClicked");
        Assert.Contains("_scrubbing = false", commit);
        Assert.Contains("PlayFrom(", commit);

        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Contains("MouseLeftButtonDown=\"OnBandScrubStart\"", xaml);
        Assert.Contains("MouseMove=\"OnBandMouseMove\"", xaml);
        Assert.Contains("MouseLeftButtonUp=\"OnBandClicked\"", xaml);
    }

    [Fact]
    public void 書籤由倉儲依通道與當日範圍載入()
    {
        var source = SourceContract.Read(Code);

        Assert.Contains("_bookmarkRepo.ListByRange(", SourceContract.BodyOf(source, "ReloadBookmarks"));
        Assert.Contains("ReloadBookmarks(", SourceContract.BodyOf(source, "LoadSegments"));
    }

    /// <summary>
    /// 回放日曆「當天有錄影」粗體＋藍字（§3.4）是視覺狀態，桌面端沒有 WPF 執行期驗不到，
    /// 這裡守住四件一改就錯、而且光看畫面不一定發現的事：
    ///
    /// <para>
    /// （1）標記必須掛 CalendarOpened——彈窗的 Calendar 是開啟當下才建的，掛在別的事件上
    /// 第一次開永遠抓不到本體，標記整批靜默失效（不報錯、畫面只是沒粗體沒藍字）。
    /// </para>
    /// <para>
    /// （2）每一格都必須寫回兩種狀態——CalendarDayButton 會被重複使用，只標不取消的話
    /// 上個月的樣子會跟著按鈕流到下個月（實測換月後舊值仍在）。
    /// </para>
    /// <para>
    /// （3）字色必須設在日字的 TextElement.Foreground 上，不許設在按鈕的 Foreground 上——
    /// 兩者是不同的 DP、模板不接這座橋，設在按鈕上屬性讀值會變、畫面永遠是樣式白
    /// （探針以紅字實測：像素完全不變），正是「看起來有改到、上機沒效果」的失敗樣態。
    /// </para>
    /// <para>
    /// （4）無錄影日必須 ClearValue 退回樣式，不能寫死白色：樣式日後改色，
    /// 被寫死的格子不會跟著改。
    /// </para>
    /// </summary>
    [Fact]
    public void 日曆有錄影日逐格寫回粗體與藍字且字色設在日字上()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Contains("CalendarOpened=\"OnCalendarOpened\"", xaml);

        var source = SourceContract.Read(Code);
        var opened = SourceContract.BodyOf(source, "OnCalendarOpened");
        Assert.Contains("QueueRecordingDayMarks", opened);
        Assert.Contains("DisplayDateChanged", opened);
        Assert.Contains(
            "ApplyRecordingDayMarks",
            SourceContract.BodyOf(source, "QueueRecordingDayMarks"));

        var marks = SourceContract.BodyOf(source, "ApplyRecordingDayMarks");
        Assert.Contains("FontWeights.Bold", marks);
        Assert.Contains("FontWeights.Normal", marks);

        // 口徑必須與 LoadSegments 同源：錯一條（final 過濾或時區算法），標記清單就會對不上。
        Assert.Contains("HasSegmentsInWindows", marks);
        Assert.Contains("ConvertTimeToUtc", marks);

        // 藍字接縫：設在日字上、清本機值退回樣式、且不許改寫成死接縫（按鈕上的 Foreground）。
        Assert.Contains("text.Foreground = s_recordedDayForeground", marks);
        Assert.Contains("TextElement.ForegroundProperty", marks);
        Assert.DoesNotContain("buttons[i].Foreground", marks);
        Assert.Contains("s_recordedDayForeground", source);

        // 找不到日字就整段跳過，藍字會靜默消失——DayText 必須真的走視覺樹找 TextBlock。
        var dayText = SourceContract.BodyOf(source, "DayText");
        Assert.Contains("VisualTreeHelper.GetChild", dayText);
        Assert.Contains("is TextBlock text", dayText);

        // 而且 null 早退不許提到查找之前：return null 放最前面，下面的查找程式碼還在、
        // 字串檢查全過，但實際執行永遠回 null，藍字整批靜默不生效。
        Assert.True(
            dayText.IndexOf("is TextBlock text", StringComparison.Ordinal)
                < dayText.IndexOf("return null", StringComparison.Ordinal),
            "DayText 必須先找日字再回 null；null 放在查找之前會讓藍字靜默失效");
    }
}
