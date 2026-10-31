using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 回放換頻道／換日／續播的接線契約（M84／ARCHITECTURE §8.5）。
///
/// <para>
/// 桌面對 WPF 沒有可用的執行期測試環境，這一組守的是這一輪修掉、而且光看畫面
/// 不一定看得出來的競態與狀態殘留：
/// </para>
/// <para>
/// （1）換台是非同步的（先收檔、再讀索引），但舊程式同步換台後立刻開播，兩秒後
/// 那次遲到的 <c>LoadSegments</c> 會把剛開的播放停掉——拖進回放牆的頻道播一下就黑、
/// 點次畫面放大後停住，日誌一片乾淨。
/// </para>
/// <para>
/// （2）<c>StopPlayback</c> 刻意保留播放頭是為了「暫停後原處續播」，但換台／換日時
/// 留著它，畫面是 B 台的清單、按播放卻播出 A 台的檔案。
/// </para>
/// <para>
/// （3）暫停後按「播放」完全沒反應（舊寫法開頭就 <c>_session is null</c> 早退），
/// 而播到尾再按一次只會立刻又結束一次，看起來像按鈕壞了。
/// </para>
/// <para>
/// （4）載入失敗只寫一行提示、清單與時間軸仍留著上一輪的段：操作員點下去播放器
/// 一片黑，還以為解碼壞了。
/// </para>
/// </summary>
public sealed class PlaybackReloadContractTests
{
    private const string Code = "src/HeliVMS.App/PlaybackWindow.xaml.cs";

    /// <summary>
    /// 換台必須先切頻道、再等當日索引載入完成，最後才讓呼叫端開播或續播。
    /// 順序顛倒就是「播兩秒後自己停掉」，而且沒有任何錯誤訊息可以追。
    /// </summary>
    [Fact]
    public void 換台會等新頻道的索引載入完成才開播()
    {
        var source = SourceContract.Read(Code);

        var switchChannel = SourceContract.BodyOf(source, "SwitchChannelAsync");
        var loadChannels = switchChannel.IndexOf("LoadChannels()", StringComparison.Ordinal);
        var reload = switchChannel.IndexOf("await ReloadForPlaybackAsync()", StringComparison.Ordinal);
        Assert.True(loadChannels >= 0, "SwitchChannelAsync 必須先套用新的焦點頻道。");
        Assert.True(
            reload > loadChannels,
            "SwitchChannelAsync 必須在換台後 await ReloadForPlaybackAsync()，回傳的才是載入完成的結果。");

        // 拖入回放與點次畫面放大都走同一條漏斗；兩者都必須等到載入完成才續播。
        foreach (var caller in new[] { "FocusChannelAndPlayAsync", "FocusPaneAsync" })
        {
            var body = SourceContract.BodyOf(source, caller);
            var wait = body.IndexOf("await SwitchChannelAsync(", StringComparison.Ordinal);
            var resume = body.IndexOf("ResumePlaybackAt(", StringComparison.Ordinal);
            var play = body.IndexOf("PlayFrom(", StringComparison.Ordinal);
            Assert.True(wait >= 0, $"{caller} 必須走 SwitchChannelAsync 才會載入新頻道的索引。");
            Assert.True(
                resume > wait || play > wait || (resume < 0 && play < 0),
                $"{caller} 的續播／開播必須排在載入完成之後。");
        }
    }

    /// <summary>
    /// 程式自己改選取（換台、時間跳轉換日）時要抑制 SelectionChanged 再排一次載入：
    /// 否則呼叫端 await 完載入後開播，事件處理常式又排一次收檔＋載入把它停掉。
    /// </summary>
    [Fact]
    public void 程式自己改選取時不會再排一次載入()
    {
        var source = SourceContract.Read(Code);

        Assert.Contains(
            "_suppressSelectionReload",
            SourceContract.BodyOf(source, "SwitchChannelAsync"));
        Assert.Contains(
            "_suppressSelectionReload",
            SourceContract.BodyOf(source, "JumpToLocalTime"));

        // 兩個事件處理常式都要看同一個旗標；只改一邊的話，換台或換日仍會多載入一次。
        Assert.Contains(
            "_suppressSelectionReload",
            SourceContract.BodyOf(source, "OnChannelSelectionChanged"));
        Assert.Contains(
            "_suppressSelectionReload",
            SourceContract.BodyOf(source, "OnDateChanged"));
    }

