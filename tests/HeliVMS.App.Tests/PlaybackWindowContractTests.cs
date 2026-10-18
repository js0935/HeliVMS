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

        var focus = SourceContract.BodyOf(source, "FocusPane");
        Assert.Contains("SplitCombo.SelectedIndex = 0", focus);
        Assert.Contains("ChannelCombo.SelectedItem = candidate", focus);
        Assert.Contains("ResumePlaybackAt", focus);
        Assert.Contains("_current.StartUtc.AddSeconds(_posInside)", focus);

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

        var play = SourceContract.BodyOf(source, "FocusChannelAndPlay");
        Assert.Contains("PlayFrom", play);

        var pick = SourceContract.BodyOf(source, "TryGetDroppedChannelId");
        Assert.Contains("DragFormats.ChannelId", pick);

        var main = SourceContract.Read("src/HeliVMS.App/MainWindow.xaml.cs");
        var move = SourceContract.BodyOf(main, "OnCellMouseMove");
        Assert.Contains("ModifierKeys.Control", move);
        Assert.Contains("DragDrop.DoDragDrop", move);
        Assert.Contains("DragFormats.ChannelId", move);
    }
}
