namespace HeliVMS.App.Tests;

/// <summary>
/// 監看牆的即時畫面必須看得出每個頻道的<b>錄影方式</b>（§15.1）。
///
/// <para>
/// 症狀：錄影方式只存在設定中心→頻道管理裡，而監看牆上一次有 16 台在跑。於是
/// 「排程外」（正常）與「勾了全時卻沒錄」（故障）在畫面上長得一模一樣，操作員只能
/// 一台一台點進設定頁查；現場回報的問句就是「我怎麼知道這台是怎麼錄的」。
/// </para>
/// <para>
/// 組字本身是純函式（<c>WallBadgeTextPlanner</c>，行為測試在 HeliVMS.Shared.Tests）；
/// 這裡釘的是桌面端的接線：徽章要拿到頻道的錄影方式、要經過那個純函式、
/// 而且「僅事件錄影／僅手動錄影」這種只是把錄影方式再說一次的原因不能重複顯示。
/// </para>
/// </summary>
public sealed class MonitorWallRecordingModeContractTests
{
    private const string Code = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string I18n = "src/HeliVMS.Storage/I18n.cs";

    [Fact]
    public void 徽章會用頻道的錄影方式組字並附工具提示()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "UpdateBadges");

        Assert.Contains("ChannelMode(", body);
        Assert.Contains("ModeLabel(", body);
        // 排版規則在共用的純函式裡，桌面端不得自己再接字串（否則兩邊遲早不一致）。
        Assert.Contains("WallBadgeTextPlanner.Compose(", body);
        Assert.Contains("badge.ToolTip", body);
        Assert.Contains("Main.ModeTip", body);
    }

    [Fact]
    public void 四種錄影方式都有標籤且未知值留白()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "ModeLabel");

        foreach (var mode in new[] { "Always", "Scheduled", "Event", "Manual" })
        {
            Assert.Contains($"RecordingMode.{mode} =>", body);
        }

        // 未知值／查不到頻道時留白。別忘了新增模式時 CS8524 會先擋下編譯（保留捨棄臂）。
        Assert.Contains("_ => null", body);
    }

    [Fact]
    public void 每格右上角都有一顆錄影方式指示燈()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "RebuildCells");

        Assert.Contains("_cellModeDots[i] = modeDot", body);
        Assert.Contains("inner.Children.Add(modeDot)", body);

        // 位置就是需求：攝影機的右上角。這裡只掃「指示燈自己的初始化區塊」——
        // 同一段 RebuildCells 裡的喇叭／麥克風面板也用 Right，整段掃描會讓這條斷言
        // 在燈真的跑到別的位置時照樣通過（改壞驗證抓到的正是這件事）。
        var stroke = body.IndexOf("Stroke = BrBadgeBg", StringComparison.Ordinal);
        Assert.True(stroke > 0, "找不到指示燈的描邊設定");
        var start = body.LastIndexOf("var modeDot", stroke, StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到指示燈的建立敘述");
        var own = body[start..stroke];

        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Right", own);
        Assert.Contains("VerticalAlignment = VerticalAlignment.Top", own);
    }

    [Fact]
    public void 指示燈的顏色來自共用色票且沒錄影時壓暗()
    {
        var source = SourceContract.Read(Code);
        var body = SourceContract.BodyOf(source, "UpdateBadges");

        Assert.Contains("ModeBrush(mode)", body);
        Assert.Contains("WallModeDotPalette.OpacityFor(recording)", body);
        Assert.Contains("Main.ModeDotOn", body);
        Assert.Contains("Main.ModeDotOff", body);

        // 顏色只是加速辨識：掛不到錄影方式時把燈收起來，不留一顆猜的顏色。
        Assert.Contains("dot.Visibility = brush is null ? Visibility.Collapsed : Visibility.Visible", body);

        // 色票必須取自共用常數。桌面端自己寫一組 hex 就等於有兩份圖例，遲早不一致。
        var brushBody = SourceContract.BodyOf(source, "ModeBrush");
        foreach (var name in new[] { "Always", "Scheduled", "Event", "Manual" })
        {
            Assert.Contains($"WallModeDotPalette.{name}", brushBody);
        }

        Assert.Contains("_ => null", brushBody);
    }

    [Fact]
    public void 全域錄影徽章不會蓋住右上角的指示燈()
    {
        var source = SourceContract.Read(Xaml);
        var at = source.IndexOf("x:Name=\"RecBadge\"", StringComparison.Ordinal);
        Assert.True(at > 0, "監看牆找不到全域 REC 徽章");

        var element = source[at..source.IndexOf('>', at)];

        // 兩者都貼右上角時，最右上那一格的錄影方式會整顆被 REC 徽章蓋掉。
        Assert.Contains("HorizontalAlignment=\"Center\"", element);
        Assert.DoesNotContain("HorizontalAlignment=\"Right\"", element);
    }

    [Fact]
    public void 查不到頻道時不會猜錄影方式()
    {
        // 猜一個「全時」會讓操作員對著一台根本不是全時錄影的攝影機判斷故障。
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "ChannelMode");

        // 查得到就回該頻道列的值，查不到回 null（不回預設模式）。
        Assert.Contains("? channel.RecordingMode : null", body);
    }

    [Fact]
    public void 事件與手動的沒錄原因不會和錄影方式說兩次()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "BlockLabel");

        // 徽章已經寫了「事件」「手動」，再補一句「僅事件錄影」只是同一句話說兩次。
        Assert.Contains("RecordingBlockReason.ManualOnly when mode == RecordingMode.Manual", body);
        Assert.Contains("RecordingBlockReason.EventOnly when mode == RecordingMode.Event", body);

        // 頻道停用與未設位址是真正的故障原因，任何模式都要留著。
        Assert.Contains("RecordingBlockReason.ChannelDisabled", body);
        Assert.Contains("RecordingBlockReason.NoStreamUrl", body);
        // 排程外是正常狀態，但正是操作員要分辨的那一種，必須保留。
        Assert.Contains("RecordingBlockReason.OutsideSchedule", body);
    }

    [Fact]
    public void 錄影方式標籤三語都在且工具提示帶得到標籤()
    {
        var i18n = SourceContract.Read(I18n);

        foreach (var key in new[]
                 {
                     "Main.ModeAlways", "Main.ModeScheduled", "Main.ModeEvent", "Main.ModeManual",
                 })
        {
            var lines = i18n.Split('\n')
                .Where(line => line.Contains($"\"{key}\"", StringComparison.Ordinal))
                .ToList();

            // 三語各一行；少一語，該語系的徽章上就只剩頻道名。
            Assert.Equal(3, lines.Count);
            Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(ValueOf(line)), key));
        }

        // 三個帶 {0} 的工具提示：徽章、燈亮、燈暗。顏色之外的第二個管道（色覺者靠文字讀同一件事）。
        foreach (var key in new[] { "Main.ModeTip", "Main.ModeDotOn", "Main.ModeDotOff" })
        {
            var tips = i18n.Split('\n')
                .Where(line => line.Contains($"\"{key}\"", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(3, tips.Count);
            // 少了 {0} 的工具提示等於一句沒有內容的廢話。
            Assert.All(tips, line => Assert.Contains("{0}", line));
        }
    }

    /// <summary>取出 i18n 行的字串值（<c>["Key"] = "值",</c> → <c>值</c>）。</summary>
    private static string ValueOf(string line)
    {
        var start = line.IndexOf("= \"", StringComparison.Ordinal);
        var end = line.LastIndexOf('"');
        return start < 0 || end <= start + 3 ? string.Empty : line[(start + 3)..end];
    }
}