    /// <summary>
    /// 連續換台／換日時，先出發但較晚抵達的載入不得覆蓋最後一次選取。
    /// 世代編號是這裡唯一的防線，少了它清單會變成前一隻頻道／前一天的資料。
    /// </summary>
    [Fact]
    public void 較舊的載入不會覆蓋較新的選取()
    {
        var source = SourceContract.Read(Code);

        var reload = SourceContract.BodyOf(source, "ReloadForPlaybackAsync");
        var bump = reload.IndexOf("Interlocked.Increment(ref _reloadGeneration)", StringComparison.Ordinal);
        var check = reload.IndexOf("Volatile.Read(ref _reloadGeneration)", StringComparison.Ordinal);
        var load = reload.IndexOf("LoadSegments()", StringComparison.Ordinal);
        Assert.True(bump >= 0, "每次重新載入都要取得新的世代編號。");
        Assert.True(
            check > bump && load > check,
            "收檔回來後必須先確認自己仍是最後一代，才寫回清單。");

        // 被取代的那一次要回報 false，呼叫端才不會拿舊資料開播。
        Assert.Contains("return false;", reload);
    }

    /// <summary>
    /// 換台／換日／載入時必須丟掉上一台、上一天的播放頭：StopPlayback 留著 _current
    /// 是為了暫停續播，跨台時留著它就會「清單是 B 台、播放的是 A 台」。
    /// </summary>
    [Fact]
    public void 換台或換日會丟掉上一輪的播放頭()
    {
        var source = SourceContract.Read(Code);

        var load = SourceContract.BodyOf(source, "LoadSegments");
        var clear = load.IndexOf("ClearPlaybackHead()", StringComparison.Ordinal);
        var stop = load.IndexOf("StopPlayback()", StringComparison.Ordinal);
        Assert.True(clear >= 0, "LoadSegments 開頭必須先丟掉上一輪的播放頭。");
        Assert.True(stop > clear, "必須先清播放頭再停播放，否則 StopPlayback 留下的 _current 會活下來。");

        var head = SourceContract.BodyOf(source, "ClearPlaybackHead");
        Assert.Contains("_current = null", head);
        Assert.Contains("_posInside = 0", head);
        Assert.Contains("SeekSlider.Value = 0", head);

        // 上一台的最後一格必須一起清掉，否則快照會把 A 台的畫面存成 B 台的證據。
        Assert.Contains("_bitmap = null", head);
        Assert.Contains("PlayerImage.Source = null", head);
    }

    /// <summary>
    /// 暫停後按「播放」要從原位置續播；播到段尾再按則從頭。
    /// 舊寫法以 <c>_session is null</c> 早退，而暫停正是把 _session 設成 null——按鈕看得到、
    /// 按下去完全沒反應；播到尾時續播又會立刻再結束一次，同樣像壞掉。
    /// </summary>
    [Fact]
    public void 暫停後可續播且播到尾再按會從頭()
    {
        var source = SourceContract.Read(Code);

        var toggle = SourceContract.BodyOf(source, "OnPlayPauseClicked");

        // 狀態必須看播放階段，不看按鈕上的字（換語言或別處改字就跟著錯）。
        Assert.DoesNotContain("PlayPauseButton.Content.ToString()", toggle);
        Assert.DoesNotContain("_session is null || _stopping", toggle);

        // 沒有播放頭才真的什麼都不能做；暫停後 _current 仍留著，所以續播得走得到。
        Assert.Contains("if (_current is null)", toggle);
        Assert.Contains("PlayFrom(_current, resumeAt)", toggle);
        Assert.Contains("_playbackEnded", toggle);
        Assert.Contains("duration > 0 && _posInside >= duration - 0.5", toggle);

        // 「播完了」不能只靠段長：索引沒回填長度（DurationSec 為 null）時位置永遠比不到
        // 終點，按播放會從檔尾續播、立刻又結束。旗標必須在真的播完時設、開播時清。
        var ended = SourceContract.BodyOf(source, "OnEnded");
        Assert.Contains("_playbackEnded = completed", ended);
        Assert.Contains("_playbackEnded = false", SourceContract.BodyOf(source, "PlayFrom"));
        Assert.Contains("_playbackEnded = false", SourceContract.BodyOf(source, "ClearPlaybackHead"));
    }

