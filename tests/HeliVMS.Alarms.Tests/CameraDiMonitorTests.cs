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

    private CameraDiMonitor Monitor(TimeSpan? retryDelay = null) => new(
        "攝影機 1",
        _ports,
        new IoRuleHost(_ports, _rules, _events),
        ListInputs,
        Subscribe,
        Pull,
        _audit,
        retryDelay ?? TimeSpan.FromMilliseconds(20));

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
