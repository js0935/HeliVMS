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

        var focus = SourceContract.BodyOf(source, "FocusPaneAsync");
        Assert.Contains("SwitchChannelAsync(pane.Channel.Id)", focus);

        // 換台是非同步的（先收檔、再載入索引）；續播若搶在載入之前，兩秒後就被那次
        // LoadSegments 的 StopPlayback 停掉，現場看到的是「放大後畫面停住」。
        var switchAt = focus.IndexOf("SwitchChannelAsync(", StringComparison.Ordinal);
        var resumeAt = focus.IndexOf("ResumePlaybackAt(", StringComparison.Ordinal);
        Assert.True(
            switchAt >= 0 && resumeAt > switchAt,
            "FocusPaneAsync 必須等 SwitchChannelAsync 完成（新頻道索引已載入）才續播。");
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
        // 搬播放頭走 PlaceCursor（直線＋菱形帽一起搬）；直接改 _cursor 會漏掉菱形帽。
        var preview = SourceContract.BodyOf(source, "PreviewBandAt");
        Assert.Contains("PlaceCursor", preview);
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
    /// 關鍵幀索引必須真的被回放視窗用起來（§3.4「播放頭為關鍵幀吸附」）。
    ///
    /// <para>
    /// 這張表原本只建了 schema：索引沒人寫、刻度沒人畫，整條功能是死的而沒有任何錯誤訊息。
    /// 這裡守住四個接縫：
    /// </para>
    /// <para>
    /// （1）<c>LoadSegments</c> 要先同步載入既有索引再丟背景補建缺的——只做背景的話，
    /// 每次換日／換頻道都要等探測跑完才看得到刻度，而且重進同一天會閃一下才出現。
    /// </para>
    /// <para>
    /// （2）背景補建必須在 <c>Task.Run</c> 裡跑 ffprobe：對每個區段探測是百毫秒級
    /// （4 路 × 一天數十段＝數十秒），同步跑會讓換日／換頻道整個卡住。
    /// </para>
    /// <para>
    /// （3）世代編號必須遞增且被背景作業檢查：連續換日時舊探測的結果會畫到新的一天上，
    /// 操作員看到的是昨天錄影位置的刻度疊在今天的色帶上。
    /// </para>
    /// <para>
    /// （4）探測回空清單時不可寫入索引：寫了空的，<c>ListMissingIndexIds</c> 就再也不認為
    /// 這一段缺索引，壞檔或暫時讀不到的段永遠補不上。
    /// </para>
    /// <para>
    /// （5）既有索引要一趟 <c>ListForSegments</c> 查完，不可逐段查：一天的段數可達上千，
    /// 逐段查是上千次來回，而且全發生在換日／換頻道的 UI 執行緒上。
    /// </para>
    /// </summary>
    [Fact]
    public void 關鍵幀索引在背景補建且缺索引才探測且畫成刻度()
    {
        var source = SourceContract.Read(Code);

        var load = SourceContract.BodyOf(source, "LoadSegments");
        Assert.Contains("LoadKeyframeCache(", load);
        Assert.Contains("StartKeyframeIndexing(", load);
        Assert.Contains("Interlocked.Increment", load);

        var cache = SourceContract.BodyOf(source, "LoadKeyframeCache");
        Assert.Contains("ListForSegments(", cache);

        // 逐段 _keyframes.List(id) 是 N 次來回；一天上千段時換日／換頻道全卡在 UI 執行緒上。
        Assert.DoesNotContain("_keyframes.List(", cache);

        var index = SourceContract.BodyOf(source, "StartKeyframeIndexing");
        Assert.Contains("Task.Run(", index);
        Assert.Contains("ListMissingIndexIds(", index);
        Assert.Contains("KeyframeProbe.Probe(", index);
        Assert.Contains("_keyframes.Replace(", index);
        Assert.Contains("Volatile.Read", index);
        Assert.Contains("rows.Count > 0", index);

        // 刻度畫在 RenderBlocks 的同一個漏斗（縮放/換日/重畫都會跟上），密度判斷走 Storage 的純函式。
        Assert.Contains("RenderKeyframeTicks(", SourceContract.BodyOf(source, "RenderBlocks"));
        var ticks = SourceContract.BodyOf(source, "RenderKeyframeTicks");
        Assert.Contains("_keyframeCache", ticks);
        Assert.Contains("PlaybackKeyframeDensity.ShouldDraw", ticks);
        Assert.Contains("KeyframeTickMinPx", ticks);
        Assert.Contains("BandCanvas.Children.Add", ticks);
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

    /// <summary>
    /// 主時間列刻度與滾輪縮放（§3.4「時間刻度依縮放層級切換」、§8.5「主時間列：刻度／滾輪無級縮放，
    /// 縮放中心跟隨游標」）。桌面端沒有 WPF 執行期，刻度畫面驗不到，這裡守住三個一改就錯的接縫：
    ///
    /// <para>
    /// （1）縮放必須掛在帶子的 MouseWheel 上、刻度必須畫在 RulerCanvas——缺任何一個，
    /// 滾輪沒反應或刻度列永遠空白，而且都不會報錯。
    /// </para>
    /// <para>
    /// （2）帶子的點擊與游標換算必須走檢視窗（<c>TimeAtFraction</c>/<c>FractionOf</c>）——
    /// 退回舊的「全日窗比例」公式（frac×1440）的話，縮放後點擊會跳到完全不同的時刻：
    /// 畫面停在 10:30、跳轉卻落在 10:00 附近，比沒縮放更容易誤判錄影位置。
    /// </para>
    /// <para>
    /// （3）載入新的一天必須重設為全日窗——留著前一日的縮放窗看新的一天，
    /// 帶子上是一片對不到資料的空白。
    /// </para>
    /// </summary>
    [Fact]
    public void 時間軸有主時間列刻度與滾輪縮放且帶子換算走檢視窗()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Contains("MouseWheel=\"OnBandMouseWheel\"", xaml);
        Assert.Contains("x:Name=\"RulerCanvas\"", xaml);

        var source = SourceContract.Read(Code);

        var wheel = SourceContract.BodyOf(source, "OnBandMouseWheel");
        Assert.Contains("PlaybackTimelineScale.Zoom", wheel);
        Assert.Contains("RenderBlocks", wheel);

        var ruler = SourceContract.BodyOf(source, "RenderRuler");
        Assert.Contains("RulerCanvas", ruler);
        Assert.Contains("PlaybackTimelineScale.Ticks", ruler);

        var click = SourceContract.BodyOf(source, "OnBandClicked");
        Assert.Contains("TimeAtFraction", click);
        Assert.DoesNotContain("AddMinutes(frac * 1440)", click);

        var cursor = SourceContract.BodyOf(source, "UpdateCursor");
        Assert.Contains("FractionOf", cursor);

        var load = SourceContract.BodyOf(source, "LoadSegments");
        Assert.Contains("PlaybackTimelineScale.FullDay", load);
    }

    /// <summary>
    /// 單擊次畫面必須放大成單畫面（§8.5 主焦點，操作者回饋）：點擊路徑要「格數切到 1 →
    /// 頻道換成點到那台 → 從原播放頭續播」，三者缺一都有對應的壞掉畫面。
    /// <para>
    /// （1）FocusPane 沒把 SplitCombo 切到 1：點次畫面只是把焦點換到左上角、牆還維持原格數，
    /// 操作員要的「放大成單畫面」沒發生。
    /// </para>
    /// <para>
    /// （2）沒記播放頭就換頻道：ChannelCombo 一動就同步 LoadSegments→StopPlayback，
    /// 放大後回到停在原點——「在多路回放時單擊放大」的瞬間反而把畫面停掉。
    /// </para>
    /// <para>
    /// （3）次畫面沒掛 MouseLeftButtonDown（或沒接到 FocusPane）：整組點擊放大靜默失效，
    /// 游標還是手型、提示也在，對操作員是「介面說可以點、點了沒反應」。
    /// </para>
    /// <para>
    /// （4）主畫面格（焦點台，通常是攝影機 01）沒掛點擊：02～04 點了會放大、01 不會——
    /// 焦點台永遠排在主格、沒有次畫面格，漏掉這條就是操作員回報的「01 不行」。
    /// </para>
    /// </summary>
    [Fact]
    public void 單擊次畫面放大成單畫面且播放頭不斷()
    {
        var source = SourceContract.Read(Code);

        var focus = SourceContract.BodyOf(source, "FocusPaneAsync");
        Assert.Contains("SplitCombo.SelectedIndex = 0", focus);
        Assert.Contains("SwitchChannelAsync(", focus);
        Assert.Contains("ResumePlaybackAt", focus);
        Assert.Contains("_current.StartUtc.AddSeconds(_posInside)", focus);

        // 續播一定要排在換台（含載入索引）之後，否則會被那次載入停掉。
        Assert.True(
            focus.IndexOf("SwitchChannelAsync(", StringComparison.Ordinal)
                < focus.IndexOf("ResumePlaybackAt(", StringComparison.Ordinal),
            "放大後續播必須等新頻道載入完成。");

        var switchChannel = SourceContract.BodyOf(source, "SwitchChannelAsync");
        Assert.Contains("LoadChannels()", switchChannel);
        Assert.Contains("await ReloadForPlaybackAsync()", switchChannel);

        var resume = SourceContract.BodyOf(source, "ResumePlaybackAt");
        Assert.Contains("SelectSegment", resume);
        Assert.Contains("PlayFrom", resume);

        var create = SourceContract.BodyOf(source, "CreatePane");
        Assert.Contains("MouseLeftButtonDown", create);
        Assert.Contains("FocusPane", create);
        Assert.Contains("Playback.PaneTip", create);

        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Contains("MouseLeftButtonDown=\"OnMainPlayerClicked\"", xaml);

        var mainClick = SourceContract.BodyOf(source, "OnMainPlayerClicked");
        Assert.Contains("_panes.Count == 0", mainClick);
        Assert.Contains("SplitCombo.SelectedIndex = 0", mainClick);
    }

    /// <summary>
    /// 縮放滑桿必須與滾輪共用同一個檢視窗（§8.5 主時間列「刻度 / 縮放滑桿 / 滾輪無級縮放」）：
    /// 滑桿是粗調、滾輪是跟游標的細調，兩者各管各的窗就會出現「滑桿在中段、畫面卻貼著秒級」的錯位。
    /// <para>
    /// （1）滑桿沒掛 ValueChanged、或 handler 沒換窗重畫：拖了完全沒反應。
    /// </para>
    /// <para>
    /// （2）滑桿值沒經 RenderBlocks 單一漏斗回同步：滾輪縮放後滑桿停在舊位置，
    /// 操作員再拖一下會從錯誤的窗寬起跳。
    /// </para>
    /// </summary>
    [Fact]
    public void 縮放滑桿與滾輪共用同一檢視窗()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Contains("x:Name=\"ZoomSlider\"", xaml);
        Assert.Contains("ValueChanged=\"OnZoomSliderChanged\"", xaml);

        var source = SourceContract.Read(Code);

        var slider = SourceContract.BodyOf(source, "OnZoomSliderChanged");
        Assert.Contains("SpanSecondsFromSliderValue", slider);
        Assert.Contains("RenderBlocks", slider);
        Assert.Contains("_view =", slider);

        var funnel = SourceContract.BodyOf(source, "RenderBlocks");
        Assert.Contains("SyncZoomSlider", funnel);

        var sync = SourceContract.BodyOf(source, "SyncZoomSlider");
        Assert.Contains("SliderValueFromSpan", sync);
        Assert.Contains("_syncingZoomSlider", sync);
    }

    /// <summary>
    /// 通道色帶列必須跟著牆的組成與檢視窗重畫（§8.5「色帶按通道橫向排列，多路同時比對一目了然」）：
    /// 每一格（主＋次）在帶子上方各佔一排，畫該通道當日的錄影涵蓋；主畫面邊框與色帶同色。
    /// <para>
    /// （1）色帶列沒掛在 RenderBlocks 漏斗：滾輪縮放或換日後色帶停在舊窗，跟主帶子對不齊。
    /// </para>
    /// <para>
    /// （2）色帶列沒畫次畫面（pane.Segments）：只剩焦點台一排，多路比對失去意義——
    /// 操作員要的就是「一眼看到哪台哪段沒錄」。
    /// </para>
    /// <para>
    /// （3）牆邊框沒上通道色：色帶列對不回畫面，只能靠 OSD 讀名字逐格比對。
    /// </para>
    /// </summary>
    [Fact]
    public void 通道色帶列跟隨牆的組成與檢視窗()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Contains("x:Name=\"ChannelBandsCanvas\"", xaml);

        var source = SourceContract.Read(Code);

        var bands = SourceContract.BodyOf(source, "RenderChannelBands");
        Assert.Contains("ChannelBandsCanvas", bands);
        Assert.Contains("_bandSegments", bands);
        Assert.Contains("pane.Segments", bands);
        Assert.Contains("View()", bands);

        var funnel = SourceContract.BodyOf(source, "RenderBlocks");
        Assert.Contains("RenderChannelBands", funnel);

        var rebuild = SourceContract.BodyOf(source, "RebuildPanes");
        Assert.Contains("RenderChannelBands", rebuild);
        Assert.Contains("ChannelBrush", rebuild);

        var create = SourceContract.BodyOf(source, "CreatePane");
        Assert.Contains("ChannelBrush", create);
    }

    /// <summary>
    /// 選擇區間窗必須能重播與匯出（§8.5「拖拽選取範圍 → 立即可重播此段 / 匯出此段」）：
    /// Shift+左鍵拖出區間、右鍵選單兩項動作、重播越過盡頭自動停、匯出精靈預填起止。
    /// <para>
    /// （1）帶子拖曳沒分流 Shift：選取與拖曳預覽互搶同一個手势，操作員永遠選不出區間。
    /// </para>
    /// <para>
    /// （2）重播沒設/沒檢查 _rangeStopUtc：會一路播到片段尾，「重播此段」變成「重播這天」。
    /// </para>
    /// <para>
    /// （3）匯出精靈沒預填起止：選好的區間還要手打日期時間，選取形同虛設。
    /// </para>
    /// </summary>
    [Fact]
    public void 帶子拖拽選取區間可重播與匯出()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Contains("Click=\"OnReplayRangeClicked\"", xaml);
        Assert.Contains("Click=\"OnExportRangeClicked\"", xaml);

        var source = SourceContract.Read(Code);

        var scrub = SourceContract.BodyOf(source, "OnBandScrubStart");
        Assert.Contains("ModifierKeys.Shift", scrub);
        Assert.Contains("_selecting = true", scrub);

        var move = SourceContract.BodyOf(source, "OnBandMouseMove");
        Assert.Contains("UpdateRangeSelection", move);

        var clicked = SourceContract.BodyOf(source, "OnBandClicked");
        Assert.Contains("_selecting", clicked);

        var blocks = SourceContract.BodyOf(source, "RenderBlocks");
        Assert.Contains("RenderSelectionWindow", blocks);

        var replay = SourceContract.BodyOf(source, "OnReplayRangeClicked");
        Assert.Contains("PlayFrom", replay);
        Assert.Contains("_rangeStopUtc", replay);

        var frame = SourceContract.BodyOf(source, "OnFrame");
        Assert.Contains("_rangeStopUtc", frame);

        var export = SourceContract.BodyOf(source, "OnExportRangeClicked");
        Assert.Contains("range.StartUtc", export);
        Assert.Contains("ExportWindow", export);

        var exportWindow = SourceContract.Read("src/HeliVMS.App/ExportWindow.xaml.cs");

        // 參數存在不等於有預填：斷言要卡在「把起止寫進時間欄」那一段，
        // 只留參數不接線時匯出精靈仍是預設整天，選取形同虛設。
        Assert.Contains("focusFromUtc is { } from", exportWindow);
        Assert.Contains("StartTime.Text = fromLocal.ToString", exportWindow);
        Assert.Contains("EndTime.Text = toLocal.ToString", exportWindow);
    }

    /// <summary>
    /// 錄影是一段一個檔，「重播所選區間」選的範圍通常跨好幾段。舊行為只播起點所在那一段，
    /// 到段尾就停住並回報「區間播畢」：選了 10:00–10:30，實際只看得到 10:00–10:05，
    /// 而畫面上沒有任何線索說明少了什麼——當成佐證播放時這就是錯的影片。
    /// </summary>
    [Fact]
    public void 區間重播跨段會接著播下一段並保留結束上界()
    {
        var source = SourceContract.Read(Code);
        var ended = SourceContract.BodyOf(source, "OnEnded");

        Assert.Contains("_rangeStopUtc is { } rangeStopUtc", ended);
        Assert.Contains("FindNextSegment(seg)", ended);
        Assert.Contains("nextInRange.StartUtc < rangeStopUtc", ended);

        // 上界必須在 PlayFrom 之後補回來：PlayFrom 會清掉它（屬於上一次操作），
        // 少這一行就變成「一路播到當日最後一段」。
        var play = ended.IndexOf("PlayFrom(nextInRange, 0)", StringComparison.Ordinal);
        var restore = ended.IndexOf("_rangeStopUtc = rangeStopUtc;", StringComparison.Ordinal);
        Assert.True(play >= 0 && restore > play, "跨段續播要先把播放上界補回來，再離開這個分支。");
    }

    /// <summary>
    /// 選取區間內已經沒有下一段時，重播要在這裡收尾，不能掉進「自動續播」。
    /// </summary>
    /// <remarks>
    /// 自動續播看的是整天的下一段，不是選取範圍的下一段：區間播完而後面還有錄影時，
    /// 它會接著播一段完全在選取之外的影片（第一格就撞上播放上界再停住），畫面上閃過
    /// 一段與選取無關的畫面，接著才說「區間播畢」——操作員會以為自己選錯了段。
    /// </remarks>
    [Fact]
    public void 區間播完不會接著播選取範圍外的一段()
    {
        var source = SourceContract.Read(Code);
        var ended = SourceContract.BodyOf(source, "OnEnded");

        var autoAdvance = ended.IndexOf("AutoAdvanceCheck.IsChecked == true", StringComparison.Ordinal);
        Assert.True(autoAdvance > 0, "找不到自動續播分支。");

        // 區間分支必須在自動續播之前就把畫面收乾淨並離開方法。
        var beforeAutoAdvance = ended[..autoAdvance];
        Assert.Contains("Playback.RangeEnded", beforeAutoAdvance);
        Assert.Contains("StopSync()", beforeAutoAdvance);
        Assert.Contains("SetIdleUi()", beforeAutoAdvance);
    }

    /// <summary>
    /// §8.5 回放清單的其餘可達項：16× 速度、1–4 數字鍵選路、時間跳轉、右鍵快照。
    /// <para>
    /// （1）速度選單加了 16× 但 <c>GetSpeed</c> 沒對應索引：畫面顯示 16× 實際仍以 1× 播，
    /// 越過整點對事件時對不上。
    /// </para>
    /// <para>
    /// （2）數字鍵沒改 <c>ChannelCombo.SelectedIndex</c>：牆前面只能回頭用滑鼠點下拉，
    /// 全螢幕操作時等於沒有這個快捷鍵。
    /// </para>
    /// <para>
    /// （3）時間跳轉沒查「該時刻是否真的在錄影範圍內」：跳去鄰近片段會讓操作員以為
    /// 自己看的是所輸入時刻的畫面。
    /// </para>
    /// <para>
    /// （4）快照沒接 PngBitmapEncoder：右鍵選單出現卻存不出檔案。
    /// </para>
    /// </summary>
    [Fact]
    public void 回放整合清單有十六倍速數字鍵選路時間跳轉與右鍵快照()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Contains("Content=\"16×\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"PlaybackTimeJumpBox\"", xaml);
        Assert.Contains("Click=\"OnTimeJumpClicked\"", xaml);
        Assert.Contains("Click=\"OnSnapshotClicked\"", xaml);

        var source = SourceContract.Read(Code);

        var speed = SourceContract.BodyOf(source, "GetSpeed");
        Assert.Contains("5 => 16", speed);

        var keys = SourceContract.BodyOf(source, "OnPreviewKeyDown");
        Assert.Contains("Key.D1", keys);
        Assert.Contains("Key.NumPad1", keys);
        Assert.Contains("ChannelCombo.SelectedIndex", keys);

        var jump = SourceContract.BodyOf(source, "JumpToLocalTime");
        Assert.Contains("ConvertTimeToUtc", jump);
        Assert.Contains("PlayFrom", jump);

        // §8.5 規格給的是完整日期時間（2026-09-12 14:30:00）；解析本身在 Shared
        // （HeliVMS.Shared.Tests.PlaybackTimeJumpTests 有行為測試），這裡卡視窗接的是它，
        // 免得日後又在視窗裡長出第二套解析、兩邊格式不同步。
        Assert.Contains("PlaybackTimeJump.TryParse", jump);
        Assert.Contains("DatePick.SelectedDate = requestedDay", jump);

        // 按鈕／Enter 兩條路都要真的接到跳轉核心：只留 Click 屬性而事件空殼，
        // 畫面看起來有按鈕，按了卻沒反應（上一輪匯出預填就是這樣被斷言漏掉的）。
        Assert.Contains("JumpToLocalTime", SourceContract.BodyOf(source, "OnTimeJumpClicked"));
        Assert.Contains("JumpToLocalTime", SourceContract.BodyOf(source, "OnTimeJumpBoxKeyDown"));

        var snapshot = SourceContract.BodyOf(source, "OnSnapshotClicked");
        Assert.Contains("PngBitmapEncoder", snapshot);
        Assert.Contains("encoder.Save(", snapshot);
        Assert.Contains("snapshots", snapshot);

        var i18n = SourceContract.BodyOf(source, "ApplyI18n");
        Assert.Contains("Snapshot.Capture", i18n);
    }

    /// <summary>
    /// 回放牆的多路容量與拖入即播（§8.5）：分割選單多了 9／16，但同時能播的路數受頻道數夾限，
    /// 並且把即時監看的畫面拖進來就能直接開播。
    /// <para>
    /// （1）只加分割選單、容量沒算：選 16 而只有 4 台時畫面只長出 4 格卻毫無說明，
    /// 操作員會以為剩下的格子壞了。
    /// </para>
    /// <para>
    /// （2）拖放兩端各寫一份格式字串：任一邊打錯字就是「拖了完全沒反應」，
    /// 沒有錯誤也沒有日誌，所以兩端都必須引用 <c>DragFormats.ChannelId</c>。
    /// </para>
    /// <para>
    /// （3）拖進來只換台不開播：§8.5 寫的是「立即回放」，停在靜止畫面等於沒做。
    /// </para>
    /// </summary>
    [Fact]
    public void 回放牆有九與十六分割且容量受限且可拖入即播()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Contains("<ComboBoxItem Content=\"9\"/>", xaml);
        Assert.Contains("<ComboBoxItem Content=\"16\"/>", xaml);
        Assert.Contains("AllowDrop=\"True\"", xaml);
        Assert.Contains("Drop=\"OnPlaybackDrop\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"PlaybackWallCapacity\"", xaml);

        var source = SourceContract.Read(Code);

        var split = SourceContract.BodyOf(source, "SplitCount");
        Assert.Contains("3 => 9", split);
        Assert.Contains("4 => 16", split);

        var geometry = SourceContract.BodyOf(source, "ApplyPlayerFrameGeometry");
        Assert.Contains("PlaybackWallCapacity.Effective", geometry);
        Assert.Contains("WallCapacityText", geometry);

        var drop = SourceContract.BodyOf(source, "OnPlaybackDrop");
        Assert.Contains("FocusChannelAndPlay", drop);

        var play = SourceContract.BodyOf(source, "FocusChannelAndPlayAsync");
        Assert.Contains("PlayFrom", play);
        Assert.Contains("SwitchChannelAsync(", play);

        // §8.5「立即回放」：先把頻道切過去並載入當日索引，才開播。倒過來的話，
        // 開播後兩秒被載入停掉，畫面就停在第一幀。
        Assert.True(
            play.IndexOf("SwitchChannelAsync(", StringComparison.Ordinal)
                < play.IndexOf("PlayFrom(", StringComparison.Ordinal),
            "拖入回放必須等新頻道索引載入完成才開播。");

        var pick = SourceContract.BodyOf(source, "TryGetDroppedChannelId");
        Assert.Contains("DragFormats.ChannelId", pick);

        var main = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");
        var move = SourceContract.BodyOf(main, "OnCellMouseMove");
        Assert.Contains("ModifierKeys.Control", move);
        Assert.Contains("DragDrop.DoDragDrop", move);
        Assert.Contains("DragFormats.ChannelId", move);
    }

    /// <summary>
    /// 播放頭是直線加頂端菱形（§8.5「播放頭(菱形)」）。
    /// <para>
    /// 只有直線時，操作員看到一條白線卻看不出頭在哪一端，也容易與選擇區間窗的方形區塊混淆。
    /// 菱形帽必須與線身一起搬（<c>PlaceCursor</c>），分開搬會在只改到一邊時讓菱形脫離線頂。
    /// </para>
    /// </summary>
    [Fact]
    public void 播放頭是直線加頂端菱形且兩者一起搬動()
    {
        var source = SourceContract.Read(Code);

        Assert.Contains("CursorCapSize", source);
        Assert.Contains("RotateTransform(45", source);

        var place = SourceContract.BodyOf(source, "PlaceCursor");
        Assert.Contains("Canvas.SetLeft(_cursor,", place);
        Assert.Contains("Canvas.SetLeft(_cursorCap,", place);

        // 隱藏路徑也要整組一起藏：只藏線身會留下孤兒菱形漂在帶子上。
        var hide = SourceContract.BodyOf(source, "HideCursor");
        Assert.Contains("_cursor,", hide);
        Assert.Contains("_cursorCap", hide);

        Assert.Contains("PlaceCursor", SourceContract.BodyOf(source, "UpdateCursor"));
        Assert.Contains("HideCursor", SourceContract.BodyOf(source, "UpdateCursor"));
        Assert.Contains("PlaceCursor", SourceContract.BodyOf(source, "PreviewBandAt"));

        // 重畫帶子時兩者都要保留，任一被清掉就再也回不來（RenderBlocks 會移除其他孩子）。
        Assert.Contains("_cursorCap", SourceContract.BodyOf(source, "RenderBlocks"));
    }

    /// <summary>
    /// §8.5 收尾：可收合側欄，以及選擇區間窗的起止手柄。
    /// <para>
    /// （1）側欄收合只改按鈕文字而欄寬照舊時，按下去畫面一格都沒變大——9／16 分割最需要的
    /// 就是這些寬度，所以斷言要卡在欄寬與可見性，不是只有事件有接。
    /// </para>
    /// <para>
    /// （2）手柄的命中與拖曳計算在 <c>HeliVMS.Shared.PlaybackRangeHandle</c>（另有行為測試）；
    /// 這裡只卡「視窗端接的是那份計算」，避免又在自己身上寫第二套而兩邊行為不同步。
    /// </para>
    /// </summary>
    [Fact]
    public void 側欄可收合且選擇區間可用起止手柄精修()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");
        Assert.Contains("x:Name=\"SideColumn\"", xaml);
        Assert.Contains("x:Name=\"SidePanel\"", xaml);
        Assert.Contains("Click=\"OnSidebarToggleClicked\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"PlaybackSidebarToggle\"", xaml);

        var source = SourceContract.Read(Code);

        var toggle = SourceContract.BodyOf(source, "OnSidebarToggleClicked");
        Assert.Contains("SideColumn.Width", toggle);
        Assert.Contains("GridLength(0)", toggle);
        Assert.Contains("Visibility.Collapsed", toggle);
        Assert.Contains("ApplyPlayerFrameGeometry", toggle);

        var scrub = SourceContract.BodyOf(source, "OnBandScrubStart");
        Assert.Contains("PlaybackRangeHandle.HitEdge", scrub);
        Assert.Contains("_rangeResizeEdge", scrub);

        var resize = SourceContract.BodyOf(source, "ResizeRangeSelection");
        Assert.Contains("PlaybackRangeHandle.Drag", resize);

        var move = SourceContract.BodyOf(source, "OnBandMouseMove");
        Assert.Contains("ResizeRangeSelection", move);

        // 手柄要看得到才抓得到：兩端各一條亮色豎條。
        Assert.Contains("GripWidth", SourceContract.BodyOf(source, "RenderSelectionWindow"));

        var i18n = SourceContract.BodyOf(source, "ApplyI18n");
        Assert.Contains("SidebarToggleButton", i18n);
        Assert.Contains("Playback.SidebarHide", i18n);
    }

    /// <summary>
    /// 分割下拉在 XAML 初始化期間的 SelectionChanged 不可重建回放牆（2026-10-19 事故）。
    ///
    /// <para>
    /// SplitCombo 帶 SelectedIndex="0"：InitializeComponent 建樹到那格時 EndInit 就送出
    /// SelectionChanged → OnSplitChanged → RebuildPanes，此刻 PlayerFrame／PlayerArea 還沒建立，
    /// NullReferenceException 被 DispatcherUnhandledException 吞掉（Handled = true），
    /// 現場症狀是「按回放完全沒反應」——沒有對話框、沒有畫面，只有 helivms-crash.log
    /// 裡一行 RebuildPanes ← OnSplitChanged ← InitializeComponent。
    /// </para>
    /// <para>
    /// 斷言卡三點：XAML 的觸發來源還在（這條路徑真實存在）、handler 在 rebuild 前早退、
    /// 建構子在 InitializeComponent 完成後才放行。去掉任一點事故就回來。
    /// </para>
    /// </summary>
    [Fact]
    public void 分割選單在XAML初始化期間不重建畫面以免回放視窗開不起來()
    {
        var xaml = SourceContract.Read("src/HeliVMS.App/PlaybackWindow.xaml");

        // 觸發來源：SplitCombo 一建好就帶選取（牆開窗即為 1 格），會在 InitializeComponent 期間送出事件。
        Assert.Matches(@"x:Name=""SplitCombo""[\s\S]*?SelectedIndex=""0""[\s\S]*?SelectionChanged=""OnSplitChanged""", xaml);

        var source = SourceContract.Read(Code);
        var onSplit = SourceContract.BodyOf(source, "OnSplitChanged");

        // 早退必須是真的 if 判斷式，不是只出現在註解裡；且必須在重建之前。
        var guard = onSplit.IndexOf("if (!_uiReady)", StringComparison.Ordinal);
        Assert.True(guard >= 0, "OnSplitChanged 必須在 _uiReady 就緒前早退，否則 InitializeComponent 期間會 NRE。");
        Assert.True(
            guard < onSplit.IndexOf("RebuildPanes(", StringComparison.Ordinal),
            "_uiReady 的早退必須在 RebuildPanes 之前；放在後面擋不住建樹期間的那次重建。");

        var ready = source.IndexOf("_uiReady = true", StringComparison.Ordinal);
        Assert.True(
            ready > source.IndexOf("InitializeComponent()", StringComparison.Ordinal),
            "_uiReady 只能在 InitializeComponent 完成後置位；提前放行等於沒有守衛。");
    }

    /// <summary>
    /// 索引對帳：索引說有、磁碟上卻不存在的段（幽靈段）必須在三條列段路徑上都被濾掉，
    /// 而且要就地寫回索引，讓日曆與用量統計下一輪自己修正（2026-10 使用者回報「D 磁碟
    /// 錄影資料刪了、回放清單還列著」）。
    ///
    /// <para>
    /// （1）只有 LoadSegments 接：開日清單看得到實情，但播放尾端跨段（FindNextSegment）
    /// 會跳到幽靈段——ffmpeg 打不開就結束，使用者只看到「按了沒反應」，這正是投訴的第二幕。
    /// </para>
    /// <para>
    /// （2）只剩次畫面的 LoadPaneSegments 沒接：多路比對時次畫面永遠是空的，母畫面卻有，
    /// 操作員會以為那台攝影機當天沒錄。
    /// </para>
    /// <para>
    /// （3）提示不講清楚：清單突然變短，不解釋是「檔案不見了」而不是「沒有錄影」，
    /// 第一反應就是系統壞了。磁碟掉線（Unverified）與檔案真的不在（Missing）必須分開講，
    /// 否則一顆拔掉的資料碟會被說成錄影全部遺失。
    /// </para>
    /// <para>
    /// （4）對帳失敗（權限、磁碟忙碌）不可讓回放視窗打不開：寧可暫時多列一段，也不要
    /// 整個視窗只剩錯誤訊息。
    /// </para>
    /// </summary>
    [Fact]
    public void 回放三條列段路徑都會濾掉幽靈段並解釋缺段()
    {
        var source = SourceContract.Read(Code);

        // ReconcileSegments 的簽章帶 tuple 回傳型別（`private (…) ReconcileSegments(`），
        // 不符合 SourceContract 的方法簽章樣式，故直接掃原始碼。
        Assert.Contains("_reconciler.VerifyExisting(", source);
        Assert.Contains("report.MarkedMissing", source);
        Assert.Contains("report.Unverified", source);

        // 逐檔檢查失敗時原樣放行：多列一段也不要讓整個回放視窗只剩錯誤訊息。
        Assert.Contains("return (rows, 0, 0);", source);

        var callers = Regex.Matches(source, @"ReconcileSegments\(")
            .Select(m => SourceContract.EnclosingMethod(source, m.Index))
            .ToHashSet();
        Assert.Contains("LoadSegments", callers);
        Assert.Contains("FindNextSegment", callers);
        Assert.Contains("LoadPaneSegments", callers);

        var load = SourceContract.BodyOf(source, "LoadSegments");
        Assert.Contains("Playback.MissingSegments", load);
        Assert.Contains("Playback.DiskNotReady", load);
    }

    /// <summary>
    /// 日曆粗體前必須先做資料夾層級的對帳，否則「使用者刪掉整天的資料夾」要等最多一小時的
    /// 背景對帳才會反映，而日曆正是操作員判斷「這天有沒有錄影」的第一個畫面——讓他等就是在騙他。
    ///
    /// <para>
    /// 這裡守住的接縫：呼叫必須發生在 <c>HasSegmentsInWindows</c> 之前（先標記再查，粗體才會
    /// 當場消失），且必須走 <c>ListFinalFoldersInRange</c> 把查詢收斂到本月份格位的範圍，
    /// 不能對整個歷史掃資料夾。對帳丟例外時不可讓日曆標不出來。
    /// </para>
    /// </summary>
    [Fact]
    public void 日曆標記前先對帳本月份資料夾存在()
    {
        var source = SourceContract.Read(Code);

        var mark = SourceContract.BodyOf(source, "ApplyRecordingDayMarks");
        var reconcileAt = mark.IndexOf("ReconcileDayFolderExistence", StringComparison.Ordinal);
        var queryAt = mark.IndexOf("HasSegmentsInWindows", StringComparison.Ordinal);
        Assert.True(reconcileAt >= 0, "ApplyRecordingDayMarks 必須先對帳資料夾存在");
        Assert.True(
            reconcileAt < queryAt,
            "對帳必須在 HasSegmentsInWindows 之前：先查再標會讓被刪掉的那天這一輪仍然粗體");

        // 不能只驗字串存在：把呼叫註解掉（// ReconcileDayFolderExistence(...)）時上面兩個
        // IndexOf 仍會命中，契約就變成裝飾品。這裡要求它是一行真正的敘述句。
        var callLine = mark
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("ReconcileDayFolderExistence(", StringComparison.Ordinal));
        Assert.True(
            callLine is not null,
            "ApplyRecordingDayMarks 必須真的呼叫 ReconcileDayFolderExistence（不能只是註解或宣告）");

        var reconcile = SourceContract.BodyOf(source, "ReconcileDayFolderExistence");
        Assert.Contains("ListFinalFoldersInRange", reconcile);
        Assert.Contains("VerifyFolders", reconcile);
    }

    /// <summary>
    /// 從即時畫面（含地圖圖釘開的小視窗）按「回放」，必須帶入該台並直接起播。
    ///
    /// <para>
    /// 實際症狀：按了回放只跳出一台「頻道選好了、清單也有資料、播放器全黑」的視窗——
    /// 操作員得自己找到最後一段再點下去才有畫面，第一眼只會認為回放壞了。
    /// </para>
    /// <para>
    /// 兩件事都要守：(1) 建立視窗時就把頻道與「自動起播」帶進去（不是靠呼叫端另叫
    /// <c>FocusChannel</c>，那只是選台）；(2) 起播要接在 <c>OnLoaded</c> 之後，
    /// 那時 <c>_bandSegments</c> 才有資料。
    /// </para>
    /// </summary>
    [Fact]
    public void 從即時畫面開回放會帶入該頻道並直接起播()
    {
        var live = SourceContract.BodyOf(
            SourceContract.Read("src/HeliVMS.App/LiveWindow.xaml.cs"), "OpenPlayback");
        Assert.False(string.IsNullOrWhiteSpace(live), "找不到 LiveWindow.OpenPlayback 的實作");
        Assert.Contains(
            "new PlaybackWindow(_store, _channelId, null, autoPlayLatest: true, playbackFinalizer: _playbackFinalizer)",
            live);

        var map = SourceContract.BodyOf(
            SourceContract.Read("src/HeliVMS.App/MapWindow.xaml.cs"), "OpenPlayback");
        Assert.False(string.IsNullOrWhiteSpace(map), "找不到 MapWindow.OpenPlayback 的實作");
        Assert.Contains(
            "new PlaybackWindow(_store, channelId, null, autoPlayLatest: true, playbackFinalizer: _playbackFinalizer)",
            map);

        var source = SourceContract.Read(Code);

        // 沒有焦點時刻時才由「自動起播」接手；兩者都在 OnLoaded 內，順序不能顛倒
        // （事件中心指定時刻的跳轉優先於「播最後一段」）。
        var loaded = SourceContract.BodyOf(source, "OnLoaded");
        Assert.Contains("PlayLatestSegment()", loaded);
        Assert.True(
            loaded.IndexOf("_focusUtc", StringComparison.Ordinal)
                < loaded.IndexOf("PlayLatestSegment()", StringComparison.Ordinal),
            "指定時刻的跳轉必須先於自動起播，否則事件中心會跳到最後一段而不是事發當時");

        var play = SourceContract.BodyOf(source, "PlayLatestSegment");
        Assert.Contains("_bandSegments[^1]", play);
        Assert.Contains("PlayFrom(", play);

        // 沒錄影時要說清楚，不能留下一台全黑播放器（與「解碼失敗」在畫面上長得一樣）。
        Assert.Contains("Playback.NoSegmentsToday", play);
    }

    /// <summary>
    /// 選台必須用 <c>ItemsSource</c> 裡的那一個實例。
    ///
    /// <para>
    /// 實際症狀：從即時畫面按「回放」，視窗開得出來但頻道沒帶進去、播放器全黑。
    /// 原因不是沒傳頻道，而是 <c>LoadChannels</c> 呼叫 <c>_channels.List()</c> 兩次：
    /// <c>ItemsSource</c> 拿第一次的結果，選台卻用第二次查出來的物件。WPF 的
    /// <c>Selector.SelectedItem</c> 是參考比對，不在 Items 裡的物件會被直接丟掉
    /// （SelectedItem 變 null、SelectedIndex 變 -1），<c>LoadSegments</c> 因此判定
    /// 「沒有頻道」，連清單都不載。
    /// </para>
    /// <para>
    /// 用實測確認過：兩個內容相同但不同實例的物件，設進 SelectedItem 之後是 null。
    /// 所以這裡釘住的不是風格，是「只能查一次、選的就是放進去的那一份」。
    /// 同樣的錯誤會讓事件中心跳時刻、拖曳即播、主畫面右鍵回放全部靜靜失效。
    /// </para>
    /// </summary>
    [Fact]
    public void 回放選台用的是ItemsSource裡同一份實例()
    {
        var load = SourceContract.BodyOf(SourceContract.Read(Code), "LoadChannels");
        Assert.False(string.IsNullOrWhiteSpace(load), "找不到 LoadChannels 的實作");

        // 只准查一次：第二次 List() 產生的物件不在 Items 裡，選了等於沒選。
        Assert.Single(Regex.Matches(load, @"_channels\.List\(\)"));

        Assert.Contains("ChannelCombo.ItemsSource = channels", load);
        Assert.Contains("ChannelCombo.SelectedItem = match", load);

        // match 必須來自 channels 這一份，而不是又一次資料庫查詢。
        Assert.Contains("channels.FirstOrDefault(c => c.Id == focus)", load);
    }
}