    /// <summary>
    /// 載入失敗、沒頻道、日期被清空都要走同一個清空路徑：留著上一輪的清單等於
    /// 給操作員一份「點得到但不屬於這天」的片段，點下去就是全黑。
    /// </summary>
    [Fact]
    public void 載入失敗會清空清單與時間軸()
    {
        var source = SourceContract.Read(Code);

        var load = SourceContract.BodyOf(source, "LoadSegments");
        Assert.True(
            Regex.Matches(load, @"ShowEmptyState\(").Count >= 3,
            "沒頻道、日期被清空、載入失敗三條路都要清空畫面，只寫提示會留下上一輪的段。");
        Assert.Contains("ShowEmptyState(string.Format(Localizer.T(\"Playback.LoadFailed\"), ex.Message))", load);

        var empty = SourceContract.BodyOf(source, "ShowEmptyState");
        Assert.Contains("ClearPlaybackHead()", empty);
        Assert.Contains("StopPlayback()", empty);
        Assert.Contains("SegmentList.ItemsSource = null", empty);
        Assert.Contains("_bandSegments = []", empty);
        Assert.Contains("_events = []", empty);
        Assert.Contains("BookmarkList.ItemsSource = null", empty);
        Assert.Contains("_timeline = null", empty);
        Assert.Contains("_dayStartUtc = default", empty);
        Assert.Contains("HideCursor()", empty);
    }

    /// <summary>
    /// 時間軸的「當日零時」以 DatePicker 選的那一天為準，不可由第一段推算：
    /// 跨午夜的那一段（例 23:55–00:05）會同時屬於前後兩天，用它反推會整整差一天，
    /// 時間軸建構於是把當天其餘的段全部裁掉，帶子上只剩一小段。
    /// </summary>
    [Fact]
    public void 時間軸當日零時以選定日期為準()
    {
        var source = SourceContract.Read(Code);

        var dayStart = SourceContract.BodyOf(source, "DayStartUtc");
        Assert.Contains("_dayStartUtc != default", dayStart);
        Assert.Contains("DatePick.SelectedDate", dayStart);
        Assert.DoesNotContain("_bandSegments[0]", dayStart);
        Assert.DoesNotContain("_events[0]", dayStart);
    }

    /// <summary>
    /// 逐幀在段尾會立刻結束、根本沒有影格觸發「首幀後暫停」，那個旗標若留著，
    /// 下一次正常播放就會播一格就停住（操作員只看到「按播放沒反應」）。
    /// </summary>
    [Fact]
    public void 逐幀留下的暫停旗標不會影響下一次播放()
    {
        var source = SourceContract.Read(Code);
        Assert.Contains("_pendingFramePause = false", SourceContract.BodyOf(source, "PlayFrom"));

        // 逐幀本身仍必須在起播後要求「首幀後暫停」，否則按 F 會變成直接播下去。
        var step = SourceContract.BodyOf(source, "StepFrame");
        var play = step.IndexOf("PlayFrom(", StringComparison.Ordinal);
        var pause = step.IndexOf("_pendingFramePause = true", StringComparison.Ordinal);
        Assert.True(
            play >= 0 && pause > play,
            "StepFrame 必須在 PlayFrom 之後設 _pendingFramePause = true，否則逐幀不會停。");
    }

