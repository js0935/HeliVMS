namespace HeliVMS.Alarms.Tests;

/// <summary>
/// 攝影機 DI 失敗稽核的節流規則（<see cref="DiFailureAuditPlanner"/>）。
///
/// <para>
/// 為什麼會有這條規則：現場一台 VIVOTEK IP9164-HT 的 event_service 一直回 HTTP 500，
/// 監視器每 15 秒重試一次、每重試一次寫一筆稽核，9 天累積 <b>18,568 筆內容完全相同</b>的
/// <c>camera.di.failed</c> 列（佔整個錄影類別稽核的 88%）。稽核是證據鏈，被這種重複列淹掉
/// 之後，「什麼時候開始停止錄影」「誰刪了頻道」都得先翻過兩萬筆一樣的字串。
/// </para>
/// <para>
/// 純狀態機＋注入時鐘，所以「一小時後會再寫一筆」不必真的等一小時。
/// </para>
/// </summary>
public sealed class DiFailureAuditPlannerTests
{
    private DateTime _now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private DiFailureAuditPlanner Planner(TimeSpan? minimum = null)
        => new(() => _now, minimumInterval: minimum);

    [Fact]
    public void 第一次失敗一定會寫稽核()
    {
        var planner = Planner();

        Assert.True(planner.ShouldAudit("HTTP 500", out var consecutive));
        Assert.Equal(1, consecutive);
    }

    [Fact]
    public void 同一個失敗在半小時內不會再寫一筆()
    {
        var planner = Planner();
        Assert.True(planner.ShouldAudit("HTTP 500", out _));

        _now = _now.AddMinutes(30);
        Assert.False(planner.ShouldAudit("HTTP 500", out var consecutive));
        Assert.Equal(2, consecutive);
    }

    /// <summary>
    /// 稽核不能對一個持續中的故障完全靜音：一小時到了仍要留下一筆心跳，
    /// 否則「這台攝影機的 DI 已經壞了一個月」在這份證據裡等於從未發生。
    /// </summary>
    [Fact]
    public void 同一個失敗超過一小時會再寫一筆心跳()
    {
        var planner = Planner();
        Assert.True(planner.ShouldAudit("HTTP 500", out _));

        _now = _now.AddMinutes(61);
        Assert.True(planner.ShouldAudit("HTTP 500", out var consecutive));
        Assert.Equal(2, consecutive);

        // 心跳之後再半小時：靜音，不會因為「剛剛寫過」就變成每輪都寫。
        _now = _now.AddMinutes(30);
        Assert.False(planner.ShouldAudit("HTTP 500", out _));
    }

    /// <summary>
    /// 原因改變是新的資訊（「什麼時候換了症狀」比「一直壞」更關鍵），
    /// 但仍在最短間隔之後才寫，否則逾時／500 輪流出現時每一輪都是一筆。
    /// </summary>
    [Fact]
    public void 原因改變時仍受最短間隔限制()
    {
        var planner = Planner();
        Assert.True(planner.ShouldAudit("HTTP 500", out _));

        _now = _now.AddSeconds(20);
        Assert.False(planner.ShouldAudit("連線逾時", out _));

        _now = _now.AddSeconds(45);
        Assert.True(planner.ShouldAudit("連線逾時", out var consecutive));
        Assert.Equal(3, consecutive);
    }

    [Fact]
    public void 最短間隔過後原因改變會再寫一筆()
    {
        var planner = Planner();
        Assert.True(planner.ShouldAudit("HTTP 500", out _));

        _now = _now.AddSeconds(70);
        Assert.True(planner.ShouldAudit("連線逾時", out _));
    }

    /// <summary>
    /// 成功之後再壞掉，是<b>新的一次故障</b>，必須留下一筆——否則「昨晚好了、今早又壞」
    /// 在稽核裡看起來像一直沒好過（或從未壞過）。
    /// </summary>
    [Fact]
    public void 成功之後再失敗會重新寫一筆稽核()
    {
        var planner = Planner();
        Assert.True(planner.ShouldAudit("HTTP 500", out _));

        planner.Reset();

        _now = _now.AddSeconds(5);
        Assert.True(planner.ShouldAudit("HTTP 500", out var consecutive));
        Assert.Equal(1, consecutive);
    }

    /// <summary>
    /// 連續次數是「從上次成功以來」的總次數，不受有沒有寫稽核影響：
    /// 稽核說明要能說出「已連續失敗 214 次」，操作員才知道這是持續中的故障。
    /// </summary>
    [Fact]
    public void 連續失敗次數會累計到下一次寫入()
    {
        var planner = Planner();
        planner.ShouldAudit("HTTP 500", out _);

        // 15 秒重試一次、共 200 輪＝50 分鐘：全部靜音（一小時還沒到）。
        for (var i = 0; i < 200; i++)
        {
            _now = _now.AddSeconds(15);
            Assert.False(planner.ShouldAudit("HTTP 500", out _));
        }

        // 再過 10 分鐘（累計 60 分 1 秒）：心跳，次數含每一輪重試。
        _now = _now.AddSeconds(601);
        Assert.True(planner.ShouldAudit("HTTP 500", out var consecutive));
        Assert.Equal(202, consecutive);
        Assert.Equal(consecutive, planner.ConsecutiveFailures);
    }

    [Fact]
    public void 空白原因視為同一個未知原因()
    {
        var planner = Planner();
        Assert.True(planner.ShouldAudit(null, out _));

        _now = _now.AddSeconds(30);
        Assert.False(planner.ShouldAudit("   ", out _));
    }
}
