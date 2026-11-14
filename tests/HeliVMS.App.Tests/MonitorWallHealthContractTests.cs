namespace HeliVMS.App.Tests;

/// <summary>
/// 監看牆的「連線健康」必須看得出來：哪一台斷了、還有幾台沒畫面。
///
/// <para>
/// 症狀：每格的連線狀態文字預設關閉（<c>monitor.show_status</c>），16 格以上的牆上
/// 「哪一台斷了」只能一台一台點開右鍵選單才看得到；頁尾只有全域的「N 台連線中」，
/// 少一台要自己算。於是離線的攝影機可以掛在牆上一整個班次沒人發現。
/// </para>
/// <para>
/// 兩個呈現都吃同一個權威狀態（<c>_cellStates</c>）：格底色條由 <c>ApplyCellState</c>
/// 投影（狀態文字的顏色口徑），頁尾徽章由 <c>UpdateBadges</c> 以秒節奏統計。
/// 這裡釘的是桌面端的接線，行為本身沒有獨立純函式可測（都是投影）。
/// </para>
/// </summary>
public sealed class MonitorWallHealthContractTests
{
    private const string Code = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/MainWindow.xaml";
    private const string I18n = "src/HeliVMS.Storage/I18n.cs";

    [Fact]
    public void 每格底邊都有狀態色條且貼在底緣()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "RebuildCells");

        Assert.Contains("inner.Children.Add(stateAccent)", body);
        Assert.Contains("_cellStateAccents[i] = stateAccent", body);

        // 位置就是需求：底邊整條。只掃「色條自己的初始化區塊」——同一段 RebuildCells
        // 裡的徽章也用 Bottom，整段掃描會在色條真的被移位時照樣通過。
        var at = body.IndexOf("var stateAccent", StringComparison.Ordinal);
        Assert.True(at > 0, "找不到狀態色條的建立敘述");
        var own = body[at..body.IndexOf(";", at, StringComparison.Ordinal)];

        Assert.Contains("Height = 3", own);
        Assert.Contains("VerticalAlignment = VerticalAlignment.Bottom", own);
        // 色條只是顯示層：吃掉滑鼠事件就會壞掉格子的選取／拖曳手勢。
        Assert.Contains("IsHitTestVisible = false", own);
    }

    [Fact]
    public void 狀態色條只在狀態落地時重畫()
    {
        // ApplyCellState 是狀態落地的唯一匯集點（OnCellState／OnCellMappingChanged／
        // 重建還原都經過它）。色條若在別處（每幀、每秒）也被寫，遲早與狀態文字各說各話。
        var apply = SourceContract.BodyOf(SourceContract.Read(Code), "ApplyCellState");
        Assert.Contains("ShowCellStateAccent(", apply);

        // 換頻道／每秒徽章這兩條高頻路徑不得直接摸色條。
        var mapping = SourceContract.BodyOf(SourceContract.Read(Code), "OnCellMappingChanged");
        Assert.DoesNotContain("_cellStateAccents", mapping);
        var badges = SourceContract.BodyOf(SourceContract.Read(Code), "UpdateBadges");
        Assert.DoesNotContain("_cellStateAccents", badges);

        // 狀態色條的顏色與狀態文字同一組筆刷（同一個事實不能有兩份色票）。
        Assert.Contains("ShowCellStateAccent(cell, BrLive,", apply);
        Assert.Contains("ShowCellStateAccent(cell, BrConnecting,", apply);
        Assert.Contains("ShowCellStateAccent(cell, BrOffline,", apply);
        Assert.Contains("ShowCellStateAccent(cell, BrStall,", apply);
    }

    [Fact]
    public void 沒有指派頻道的空格不上色()
    {
        // Disable 佔位圖已說明「還沒配頻道」；空格上色會讓它看起來像故障，
        // 操作員會去查一台根本不存在的攝影機。
        var apply = SourceContract.BodyOf(SourceContract.Read(Code), "ApplyCellState");
        Assert.Contains("ShowCellStateAccent(cell, null, null)", apply);
    }

    [Fact]
    public void 頁尾離線徽章只算配了頻道卻沒畫面的格數()
    {
        var source = SourceContract.Read(Code);

        var cells = SourceContract.BodyOf(source, "OfflineCells");
        Assert.Contains("_cellChannel[i] is not null", cells);
        Assert.Contains("_cellStates[i] != RtspState.Streaming", cells);

        // 統計掛在 UpdateBadges 的同一個秒節奏上，不另開計時器。
        Assert.Contains("UpdateOfflineBadge()", SourceContract.BodyOf(source, "UpdateBadges"));

        var update = SourceContract.BodyOf(source, "UpdateOfflineBadge");
        Assert.Contains("Main.OfflineBadge", update);
        // 零顆時整顆隱藏：常駐的「離線 0 格」會稀釋真正有離線時的注意力。
        Assert.Contains("Visibility.Collapsed", update);
    }

    [Fact]
    public void 點離線徽章會跳到選取格之後的下一個離線格()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnOfflineBadgeClicked");

        Assert.Contains("OfflineCells()", body);
        // 以目前選取格為基準往後循環：另記游標會在別處點選後出現「點了卻跳回同一格」的停滯。
        Assert.Contains("FirstOrDefault(c => c > _selectedCell", body);
        Assert.Contains("SelectCell(", body);
    }

    [Fact]
    public void 離線徽章在頁尾預設隱藏且點擊已接線()
    {
        var source = SourceContract.Read(Xaml);
        var at = source.IndexOf("x:Name=\"OfflineBadge\"", StringComparison.Ordinal);
        Assert.True(at > 0, "頁尾找不到離線徽章");

        var element = source[at..source.IndexOf('>', at)];
        Assert.Contains("Visibility=\"Collapsed\"", element);
        Assert.Contains("MouseLeftButtonUp=\"OnOfflineBadgeClicked\"", element);
        // 與 REC 徽章同一課題：頁尾徽章是排進列尾的獨立元素，不會蓋住任何一格。
        Assert.Contains("Margin=\"0,0,8,0\"", element);
    }

    [Fact]
    public void 離線徽章文案三語都在且帶格數()
    {
        var i18n = SourceContract.Read(I18n);

        var lines = i18n.Split('\n')
            .Where(line => line.Contains("\"Main.OfflineBadge\"", StringComparison.Ordinal))
            .ToList();

        // 三語各一行；少一語，該語系的頁尾就只剩一顆空徽章。
        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.Contains("{0}", line));

        var tips = i18n.Split('\n')
            .Where(line => line.Contains("\"Main.OfflineBadgeTip\"", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, tips.Count);
        Assert.All(tips, line => Assert.False(string.IsNullOrWhiteSpace(ValueOf(line)), "Main.OfflineBadgeTip"));
    }

    /// <summary>取出 i18n 行的字串值（<c>["Key"] = "值",</c> → <c>值</c>）。</summary>
    private static string ValueOf(string line)
    {
        var start = line.IndexOf("= \"", StringComparison.Ordinal);
        var end = line.LastIndexOf('"');
        return start < 0 || end <= start + 3 ? string.Empty : line[(start + 3)..end];
    }
}
