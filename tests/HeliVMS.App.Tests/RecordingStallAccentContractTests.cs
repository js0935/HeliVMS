namespace HeliVMS.App.Tests;

/// <summary>
/// 監看牆的紅色色條（錄影停滯）必須「出現得有原因、消失得掉」。
/// </summary>
/// <remarks>
/// <para>
/// 現場回報的症狀是「畫面正常顯示，但格位下方的線是紅色的」——而且一直紅著。
/// 兩個獨立的缺口疊在一起：
/// </para>
/// <para>
/// ① <b>紅線永遠不會消失</b>。<c>MainWindow._recordingStalls</c> 只由
/// <c>RecordingScheduler.RecordingStalled</c> 寫入，而 <c>RecordingResumed</c> 原本只在
/// 「整段成功錄完」時才發；被收掉的錄影機（停止錄影、品質協調換串流、關閉程式）永遠不會再
/// 成功錄出任何東西，也就永遠不會發解除訊號。牆上手動錄影更完全沒接線——它的錄影機是
/// <c>ChannelSession</c> 自己持有的。
/// </para>
/// <para>
/// ② <b>紅線在 1 秒內就失去原因</b>。每秒執行的 <c>UpdateCellMetrics</c> 直接呼叫
/// <c>RenderCellStatusText</c>，把「錄影失敗 N 次」換成正常的 fps 文字，卻不動色條。
/// </para>
/// <para>
/// 這裡釘的是桌面端與工作階段的接線（純顯示投影，沒有獨立純函式可測）。
/// </para>
/// </remarks>
public sealed class RecordingStallAccentContractTests
{
    private const string MainCode = "src/HeliVMS.App/MainWindow.xaml.cs";
    private const string SessionCode = "src/HeliVMS.App/Services/ChannelSession.cs";
    private const string ManagerCode = "src/HeliVMS.App/Services/ChannelManager.cs";
    private const string RecorderCode = "src/HeliVMS.Recording/SegmentRecorder.cs";

    [Fact]
    public void 錄影機被收掉時迴圈結束會解除停滯()
    {
        // 「錄影機不存在了」本身就是解除停滯的充分理由。少了這一段，紅線會活得比它所描述的
        // 那個故障還久：操作員停掉錄影或品質協調把錄影機換掉之後，畫面上仍是一片紅。
        var loop = SourceContract.BodyOf(SourceContract.Read(RecorderCode), "RecordLoopAsync");

        Assert.Contains("finally", loop);
        Assert.Contains("if (_stallReported)", loop);
        Assert.Contains("RecordingResumed?.Invoke(this, ChannelId)", loop);

        // 清除要寫回欄位，否則解一次之後又會被當成「還沒回報過」。
        Assert.Contains("_stallReported = false", loop);
    }

    [Fact]
    public void 牆上手動錄影的停滯事件會在開錄前訂閱()
    {
        // 訂閱晚一步就會漏掉第一次停滯（錄影機一開就可能失敗），所以順序是契約的一部分。
        var body = SourceContract.BodyOf(SourceContract.Read(SessionCode), "SetRecordingAsync");

        var stalledAt = body.IndexOf("recorder.RecordingStalled +=", StringComparison.Ordinal);
        var resumedAt = body.IndexOf("recorder.RecordingResumed +=", StringComparison.Ordinal);
        var startAt = body.IndexOf("await recorder.StartAsync(", StringComparison.Ordinal);

        Assert.True(stalledAt > 0, "工作階段沒有訂閱自己錄影機的 RecordingStalled");
        Assert.True(resumedAt > 0, "工作階段沒有訂閱自己錄影機的 RecordingResumed");
        Assert.True(startAt > stalledAt, "RecordingStalled 必須在 StartAsync 之前訂閱");
        Assert.True(startAt > resumedAt, "RecordingResumed 必須在 StartAsync 之前訂閱");
    }

    [Fact]
    public void 工作階段與管理器都會把停滯事件往上轉發()
    {
        var session = SourceContract.Read(SessionCode);
        Assert.Contains("public event EventHandler<RecordingStallInfo>? RecordingStalled;", session);
        Assert.Contains("public event EventHandler<int>? RecordingResumed;", session);

        // 管理器是牆唯一認識的物件；少了這一層轉發，工作階段的事件永遠到不了畫面。
        var create = SourceContract.BodyOf(SourceContract.Read(ManagerCode), "CreateSession");
        Assert.Contains("session.RecordingStalled +=", create);
        Assert.Contains("session.RecordingResumed +=", create);
    }

    [Fact]
    public void 主視窗有接上管理器轉發的停滯事件()
    {
        // 排程器（CreateScheduler）與管理器（CreateManager）是兩條獨立接線；
        // 只接排程器時，牆上手動錄影失敗在畫面上完全看不出來。
        var create = SourceContract.BodyOf(SourceContract.Read(MainCode), "CreateManager");

        Assert.Contains("manager.RecordingStalled += OnRecordingStalled;", create);
        Assert.Contains("manager.RecordingResumed += OnRecordingResumed;", create);
    }

    [Fact]
    public void 每秒刷新與狀態文字重畫都不得繞過ApplyCellState()
    {
        // 這條就是「畫面正常但下方的線是紅色的」的直接成因：每秒一次的 metrics 更新
        // 把停滯文字蓋成正常 fps 文字，而色條仍留在原地——原因在 1 秒內就消失了。
        var source = SourceContract.Read(MainCode);

        foreach (var method in new[] { "UpdateCellMetrics", "RefreshCellStatusText" })
        {
            var body = SourceContract.BodyOf(source, method);
            Assert.True(body.Length > 0, $"找不到 {method}");
            Assert.DoesNotContain("RenderCellStatusText(", body);
            Assert.Contains("ApplyCellState(", body);
        }
    }

    [Fact]
    public void 停滯時文字與色條由同一個投影點一起寫()
    {
        var apply = SourceContract.BodyOf(SourceContract.Read(MainCode), "ApplyCellState");

        // 文字與色條必須成對：只寫其中一個，就是「紅線沒有說明」或「說明還留著但線變綠」。
        Assert.Contains("_recordingStalls.TryGetValue(owner, out var stall)", apply);
        Assert.Contains("SetCellStatus(cell, stalledText, BrStall)", apply);
        Assert.Contains("ShowCellStateAccent(cell, BrStall, stalledText)", apply);
    }
}
