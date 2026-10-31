using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 回放前「先把正在錄的那一段收檔」的接線契約。
///
/// <para>
/// 現場症狀：錄影一段一個檔，正在寫的那一段要等分段時間（預設 60 秒）到期才會
/// 出現在索引裡。操作員從即時畫面按「回放」想看的正是「剛剛發生的事」，而它剛好
/// 永遠是清單裡缺的那一段——畫面沒有任何線索說明少了什麼。
/// </para>
/// <para>
/// 桌面對 WPF 沒有可用的測試環境，所以這裡守的是接縫而不是畫面：
/// </para>
/// <para>
/// 一）載入時間軸之前一定要先收檔。順序反過來的話，收檔收到的段要等下一次
/// 重整才看得到，等於沒收。
/// </para>
/// <para>
/// 二）換頻道、換日期與按「載入」都要重新收檔，否則操作員只能靠關窗重開。
/// </para>
/// <para>
/// 三）只有看「今天」時才收檔：翻歷史日期時截斷正在錄的段沒有任何好處，
/// 而連按日期會讓錄影一直被迫換段（每一段都要重連 RTSP）。
/// </para>
/// <para>
/// 四）每一個開回放視窗的入口都要帶入收檔服務。少接一個入口就等於有一條路
/// 「看得到回放、但看不到最新的一段」，而它不會有任何錯誤。
/// </para>
/// </summary>
public sealed class PlaybackFinalizeContractTests
{
    private const string Playback = "src/HeliVMS.App/PlaybackWindow.xaml.cs";

    [Fact]
    public void 開回放會先收檔再載入時間軸()
    {
        var source = SourceContract.Read(Playback);

        var loaded = SourceContract.BodyOf(source, "OnLoaded");
        var finalizeAt = loaded.IndexOf("FinalizeRunningSegmentAsync(", StringComparison.Ordinal);
        var loadAt = loaded.IndexOf("LoadSegments(", StringComparison.Ordinal);
        Assert.True(finalizeAt >= 0, "回放視窗開窗時必須先收檔，否則清單裡永遠少最後一段。");
        Assert.True(
            loadAt >= 0 && finalizeAt < loadAt,
            "收檔必須在 LoadSegments 之前：順序反過來就等於沒收（要等下一次重整才看得到）。");

        var reload = SourceContract.BodyOf(source, "ReloadForPlaybackAsync");
        var reloadFinalize = reload.IndexOf("FinalizeRunningSegmentAsync(", StringComparison.Ordinal);
        var reloadLoad = reload.IndexOf("LoadSegments(", StringComparison.Ordinal);
        Assert.True(
            reloadFinalize >= 0 && reloadFinalize < reloadLoad,
            "換頻道／按載入的重新載入路徑同樣要先收檔再讀索引。");
    }

    [Fact]
    public void 換頻道換日期與按載入都會走同一個重新載入路徑()
    {
        var source = SourceContract.Read(Playback);

        foreach (var handler in new[] { "OnChannelSelectionChanged", "OnDateChanged", "OnLoadClicked" })
        {
            Assert.Contains(
                "ReloadForPlaybackAsync(",
                SourceContract.BodyOf(source, handler));
        }
    }

    [Fact]
    public void 只有看今天時才會收檔()
    {
        var source = SourceContract.Read(Playback);

        var finalize = SourceContract.BodyOf(source, "FinalizeRunningSegmentAsync");
        Assert.Contains("IsViewingToday()", finalize);

        var today = SourceContract.BodyOf(source, "IsViewingToday");
        Assert.Contains("SelectedDate", today);
        Assert.Contains("DateTime.Today", today);

        // 沒選日期也算今天：開窗時日期是空的，那條路徑若被當成「不是今天」，
        // 從即時畫面按回放就永遠不會收檔——正是要修的那個入口。
        Assert.True(
            today.IndexOf("is not DateTime day", StringComparison.Ordinal) >= 0,
            "IsViewingToday 必須把「未選日期」視為今天，否則主要入口不會收檔。");
    }

    [Fact]
    public void 收檔失敗不會讓回放視窗崩掉()
    {
        var source = SourceContract.Read(Playback);

        // 收檔是 async void 事件處理常式鏈上的一步：例外一漏出去就是整個程式結束。
        Assert.Contains("await FinalizeRunningSegmentAsync();", SourceContract.BodyOf(source, "ReloadForPlaybackAsync"));
        Assert.Contains("await FinalizeRunningSegmentAsync();", SourceContract.BodyOf(source, "OnLoaded"));

        // 上限必須存在：一台卡住的攝影機不能讓「按回放」變成等半分鐘。
        Assert.Contains("FinalizeTimeout", source);
        Assert.Contains("FinalizeTimeout", SourceContract.BodyOf(source, "FinalizeRunningSegmentAsync"));
    }

    [Fact]
    public void 每個開回放視窗的入口都帶入收檔服務()
    {
        var constructions = WindowConstructions("PlaybackWindow").ToList();

        // 忘記更新這份清單時不要讓測試默默通過（至少要有主視窗、即時畫面、地圖、
        // 事件中心與設定中心這幾個入口）。
        Assert.True(constructions.Count >= 7, $"只找到 {constructions.Count} 個回放視窗建立點，掃描規則可能失效了。");

        foreach (var (file, args) in constructions)
        {
            Assert.True(
                args.Contains("playbackFinalizer:", StringComparison.Ordinal),
                $"{file} 開了回放視窗卻沒帶入收檔服務：從這個入口看不到正在錄的最後一段。");
        }
    }

    [Fact]
    public void 開啟子視窗時會把收檔服務繼續傳下去()
    {
        foreach (var window in new[] { "EventCenterWindow", "MapWindow", "SettingsWindow" })
        {
            var constructions = WindowConstructions(window).ToList();
            Assert.NotEmpty(constructions);

            foreach (var (file, args) in constructions)
            {
                Assert.True(
                    args.Contains("playbackFinalizer:", StringComparison.Ordinal),
                    $"{file} 建立了 {window} 卻沒把收檔服務傳下去：從那個視窗開的回放不會收檔。");
            }
        }
    }

    /// <summary>
    /// 掃出所有 <c>new 視窗名(</c> 的引數（不跨語句），用來確認每個入口都有接線。
    /// </summary>
    private static IEnumerable<(string File, string Args)> WindowConstructions(string window)
    {
        var root = Path.Combine(SourceContract.RepoRoot(), "src", "HeliVMS.App");
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text, $@"new {window}\("))
            {
                var open = match.Index + match.Length - 1;
                var depth = 0;
                var i = open;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '(')
                    {
                        depth++;
                    }
                    else if (text[i] == ')' && --depth == 0)
                    {
                        break;
                    }
                }

                yield return (Path.GetFileName(path), text[(open + 1)..Math.Min(i, text.Length)]);
            }
        }
    }
}