    /// <summary>
    /// 播完的播放工作不會再被 StopPlayback 收掉（它只處理「正在播」的那一個）。
    /// 不在 Ended 當下釋放，每段就漏一個 CTS／行程控制代碼；連續回放整天會慢慢累積。
    /// </summary>
    [Fact]
    public void 播完的播放工作會被釋放()
    {
        var source = SourceContract.Read(Code);
        Assert.Contains("DisposeQuietly(finished)", SourceContract.BodyOf(source, "OnEnded"));
        Assert.Contains("DisposeQuietly(finished)", SourceContract.BodyOf(source, "OnPaneEnded"));
    }

    /// <summary>
    /// 段結束時刻只能有一個換算口徑：索引沒寫長度時以 10 秒估算。散落各處的
    /// <c>?? 0</c> 會讓那一條路徑永遠對不到時刻——點下去回報「這裡沒有錄影」。
    /// </summary>
    [Fact]
    public void 段結束時刻一律使用同一個換算口徑()
    {
        var source = SourceContract.Read(Code);

        // 換算本體只有一份（HeliVMS.Shared.PlaybackSegmentTiming），畫面這邊只轉呼叫：
        // 多寫一份就等於多一種口徑，時間軸、事件比對與多路同步會各自算出不同的段尾。
        Assert.Contains(
            "HeliVMS.Shared.PlaybackSegmentTiming.EndOf(seg)",
            SourceContract.BodyOf(source, "EndOf"));
        Assert.Contains(
            "HeliVMS.Shared.PlaybackSegmentTiming.LengthOf(seg)",
            SourceContract.BodyOf(source, "LengthOf"));

        // 除了上面兩個轉呼叫，不允許任何地方再手寫一份（?? 0 或 ?? 10 都算漂移）。
        var raw = Regex.Matches(
            source,
            @"\.EndUtc \?\? [^\r\n;]*AddSeconds\([^\r\n;]*DurationSec");
        Assert.True(
            raw.Count == 0,
            $"段結束時刻只可在 PlaybackSegmentTiming 內換算，實際找到 {raw.Count} 處；"
            + "多一份就有一條路徑會用錯的長度（通常是 0 秒）比對時刻。");

        // 「起點 + 長度 ?? 0」是同一種漂移的另一個寫法：段有 EndUtc 但沒填長度時，
        // 比對範圍會塌成 0 秒，事件／書籤點了就回報「這裡沒有錄影」。
        var collapsed = Regex.Matches(
            source,
            @"StartUtc\.AddSeconds\([^\r\n;]*DurationSec \?\? (?:0|1)\)");
        Assert.True(
            collapsed.Count == 0,
            $"以段長估算結束時刻必須走 EndOf／LengthOf，不可用 ?? 0 或 ?? 1（找到 {collapsed.Count} 處）。");
    }

    /// <summary>
    /// 事件與書籤的「這個時刻屬於哪一段」比對必須走 <c>EndOf</c>；方向鍵跳轉走
    /// <c>PlaybackSeekPlanner</c>，兩者都吃同一份段長換算。索引沒填段長時舊寫法用
    /// <c>DurationSec ?? 0</c>，比對範圍塌成一個瞬間，點下去只得到「這裡沒有錄影」，
    /// 而畫面上明明有那一段的色塊。
    /// </summary>
    [Fact]
    public void 事件與書籤跳轉使用同一段結束口徑()
    {
        var source = SourceContract.Read(Code);

        foreach (var method in new[] { "OnEventSelected", "OnBookmarkSelected" })
        {
            var body = SourceContract.BodyOf(source, method);
            Assert.Contains("EndOf(", body);
            Assert.DoesNotContain("DurationSec ?? 0", body);
            Assert.DoesNotContain("DurationSec ?? 1", body);
        }

        // 方向鍵跳轉的落點（含空隙往哪一邊落）抽成純函式才有測試可釘；
        // 留在鍵盤處理常式裡就只能靠人眼，而它錯的時候畫面看起來完全正常。
        var seek = SourceContract.BodyOf(source, "SeekBy");
        Assert.Contains("PlaybackSeekPlanner.Resolve(", seek);
        Assert.Contains("forward: seconds >= 0", seek);
        Assert.DoesNotContain("DurationSec ?? 0", seek);
        Assert.DoesNotContain("DurationSec ?? 1", seek);
        Assert.DoesNotContain("_bandSegments[^1]", seek);

        // 逐幀要與段長同一口徑：讀 DurationSec 會退回 1 秒或直接不做，
        // 未知長度的段按 F 沒反應。
        Assert.Contains("LengthOf(", SourceContract.BodyOf(source, "StepFrame"));

        // 事件落在空檔時要有回饋，不能靜默不動。
        Assert.Contains(
            "Playback.NoRecordingHere",
            SourceContract.BodyOf(source, "OnEventSelected"));
    }

