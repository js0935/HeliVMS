using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

/// <summary>
/// 攝影機 DI 事件監視器（<see cref="CameraDiMonitor"/>）契約。
///
/// <para>
/// 這一段補的是「攝影機的乾接點輸入從來沒進過系統」：ONVIF 沒有讀取 DI 狀態的操作，
/// 狀態只在事件裡，沒有監視器就等於規則與警報只能靠人工模擬。測試用假的事件來源把
/// 訂閱、對應、餵入、重試與停止逐一釘住，不必真的接攝影機。
/// </para>
/// </summary>
public sealed class CameraDiMonitorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly IoPortRepository _ports;
    private readonly IoRuleRepository _rules;
    private readonly AlarmEventRepository _events;
    private readonly AuditLogRepository _audit;
    private readonly int _channelId = 1;

    private IReadOnlyList<OnvifInputRef> _inputs = [new("DigitalInputToken_1", 1, "IN1")];
    private readonly Queue<IReadOnlyList<CameraDiInput>> _batches = new();
    private Exception? _pullThrows;

    /// <summary>一直失敗（不是只失敗一次）：用來驗「同一個失敗不會每一輪都寫稽核」。</summary>
    private Exception? _pullAlwaysThrows;

    private int _subscribeCount;

    public CameraDiMonitorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-camdi-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _ports = new IoPortRepository(_store);
        _rules = new IoRuleRepository(_store);
        _events = new AlarmEventRepository(_store);
        _audit = new AuditLogRepository(_store);
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, $n, $m, NULL, 'h264', 1, 'copy');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", "攝影機 1");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1/cam1");
            });
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private Task<IReadOnlyList<OnvifInputRef>> ListInputs(CancellationToken ct)
        => Task.FromResult(_inputs);

    private Task<Uri> Subscribe(CancellationToken ct)
    {
        _subscribeCount++;
        return Task.FromResult(new Uri("http://127.0.0.1/onvif/events/pull"));
    }

    private Task<IReadOnlyList<CameraDiInput>> Pull(Uri subscription, CancellationToken ct)
    {
        if (_pullAlwaysThrows is not null)
        {
            throw _pullAlwaysThrows;
        }

        if (_pullThrows is not null)
        {
            var ex = _pullThrows;
            _pullThrows = null;   // 只失敗一次：用來驗證重試
            throw ex;
        }

        if (_batches.Count > 0)
        {
            return Task.FromResult(_batches.Dequeue());
        }

        // 沒有事件時模擬設備端的逾時等待（很短，讓測試快）。
        return Task.Delay(20, ct).ContinueWith(_ => (IReadOnlyList<CameraDiInput>)[], ct);
    }

    private CameraDiMonitor Monitor(TimeSpan? retryDelay = null, DiFailureAuditPlanner? failureAudit = null) => new(
        "攝影機 1",
        _ports,
        new IoRuleHost(_ports, _rules, _events),
        ListInputs,
        Subscribe,
        Pull,
        _audit,
        retryDelay ?? TimeSpan.FromMilliseconds(20),
        failureAudit);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task 數位輸入事件會餵進規則鏈並寫出警報()
    {
        var di = _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        _rules.Add(di, IoActionKind.Alarm, null, "io_trigger");
        _batches.Enqueue([new CameraDiInput("DigitalInputToken_1", true), new CameraDiInput("DigitalInputToken_1", true)]);

        using var monitor = Monitor();
        monitor.Start();

        Assert.True(await WaitUntilAsync(() => _events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue).Count > 0),
            "DI 事件應該要落地成警報事件");

        Assert.True(monitor.NotificationCount >= 2);
        Assert.True(monitor.FedCount >= 2);

        // 重複餵同一狀態不會重複落地（引擎的去抖動仍然生效）。
        var row = Assert.Single(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal("io_trigger", row.EventType);
        Assert.Equal(_channelId, row.ChannelId);
    }

    [Fact]
    public async Task 對不到端子的通知只計數不餵入()
    {
        _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        _batches.Enqueue([new CameraDiInput("DigitalInputToken_9", true)]);

        using var monitor = Monitor();
        monitor.Start();

        Assert.True(await WaitUntilAsync(() => monitor.UnmappedCount == 1));
        Assert.Equal(0, monitor.FedCount);
        Assert.Empty(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
    }

    [Fact]
    public async Task 拉取失敗會記錄並重試直到成功()
    {
        var di = _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        _rules.Add(di, IoActionKind.Alarm, null, "io_trigger");
        _pullThrows = new TimeoutException("pull timeout");
        _batches.Enqueue([new CameraDiInput("DigitalInputToken_1", true)]);

        using var monitor = Monitor();
        monitor.Start();

        Assert.True(await WaitUntilAsync(() => monitor.FedCount > 0), "第一次失敗後應該重新訂閱並成功餵入");
        Assert.Equal(1, _audit.Count(new AuditLogQuery { Action = "camera.di.failed" }));
        Assert.True(_subscribeCount >= 2, "重試時應該重新建立訂閱");
    }

    /// <summary>
    /// 同一個失敗<b>不會每一輪重試都寫一筆稽核</b>：只留下第一次。
    /// </summary>
    /// <remarks>
    /// 現場實測（VIVOTEK IP9164-HT，event_service 一直回 HTTP 500）：舊版每 15 秒寫一筆，
    /// 9 天 18,568 筆內容完全相同的列，佔整個錄影類別稽核的 88%。稽核是證據鏈，
    /// 被這種重複列淹掉之後，真正的訊號（<c>recording.stalled</c>、<c>recording.disk_full</c>）
    /// 就找不到了。這一條同時確認監視器<b>仍持續重試</b>——節流只影響稽核，不影響復原。
    /// </remarks>
    [Fact]
    public async Task 同一個失敗不會每一輪都寫稽核()
    {
        _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        _pullAlwaysThrows = new TimeoutException("HTTP 500 InternalServerError");

        using var monitor = Monitor(retryDelay: TimeSpan.FromMilliseconds(10));
        monitor.Start();

        Assert.True(await WaitUntilAsync(() => _subscribeCount >= 8), "失敗後仍要持續重新訂閱");
        Assert.Equal(1, _audit.Count(new AuditLogQuery { Action = "camera.di.failed" }));
    }

    /// <summary>
    /// 失敗原因改變時要再記一筆，並在說明裡寫出連續失敗次數。
    /// </summary>
    /// <remarks>
    /// 「什麼時候換了症狀」比「一直壞」更關鍵（HTTP 500 → 連線逾時通常代表換了一層出問題：
    /// 設備或網路）。連續次數則是操作員判斷「這是持續中的故障」的依據。
    /// </remarks>
    [Fact]
    public async Task 失敗原因改變時會補記一筆並帶出連續次數()
    {
        _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        _pullAlwaysThrows = new TimeoutException("HTTP 500 InternalServerError");

        // 最短間隔歸零：正式值是一分鐘，測試不必真的等。
        var planner = new DiFailureAuditPlanner(minimumInterval: TimeSpan.Zero);
        using var monitor = Monitor(TimeSpan.FromMilliseconds(10), planner);
        monitor.Start();

        Assert.True(await WaitUntilAsync(
            () => _audit.Count(new AuditLogQuery { Action = "camera.di.failed" }) == 1));

        _pullAlwaysThrows = new TimeoutException("連線逾時");

        Assert.True(await WaitUntilAsync(
            () => _audit.Count(new AuditLogQuery { Action = "camera.di.failed" }) >= 2),
            "原因改變是新資訊，應該立刻再寫一筆");

        var rows = _audit.List(new AuditLogQuery { Action = "camera.di.failed" });
        Assert.Contains(rows, r => r.Detail?.Contains("連線逾時", StringComparison.Ordinal) == true
                                   && r.Detail.Contains("已連續失敗", StringComparison.Ordinal));
    }

    /// <summary>
    /// 成功之後再壞掉要重新留一筆：那是新的一次故障。
    /// </summary>
    /// <remarks>
    /// 若節流只認「訊息字串」而不在成功時歸零，「昨晚好了、今早又壞」在稽核裡看起來
    /// 像從未好過，維修紀錄就對不起來。
    /// </remarks>
    [Fact]
    public async Task 成功之後再失敗會重新寫一筆稽核()
    {
        _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        _pullThrows = new TimeoutException("HTTP 500 InternalServerError");

        using var monitor = Monitor(retryDelay: TimeSpan.FromMilliseconds(10));
        monitor.Start();

        Assert.True(await WaitUntilAsync(
            () => _audit.Count(new AuditLogQuery { Action = "camera.di.failed" }) == 1));
        Assert.True(await WaitUntilAsync(() => _subscribeCount >= 2), "第一次失敗後應該重新訂閱成功");

        _pullAlwaysThrows = new TimeoutException("HTTP 500 InternalServerError");

        Assert.True(await WaitUntilAsync(
            () => _audit.Count(new AuditLogQuery { Action = "camera.di.failed" }) == 2),
            "成功過之後的同一個失敗是新的一次故障，要再寫一筆");
    }

    [Fact]
    public async Task 停止後不再拉取()
    {
        _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        using var monitor = Monitor();
        monitor.Start();
        Assert.True(await WaitUntilAsync(() => monitor.NotificationCount >= 0));

        await monitor.StopAsync();
        var notificationsAtStop = monitor.NotificationCount;
        _batches.Enqueue([new CameraDiInput("DigitalInputToken_1", true)]);
        await Task.Delay(120);

        Assert.Equal(notificationsAtStop, monitor.NotificationCount);
    }

    /// <summary>埠對應的三段規則（與繼電器輸出同一套，避免 DI/DO 兩邊行為不一致）。</summary>
    [Fact]
    public void 埠對應以權杖尾數優先於清單順序()
    {
        var inputs = new List<OnvifInputRef>
        {
            new("DigitalInputToken_5", 5, "IN5"),
            new("DigitalInputToken_9", 9, "IN9"),
        };

        var portId = _ports.Add(_channelId, IoPortKind.Di, 9, "後門", IoPolarity.NormallyOpen);
        var port = _ports.Get(portId)!;

        Assert.Equal("DigitalInputToken_9", CameraDiMapping.TokenFor(port, inputs));
        var map = CameraDiMapping.Build([port], inputs);
        Assert.Equal(portId, map["DigitalInputToken_9"]);
    }

    /// <summary>
    /// 設定中心明確綁定的端子優先於端子號推測（與 DO 同一條規則）。
    /// </summary>
    /// <remarks>
    /// 端子號是 1、綁定是 token_9：若綁定被忽略，A 門的信號會觸發 B 門的規則。
    /// </remarks>
    [Fact]
    public void 綁定的端子優先於端子號推測()
    {
        var inputs = new List<OnvifInputRef>
        {
            new("DigitalInputToken_1", 1, "IN1"),
            new("DigitalInputToken_9", 9, "IN9"),
        };

        var portId = _ports.Add(
            _channelId, IoPortKind.Di, 1, "後門", IoPolarity.NormallyOpen, terminalToken: "DigitalInputToken_9");
        var port = _ports.Get(portId)!;

        Assert.Equal("DigitalInputToken_9", CameraDiMapping.TokenFor(port, inputs));
        Assert.Equal(portId, CameraDiMapping.Build([port], inputs)["DigitalInputToken_9"]);
    }

    /// <summary>
    /// 綁定失效時不再退回「猜一顆」，寧可少一個事件。
    /// </summary>
    /// <remarks>
    /// 退回尾數規則會讓別顆端子的訊號驅動這個埠的規則——沒有例外、沒有日誌，
    /// 只有事件裡出現一個從未發生過的門磁觸發。
    /// </remarks>
    [Fact]
    public void 綁定失效時不會退回端子號推測()
    {
        var inputs = new List<OnvifInputRef> { new("DigitalInputToken_1", 1, "IN1") };
        var portId = _ports.Add(
            _channelId, IoPortKind.Di, 1, "後門", IoPolarity.NormallyOpen, terminalToken: "DigitalInputToken_99");
        var port = _ports.Get(portId)!;

        Assert.Null(CameraDiMapping.TokenFor(port, inputs));
        Assert.False(CameraDiMapping.Build([port], inputs).ContainsKey("DigitalInputToken_1"));
    }

    [Fact]
    public void 沒有線索時以端子號當一起算序號並在超出時回空()
    {
        var inputs = new List<OnvifInputRef> { new("alpha", 0, "第一"), new("beta", 0, "第二") };

        var firstId = _ports.Add(_channelId, IoPortKind.Di, 1, "無線索", IoPolarity.NormallyOpen);
        Assert.Equal("alpha", CameraDiMapping.TokenFor(_ports.Get(firstId)!, inputs));

        var thirdId = _ports.Add(_channelId, IoPortKind.Di, 3, "無線索", IoPolarity.NormallyOpen);
        Assert.Null(CameraDiMapping.TokenFor(_ports.Get(thirdId)!, inputs));
        Assert.False(CameraDiMapping.Build([_ports.Get(thirdId)!], inputs).ContainsKey("alpha"));
    }
}
