namespace HeliVMS.App.Tests;

/// <summary>
/// 離線／上線事件的接線契約。
/// </summary>
/// <remarks>
/// 現場回報「攝影機斷線超過 30 秒才在事件裡顯示」。量測後發現是三件事疊在一起：
/// ①偵測窗 10.9 秒（<c>StaleSeconds</c>，設計值）；②健康檢查在重啟成功後<b>立刻</b>補
/// <c>MarkOnline</c>（<c>StartAsync</c> 只是把拉流迴圈丟出去，回來時一幀都還沒到），
/// 於是每一段離線都被壓成 0 秒；③這兩個事件只寫進資料庫，沒有經過
/// <c>AlarmEvent</c>／<c>Alerts.Publish</c>，所以事件中心要等下一個人手動刷新、
/// 警報管理器要等 15 秒輪詢。三件事都無聲無息，日誌乾淨。
/// </remarks>
public sealed class OfflineEventPlaneContractTests
{
    private const string ManagerCode = "src/HeliVMS.App/Services/ChannelManager.cs";
    private const string MainWindowCode = "src/HeliVMS.App/MainWindow.xaml.cs";

    /// <summary>
    /// 偵測事件與連線事件共用同一個發布點。分成兩處各呼叫一次，遲早有一邊忘了推播——
    /// 症狀就是「這台斷線了，但事件中心/牆上事件面板沒有」。
    /// </summary>
    [Fact]
    public void 連線事件與偵測事件共用同一個發布點()
    {
        var source = SourceContract.Read(ManagerCode);

        var publish = SourceContract.BodyOf(source, "PublishEvent");
        Assert.NotEqual(string.Empty, publish);
        Assert.Contains("AlarmEvent?.Invoke", publish);
        Assert.Contains("Alerts.Publish(new AlertUpdate(\"alarm.created\"", publish);
        // null ＝ 這一次並沒有真的寫入資料列（同一個離線期的重複 Reconnecting），不得推播。
        Assert.Contains("if (record is null)", publish);

        var create = SourceContract.BodyOf(source, "CreateSession");
        Assert.Contains("PublishEvent(session.Cell, _offline.MarkOffline(ch.Id))", create);
        Assert.Contains("PublishEvent(session.Cell, _offline.MarkOnline(ch.Id))", create);
        Assert.Contains("session.EventInserted += (_, r) => PublishEvent(session.Cell, r);", create);

        // 整個檔案只能有一個 Alerts.Publish：多了就是「另一個發布點」。
        Assert.Equal(1, CountOf(source, "Alerts.Publish"));
    }

    /// <summary>
    /// 健康檢查不得在重啟之後補上線：那時 <c>StartAsync</c> 才剛把迴圈丟出去，
    /// 一幀都還沒到。上線必須由 <c>StateChanged(Streaming)</c> 收斂，離線窗才有真實長度；
    /// 攝影機根本連不上時，窗就該一直開著。
    /// </summary>
    [Fact]
    public void 健康檢查重啟後不會立刻補上線()
    {
        var tick = SourceContract.BodyOf(SourceContract.Read(ManagerCode), "OnHealthTick");
        Assert.NotEqual(string.Empty, tick);
        Assert.Contains("PublishEvent(cell, _offline.MarkOffline(kv.Key))", tick);
        // 釘「呼叫形狀」而不是裸字串：上面那段說明文字本身就寫著 MarkOnline，
        // 用裸字串會讓「整個 if 區塊被刪掉」也照樣綠燈（AGENTS.md 的註解誤匹配教訓）。
        Assert.DoesNotContain("_offline.MarkOnline(", tick);
    }

    /// <summary>
    /// 夜間排程「重連攝影機」是保養不是故障：健康的頻道不該每天都在事件中心留下一筆
    /// 沒人遇過的斷線。只有重連真的失敗（else 分支）才開窗。
    /// </summary>
    [Fact]
    public void 排程重連只在失敗時才開離線窗()
    {
        var all = SourceContract.BodyOf(SourceContract.Read(ManagerCode), "RestartAllAsync");
        Assert.NotEqual(string.Empty, all);

        var successAt = all.IndexOf("if (await RestartAsync(session))", StringComparison.Ordinal);
        var elseAt = all.IndexOf("else", successAt, StringComparison.Ordinal);
        var markAt = all.IndexOf("MarkOffline", StringComparison.Ordinal);

        Assert.True(successAt >= 0, "重連結果必須是被檢查的");
        Assert.True(elseAt > successAt, "MarkOffline 必須在重連失敗的分支裡");
        Assert.True(markAt > elseAt, "MarkOffline 不得排在成功路徑（會對健康頻道捏造斷線）");
        Assert.DoesNotContain("_offline.MarkOnline(", all);
    }

    /// <summary>
    /// 連線狀態事件在 <c>RouteAlarm</c> 走早退分支：照樣進牆上事件面板與 CMS 回報，
    /// 但<b>不進事件錄影閘</b>（對離線開錄只會產生 corrupt 段）、<b>不進智慧聚合</b>、
    /// 也<b>沒有預設通知</b>（一台不穩的攝影機一個晚上可以重連幾十次，等於通知洗版）。
    /// </summary>
    [Fact]
    public void 連線事件不開事件錄影也不預設通知()
    {
        var route = SourceContract.BodyOf(SourceContract.Read(MainWindowCode), "RouteAlarm");
        Assert.NotEqual(string.Empty, route);

        var guardAt = route.IndexOf("EventTaxonomy.IsConnectivity", StringComparison.Ordinal);
        Assert.True(guardAt >= 0, "連線狀態必須由 EventTaxonomy 判定，不能各處自己比字串");

        var branchEnd = route.IndexOf("return;", guardAt, StringComparison.Ordinal);
        Assert.True(branchEnd > guardAt, "連線狀態分支必須早退，否則下面的事件錄影閘照樣會執行");

        var connectivity = route[guardAt..branchEnd];
        Assert.Contains("AppendEventPanel(record)", connectivity);
        Assert.Contains("_qctekHost?.ReportEvent", connectivity);
        Assert.DoesNotContain("_scheduler?.RegisterEvent", connectivity);
        Assert.DoesNotContain("_smartAlerts", connectivity);
        // 通知只走複合事件規則；沒有規則就一則都不外送。
        Assert.Contains("_eventRules is not null", connectivity);
        Assert.Contains("NotifyAlarm(enriched)", connectivity);

        // 事件錄影閘在分支之後，偵測事件才走得到。
        var gateAt = route.IndexOf("_scheduler?.RegisterEvent", StringComparison.Ordinal);
        Assert.True(gateAt > branchEnd, "偵測事件的事件錄影閘必須排在連線狀態分支之後");
    }

    /// <summary>
    /// 牆上事件面板顯示的是在地化名稱與明細。連線事件現在會即時出現在這個面板上，
    /// 若照舊直接印 <c>record.EventType</c>，中文介面會出現「攝影機 · offline · connection lost」。
    /// </summary>
    [Fact]
    public void 牆上事件面板顯示在地化的事件名稱與明細()
    {
        var row = SourceContract.BodyOf(SourceContract.Read(MainWindowCode), "ToEventRow");
        Assert.NotEqual(string.Empty, row);
        Assert.Contains("EventTypeText.Label(record.EventType)", row);
        Assert.Contains("EventDetailText.Format(Localizer.Lang, record.Detail)", row);
    }

    private static int CountOf(string source, string needle)
    {
        var count = 0;
        for (var i = source.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
