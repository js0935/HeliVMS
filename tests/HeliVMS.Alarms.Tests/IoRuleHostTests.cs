using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

/// <summary>
/// M74 落地層（<see cref="IoRuleHost"/>）契約：規則觸發後警報要真的寫進 alarm_events、
/// DO 要真的呼叫輸出寫入器。M74/M75 只交付引擎與模擬視窗，這裡補的正是「觸發卻沒落地」
/// 那段——沒有這些測試，規則看似生效、系統卻不留痕跡。
/// </summary>
public sealed class IoRuleHostTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly IoPortRepository _ports;
    private readonly IoRuleRepository _rules;
    private readonly AlarmEventRepository _events;
    private readonly RecordingWriter _writer = new();
    private readonly int _channelId = 1;

    public IoRuleHostTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-iorule-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _ports = new IoPortRepository(_store);
        _rules = new IoRuleRepository(_store);
        _events = new AlarmEventRepository(_store);
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, $n, $m, NULL, 'h264', 1, 'copy');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", "感測 IO 頻道");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/sensor");
            });
    }

    private IoRuleHost Host() => new(_ports, _rules, _events, _writer);

    [Fact]
    public async Task 上升沿觸發警報會落地一筆綁定頻道的alarm_events()
    {
        var di = _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        _rules.Add(di, IoActionKind.Alarm, null, "io_trigger");
        var host = Host();

        await host.FeedAsync(di, physicalOn: false);   // 基準（無上昇沿）
        Assert.Empty(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));

        var actions = await host.FeedAsync(di, physicalOn: true);

        var action = Assert.Single(actions);
        Assert.Equal(IoActionKind.Alarm, action.Kind);
        var row = Assert.Single(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal("io_trigger", row.EventType);
        Assert.Equal(_channelId, row.ChannelId);
        Assert.Contains($"port={di}", row.Detail);
    }

    [Fact]
    public async Task 同值重複餵入不會重複落地()
    {
        var di = _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        _rules.Add(di, IoActionKind.Alarm, null, "io_trigger");
        var host = Host();

        await host.FeedAsync(di, physicalOn: true);
        await host.FeedAsync(di, physicalOn: true);
        await host.FeedAsync(di, physicalOn: true);

        Assert.Single(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
    }

    [Fact]
    public async Task 冷卻窗內不重複觸發_逾時後可再觸發()
    {
        var di = _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        _rules.Add(di, IoActionKind.Alarm, null, "io_trigger", retriggerSec: 300);
        var host = Host();

        await host.FeedAsync(di, physicalOn: false);
        await host.FeedAsync(di, physicalOn: true);    // 第一次觸發
        await host.FeedAsync(di, physicalOn: false);   // 降沿
        await host.FeedAsync(di, physicalOn: true);    // 冷卻窗內→不再觸發

        Assert.Single(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
    }

    [Fact]
    public async Task 常閉埠物理斷開即邏輯上昇沿並落地()
    {
        var di = _ports.Add(_channelId, IoPortKind.Di, 1, "緊急鈕", IoPolarity.NormallyClosed);
        _rules.Add(di, IoActionKind.Alarm, null, "io_trigger");
        var host = Host();

        await host.FeedAsync(di, physicalOn: true);    // NC 閉合→邏輯 false（基準）
        Assert.Empty(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));

        await host.FeedAsync(di, physicalOn: false);   // 斷開→邏輯 true（上昇沿）

        Assert.Single(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
    }

    [Fact]
    public async Task ToggleDo動作會以邏輯狀態呼叫輸出寫入器()
    {
        var di = _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        var output = _ports.Add(_channelId, IoPortKind.Do, 1, "警報燈", IoPolarity.NormallyOpen);
        _rules.Add(di, IoActionKind.ToggleDo, output, null, retriggerSec: 0);
        var host = Host();

        await host.FeedAsync(di, physicalOn: true);    // 上昇沿→DO 開
        Assert.Equal(((long PortId, bool On))(output, true), _writer.Writes[^1]);

        await host.FeedAsync(di, physicalOn: false);   // 降沿
        await host.FeedAsync(di, physicalOn: true);    // 再上昇沿→DO 關

        Assert.Equal(2, _writer.Writes.Count);
        Assert.Equal((output, false), _writer.Writes[^1]);
        Assert.False(host.GetOutputState(output));
    }

    [Fact]
    public async Task 停用規則不會觸發()
    {
        var di = _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        _rules.Add(di, IoActionKind.Alarm, null, "io_trigger", enabled: false);
        var host = Host();

        var actions = await host.FeedAsync(di, physicalOn: true);

        Assert.Empty(actions);
        Assert.Empty(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
    }

    private sealed class RecordingWriter : IIoOutputWriter
    {
        public List<(long PortId, bool On)> Writes { get; } = [];

        public Task WriteAsync(long outputPortId, bool logicalOn, CancellationToken cancellationToken = default)
        {
            Writes.Add((outputPortId, logicalOn));
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
        }
    }
}