    /// <summary>
    /// 回放窗是重用同一份：開著的時候操作員可以新增攝影機。下拉清單是開窗當下讀的，
    /// 換台若只比對 <c>ChannelCombo.Items</c>，新頻道會被判成「不存在」，
    /// 從即時畫面拖進來就完全沒反應——沒有錯誤、沒有日誌。
    /// </summary>
    [Fact]
    public void 開窗後才加入的頻道也能拖進回放()
    {
        var source = SourceContract.Read(Code);

        var switchChannel = SourceContract.BodyOf(source, "SwitchChannelAsync");
        Assert.Contains("ChannelExists(", switchChannel);
        Assert.DoesNotContain("ChannelCombo.Items.OfType<ChannelInfo>().All(", switchChannel);

        var exists = SourceContract.BodyOf(source, "ChannelExists");
        Assert.Contains("_channels.List()", exists);

        // 從事件中心／地圖指定時刻開回放，那一刻落在空檔時要說清楚，不可留下全黑播放器。
        var loaded = SourceContract.BodyOf(source, "OnLoaded");
        Assert.Contains("Playback.NoRecordingHere", loaded);
    }

    /// <summary>
    /// 把頻道拖進回放時，會從目前播放頭接上同一時刻；但拖進來的另一台在那個時刻
    /// 不一定有錄影。舊寫法找不到就直接返回，結果是一個全黑播放器——沒有錯誤、沒有日誌，
    /// 看起來像拖放沒有作用。這種情況要改播當日最後一段，並說明時刻為什麼變了。
    /// </summary>
    [Fact]
    public void 拖進回放的頻道在該時刻沒錄影時會改播最後一段()
    {
        var source = SourceContract.Read(Code);

        var drag = SourceContract.BodyOf(source, "FocusChannelAndPlayAsync");
        Assert.Contains("if (ResumePlaybackAt(target))", drag);
        Assert.Contains("PlayLatestSegment()", drag);
        Assert.Contains("Playback.ResumeFellBackToLatest", drag);

        // 續播是否成功必須回報：直接 void 的話呼叫端分不出「已開播」與「那一刻是空檔」。
        Assert.Contains("private bool ResumePlaybackAt(", source);
        var resume = SourceContract.BodyOf(source, "ResumePlaybackAt");
        Assert.Contains("return false;", resume);
        Assert.Contains("return true;", resume);
    }

    /// <summary>
    /// 點次畫面放大成單畫面時，那台在母鐘同一時刻常常正好是空檔（各台切點不同）；
    /// 換台已清掉播放頭，若不出聲就只剩全黑播放器與灰掉的播放鈕——和「解碼壞了」
    /// 在畫面上完全一樣，操作員無法判斷該換時刻還是換台。
    /// </summary>
    [Fact]
    public void 放大次畫面時該台此刻沒錄影會說清楚()
    {
        var source = SourceContract.Read(Code);
        var pane = SourceContract.BodyOf(source, "FocusPaneAsync");

        Assert.Contains("if (ResumePlaybackAt(target))", pane);
        Assert.Contains("Playback.NoRecordingHere", pane);

        // 訊息必須出現在續播失敗之後，否則等於在成功路徑上也喊「沒錄影」。
        var resume = pane.IndexOf("ResumePlaybackAt(target)", StringComparison.Ordinal);
        var message = pane.IndexOf("Playback.NoRecordingHere", StringComparison.Ordinal);
        Assert.True(message > resume, "「此時段無錄影」要接在續播失敗之後。");
    }
}
