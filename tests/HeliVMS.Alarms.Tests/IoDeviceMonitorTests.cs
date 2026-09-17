using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

public class IoDeviceMonitorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly IoRepository _io;
    private readonly AlarmEventRepository _events;
    private readonly int _cameraId;

    public IoDeviceMonitorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-iomon-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _io = new IoRepository(_store);
        _events = new AlarmEventRepository(_store);
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, $n, $m, NULL, 'h264', 1, 'copy');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", "IO 綁定頻道");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/io");
            });
        _cameraId = 1;
    }

    [Fact]
    public async Task RisingEdge_WritesIoInputEvent_AndRaisesEvent()
    {
        var (monitor, fake) = await BuildAsync(ch => _io.AddChannel(ch, "DI", 0, "門磁", debounceMs: 0, cameraId: _cameraId));

        var raised = 0;
        monitor.EventInserted += (_, _) => raised++;

        await monitor.PollOnceAsync();                 // 基準：raw=false → stable=false
        fake.Push([true]);
        await monitor.PollOnceAsync();                 // candidate=true（去抖 0 需同態兩次）
        fake.Push([true]);
        await monitor.PollOnceAsync();                 // 升沿觸發

        var row = Assert.Single(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal("io_input", row.EventType);
        Assert.Contains("device=門禁模組", row.Detail);
        Assert.Contains("io=門磁", row.Detail);
        Assert.Contains("index=0", row.Detail);
        Assert.Contains("priority=normal", row.Detail);
        Assert.Contains("state=on", row.Detail);
        Assert.Null(row.EndUtc);
        Assert.Equal(1, raised);
        monitor.Dispose();
    }

    [Fact]
    public async Task FallingEdge_ClosesEventWithEndTime()
    {
        var (monitor, fake) = await BuildAsync(ch => _io.AddChannel(ch, "DI", 0, "門磁", debounceMs: 0, cameraId: _cameraId));

        await monitor.PollOnceAsync();
        fake.Push([true]);
        await monitor.PollOnceAsync();
        fake.Push([true]);
        await monitor.PollOnceAsync();
        var open = _events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue).Single();

        fake.Push([false]);
        await monitor.PollOnceAsync();
        fake.Push([false]);
        await monitor.PollOnceAsync();

        var closed = _events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue).Single();
        Assert.Equal(open.Id, closed.Id);
        Assert.NotNull(closed.EndUtc);
        monitor.Dispose();
    }

    [Fact]
    public async Task NcPolarity_InvertsReportedState()
    {
        // polarity=true（常閉 NC）：raw=false 時視為 active（與 NO 相反）。
        var devId = _io.AddDevice("門禁模組", "127.0.0.1");
        _io.AddChannel(devId, "DI", 1, "緊急鈕", debounceMs: 0, polarity: true, cameraId: _cameraId);
        var fake = new FakeModbusClient([true]);
        var monitor = new IoDeviceMonitor(_io.GetDevice(devId)!, _io, _events, fake);
        monitor.RefreshChannels();

        await monitor.PollOnceAsync();                 // raw=true → NC active=false（基準）
        fake.Push([false]);
        await monitor.PollOnceAsync();                 // candidate=true
        fake.Push([false]);
        await monitor.PollOnceAsync();                 // 升沿觸發

        var open = Assert.Single(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal("io_input", open.EventType);
        Assert.Contains("state=on", open.Detail);

        fake.Push([true]);
        await monitor.PollOnceAsync();                 // candidate=false
        fake.Push([true]);
        await monitor.PollOnceAsync();                 // 降沿觸發

        var closed = _events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue).Single();
        Assert.NotNull(closed.EndUtc);
        monitor.Dispose();
    }

    [Fact]
    public async Task Debounce_FiltersShortGlitch()
    {
        // debounce 極大：短暫閃爍不應觸發。
        var (monitor, fake) = await BuildAsync(ch => _io.AddChannel(ch, "DI", 0, "門磁", debounceMs: 100_000));

        await monitor.PollOnceAsync();                 // 基準 false
        fake.Push([true]);
        await monitor.PollOnceAsync();                 // candidate=true（未過 debounce）
        fake.Push([false]);
        await monitor.PollOnceAsync();                 // 回到 raw=false（與 stable 一致，candidate 重置）

        Assert.Empty(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        monitor.Dispose();
    }

    [Fact]
    public async Task DisabledChannel_IsIgnored()
    {
        var devId = _io.AddDevice("門禁模組", "127.0.0.1");
        var chId = _io.AddChannel(devId, "DI", 0, "停用輸入", debounceMs: 0, cameraId: _cameraId);
        _io.SetChannelEnabled(chId, enabled: false);

        var monitor = new IoDeviceMonitor(_io.GetDevice(devId)!, _io, _events, new FakeModbusClient([true]));
        monitor.RefreshChannels();
        await monitor.PollOnceAsync();

        Assert.Empty(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        monitor.Dispose();
    }

    [Fact]
    public async Task UnboundDi_DoesNotWriteEvent_ButRaisesState()
    {
        // DI 未綁定相機：不寫 alarm_events（FK 防護），但 StateChanged 仍送出。
        var devId = _io.AddDevice("門禁模組", "127.0.0.1");
        _io.AddChannel(devId, "DI", 0, "未綁定輸入", debounceMs: 0);
        var fake = new FakeModbusClient([false]);
        var monitor = new IoDeviceMonitor(_io.GetDevice(devId)!, _io, _events, fake);
        monitor.RefreshChannels();
        var states = 0;
        monitor.StateChanged += (_, _) => states++;

        await monitor.PollOnceAsync();                 // 基準 false
        fake.Push([true]);
        await monitor.PollOnceAsync();                 // 候選 true
        fake.Push([true]);
        await monitor.PollOnceAsync();                 // 升沿

        Assert.Empty(_events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue));
        Assert.True(states >= 1, $"預期至少一次 StateChanged，實際 {states}");
        monitor.Dispose();
    }

    [Fact]
    public async Task RefreshChannels_RejectsDirectionMismatch()
    {
        // RefreshChannels 只載入 DI；DO 通道不會有 DI 輪詢行為。
        var devId = _io.AddDevice("門禁模組", "127.0.0.1");
        _io.AddChannel(devId, "DO", 0, "警報燈");
        var monitor = new IoDeviceMonitor(_io.GetDevice(devId)!, _io, _events, new FakeModbusClient([true]));
        monitor.RefreshChannels();
        await monitor.PollOnceAsync();
        var rows = _events.ListByRange(null, DateTime.MinValue, DateTime.MaxValue);
        Assert.Empty(rows);
        monitor.Dispose();
    }

    [Fact]
    public async Task WriteOutputByChannelAsync_ControlsDoOnly()
    {
        var devId = _io.AddDevice("門禁模組", "127.0.0.1");
        var doId = _io.AddChannel(devId, "DO", 2, "警報燈");
        var diId = _io.AddChannel(devId, "DI", 0, "門磁", cameraId: _cameraId);

        var (monitor, fake) = await BuildAsync(_ => 0, devId);
        Assert.True(await monitor.WriteOutputByChannelAsync(doId, on: true));
        Assert.Equal(1, fake.CoilWrites);
        Assert.True(fake.LastOn);
        Assert.False(await monitor.WriteOutputByChannelAsync(diId, on: true));
        Assert.Equal(1, fake.CoilWrites);   // DI 不寫出
        monitor.Dispose();
    }

    private async Task<(IoDeviceMonitor M, FakeModbusClient C)> BuildAsync(Func<int, int> seedChannel, int? knownDeviceId = null)
    {
        var devId = knownDeviceId ?? _io.AddDevice("門禁模組", "127.0.0.1");
        if (knownDeviceId is null)
        {
            seedChannel(devId);
        }

        var fake = new FakeModbusClient([false]);
        var monitor = new IoDeviceMonitor(_io.GetDevice(devId)!, _io, _events, fake);
        monitor.RefreshChannels();
        return (monitor, fake);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private sealed class FakeModbusClient : IModbusTcpClient
    {
        private readonly Queue<IReadOnlyList<bool>> _pending = new();
        private IReadOnlyList<bool> _last;

        public FakeModbusClient(IReadOnlyList<bool> initial)
        {
            _last = initial;
        }

        public int CoilWrites { get; private set; }
        public bool LastOn { get; private set; }
        public int DiscreteReads { get; private set; }

        public void Push(IReadOnlyList<bool> next) => _pending.Enqueue(next);

        public Task<IReadOnlyList<bool>> ReadDiscreteInputsAsync(byte unitId, ushort start, ushort count, CancellationToken ct = default)
        {
            DiscreteReads++;
            var src = _pending.Count > 0 ? _pending.Dequeue() : _last;
            var result = new bool[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = src[Math.Min(i, src.Count - 1)];
            }

            _last = result;
            return Task.FromResult(_last);
        }

        public Task<IReadOnlyList<bool>> ReadCoilsAsync(byte unitId, ushort start, ushort count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<bool>>(new bool[count]);

        public Task<bool> WriteSingleCoilAsync(byte unitId, ushort address, bool on, CancellationToken ct = default)
        {
            CoilWrites++;
            LastOn = on;
            return Task.FromResult(true);
        }
    }
}