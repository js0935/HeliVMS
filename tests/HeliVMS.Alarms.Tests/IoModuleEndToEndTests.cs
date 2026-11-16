using System.Net;
using System.Net.Sockets;
using HeliVMS.Shared;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Alarms.Tests;

/// <summary>
/// 操作員的場景，從頭到尾跑一次：<b>新增一顆 ADAM-6250 → 看到它的 DI/DO → DI 變化 → 規則動作
/// → 對模組寫出 DO</b>。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要有這一條：模組（<c>io_devices</c>）、通道（<c>io_channels</c>）、接點（<c>io_ports</c>）、
/// 規則（<c>io_rules</c>）與輪詢器是五個各有測試的零件，但<b>串起來</b>只有手上有實機的人驗得到。
/// 這條測試在 loopback 上開一個真的 Modbus/TCP 伺服器（比照 ModbusTcpClientTests 的假伺服器），
/// 讓整條鏈真的走 TCP：少了任何一段——沒建立通道、接點沒映、規則掛錯邊、輪詢沒接規則引擎——
/// 都會在這裡紅。
/// </para>
/// <para>
/// 假模組只實作 ADAM-6250 用到的三個功能碼：讀離散輸入（FC02）、讀線圈（FC01）、寫單一線圈
/// （FC05）。這是「照規範寫的假設備」，不是「照我們的實作寫的假設備」：請求位址與回應格式都由
/// 測試自己解析，所以我們的客戶端若把位址算錯，這裡也會紅。
/// </para>
/// </remarks>
public sealed class IoModuleEndToEndTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-ioe2e-{Guid.NewGuid():N}.db");
    private readonly SqliteStore _store;
    private readonly IoRepository _io;
    private readonly IoPortRepository _ports;
    private readonly IoRuleRepository _rules;
    private readonly AlarmEventRepository _events;
    private readonly FakeAdam _adam = new();

    public IoModuleEndToEndTests()
    {
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _io = new IoRepository(_store);
        _ports = new IoPortRepository(_store);
        _rules = new IoRuleRepository(_store);
        _events = new AlarmEventRepository(_store);
    }

    public void Dispose()
    {
        _adam.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task 新增ADAM6250後DI變化會觸發規則並對同一台模組寫出DO()
    {
        _adam.Start();

        // ① 新增模組：8 DI + 8 DO
        var enrollment = IoModuleEnroller.Enroll(
            _io,
            IoModuleProfile.Adam6250,
            "127.0.0.1",
            port: _adam.Port,
            pollMs: 50);
        Assert.Equal(8, enrollment.DiCount);
        Assert.Equal(8, enrollment.DoCount);

        // ② 通道映成接點（規則掛在接點上；桌面端由 IoMonitorHost 呼叫同一個 IoPortMirror）
        Assert.Equal(16, IoPortMirror.Sync(_io, _ports));
        var diPort = _ports.List().Single(p => p.IoChannelId == _io.ListChannels(direction: "DI").Single(c => c.IoIndex == 2).Id);
        var doPort = _ports.List().Single(p => p.IoChannelId == _io.ListChannels(direction: "DO").Single(c => c.IoIndex == 0).Id);
        Assert.Equal(IoPortKind.Di, diPort.Kind);
        Assert.Equal(IoPortKind.Do, doPort.Kind);

        // ③ 規則：第 3 個 DI 觸發 → 第 1 個 DO 導通 5 秒
        _rules.Add(diPort.Id, IoActionKind.ToggleDo, doPort.Id, eventType: null, retriggerSec: 0, outputSeconds: 5);

        // ④ 輪詢器：真 Modbus 客戶端、假模組、接上同一套規則引擎
        var device = _io.GetDevice(enrollment.DeviceId)!;

        // 監看器與規則引擎互相需要（監看器要餵規則、規則要靠監看器寫出），所以輸出寫入器透過
        // 一個可變區域變數延後解析——桌面端的 IoMonitorHost 也是同樣的接法。
        IoDeviceMonitor? monitor = null;
        var ruleHost = new IoRuleHost(
            _ports,
            _rules,
            _events,
            new ModbusIoOutputWriter(_ports, (channelId, on) => monitor!.WriteOutputByChannelAsync(channelId, on)));
        monitor = new IoDeviceMonitor(
            device,
            _io,
            _events,
            ruleHost: ruleHost,
            ports: _ports,
            client: new ModbusTcpClient("127.0.0.1", _adam.Port));
        using var _ = monitor;
        monitor.Start();

        // ⑤ 先確認輪詢真的在跑（否則後面的等待只會等到逾時，看不出是哪一段壞掉）
        var pollDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (monitor.PollCount == 0 && DateTime.UtcNow < pollDeadline)
        {
            await Task.Delay(25);
        }

        Assert.True(monitor.PollCount > 0, $"輪詢沒有成功過；假模組收到的離散輸入讀取={_adam.DiscreteReads}");

        // ⑥ 模組上的第 3 點閉合
        _adam.SetDiscreteInput(2, true);

        // ⑦ 規則要求寫出：假模組真的收到 FC05（位址 0 ＝ 第 1 個 DO，值為 ON）
        await _adam.WaitForCoilWriteAsync(address: 0, on: true, TimeSpan.FromSeconds(10));
        Assert.Contains(_adam.CoilWrites, w => w is { Address: 0, On: true });

        // ⑦ 未綁定相機的模組接點**不會**產生事件列（alarm_events.channel_id 是 NOT NULL + FK）：
        //    這是資料模型的事實，所以規則只能驅動輸出。要在事件中心看到它就必須綁一台相機
        //    （下一條測試），而 UI 也必須把這件事說出來，否則操作員會以為規則沒生效。
        var rows = _events.ListByRange(null, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
        Assert.DoesNotContain(rows, r => (r.Detail ?? string.Empty).Contains($"port={diPort.Id}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 模組接點綁定相機後DI觸發會產生事件列()
    {
        _adam.Start();

        var enrollment = IoModuleEnroller.Enroll(_io, IoModuleProfile.Adam6250, "127.0.0.1", port: _adam.Port, pollMs: 50);

        // 一台相機：alarm_events 的 channel_id 指向它（模組接點要綁相機才能進事件平面）。
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, $n, $m, NULL, 'h264', 1, 'copy');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", "大門");
                cmd.Parameters.AddWithValue("$m", "rtsp://127.0.0.1:8554/gate");
            });
        var cameraId = _store.Query("SELECT last_insert_rowid();", static r => { r.Read(); return r.GetInt32(0); });

        // 把模組的第 3 個 DI 綁到那台相機（設定頁／IO 頁面做的事），再映成接點。
        var diChannel = _io.ListChannels(direction: "DI").Single(c => c.IoIndex == 2);
        _store.Execute("UPDATE io_channels SET camera_id = $c WHERE id = $d;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", cameraId);
                cmd.Parameters.AddWithValue("$d", diChannel.Id);
            });
        IoPortMirror.Sync(_io, _ports);
        var diPort = _ports.List().Single(p => p.IoChannelId == diChannel.Id);
        Assert.Equal(cameraId, diPort.ChannelId);

        _rules.Add(diPort.Id, IoActionKind.Alarm, null, "io_trigger", retriggerSec: 0);

        var device = _io.GetDevice(enrollment.DeviceId)!;
        using var monitor = new IoDeviceMonitor(
            device, _io, _events, ports: _ports, client: new ModbusTcpClient("127.0.0.1", _adam.Port));
        monitor.Start();

        // 先讓第一次輪詢建立基準（啟動時就已經閉合的接點刻意不觸發——那是「開機誤報」防護），
        // 再讓第 3 點閉合，這才是一次真正的邊緣變化。
        var baselineDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (monitor.PollCount == 0 && DateTime.UtcNow < baselineDeadline)
        {
            await Task.Delay(25);
        }

        _adam.SetDiscreteInput(2, true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        AlarmEventRecord? row = null;
        while (row is null && DateTime.UtcNow < deadline)
        {
            row = _events.ListByRange(cameraId, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5)).FirstOrDefault();
            if (row is null)
            {
                await Task.Delay(25);
            }
        }

        Assert.True(
            row is not null,
            $"PollCount={monitor.PollCount}; 事件=[{string.Join(" | ", _events.ListByRange(null, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5)).Select(e => $"{e.ChannelId}:{e.EventType}:{e.Detail}"))}]; 通道={string.Join(",", _io.ListChannels(deviceId: enrollment.DeviceId).Select(c => $"{c.Direction}{c.IoIndex}/cam={c.CameraId}"))}; 讀取={_adam.DiscreteReads}");
        Assert.Equal(cameraId, row!.ChannelId);
        Assert.Equal("io_input", row.EventType);
        Assert.Contains("io=DI 3", row.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 模組連不上時不會拋例外且輪詢次數留在零()
    {
        // 操作員填錯位址是常態（模組還沒上電、打錯 IP）。連不上的模組必須只是「沒有狀態」，
        // 不能讓整個 IO 監看停掉——那會連帶讓其他模組的規則也不動作。
        var enrollment = IoModuleEnroller.Enroll(_io, IoModuleProfile.Adam6250, "127.0.0.1", port: 1, pollMs: 50);
        var device = _io.GetDevice(enrollment.DeviceId)!;

        using var monitor = new IoDeviceMonitor(device, _io, _events, client: new ModbusTcpClient("127.0.0.1", 1, TimeSpan.FromMilliseconds(200)));
        monitor.Start();
        await Task.Delay(400);

        Assert.Equal(0, monitor.PollCount);
    }

    /// <summary>假 ADAM-6250：Modbus/TCP 的三個功能碼（FC01/02/05）。</summary>
    private sealed class FakeAdam : IAsyncDisposable
    {
        private readonly bool[] _discretes = new bool[8];
        private readonly bool[] _coils = new bool[8];
        private readonly List<(int Address, bool On)> _coilWrites = [];
        private readonly object _gate = new();
        private TcpListener? _listener;
        private Task? _accept;
        private CancellationTokenSource? _cts;

        public int Port { get; private set; }

        private int _discreteReads;

        /// <summary>收到幾次離散輸入讀取（FC02）——用來分辨「輪詢沒跑」與「規則沒接上」。</summary>
        public int DiscreteReads => Volatile.Read(ref _discreteReads);

        public IReadOnlyList<(int Address, bool On)> CoilWrites
        {
            get
            {
                lock (_gate)
                {
                    return [.. _coilWrites];
                }
            }
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            _accept = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void SetDiscreteInput(int index, bool on)
        {
            lock (_gate)
            {
                _discretes[index] = on;
            }
        }

        public async Task WaitForCoilWriteAsync(int address, bool on, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (CoilWrites.Any(w => w.Address == address && w.On == on))
                {
                    return;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException($"等不到對線圈 {address} 寫入 {(on ? "ON" : "OFF")}；收到的寫入："
                + string.Join(", ", CoilWrites.Select(w => $"{w.Address}={(w.On ? 1 : 0)}")));
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(token);
                    _ = HandleAsync(client, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                var stream = client.GetStream();

                // 一次連線可能承載多個請求（客戶端每次呼叫開一條，但多讀也不吃虧）。
                while (!token.IsCancellationRequested)
                {
                    var header = new byte[7];
                    try
                    {
                        await stream.ReadExactlyAsync(header, token);
                    }
                    catch (EndOfStreamException)
                    {
                        return;
                    }

                    var length = (header[4] << 8) | header[5];
                    if (length < 2)
                    {
                        return;
                    }

                    var body = new byte[length - 1];
                    await stream.ReadExactlyAsync(body, token);

                    // Modbus/TCP 的封框：回應＝MBAP 標頭 ＋ PDU。少了標頭，客戶端會把 PDU 的
                    // 頭幾個位元組當成交易識別碼與長度而解析失敗——而那在我們的客戶端裡是
                    // 「讀不到」而不是「連不上」，測試只會看到輪詢次數永遠是 0。
                    var pdu = Respond(body);
                    var response = new byte[7 + pdu.Length];
                    response[0] = header[0];
                    response[1] = header[1];
                    response[2] = 0;
                    response[3] = 0;
                    var payloadLength = pdu.Length + 1;
                    response[4] = (byte)(payloadLength >> 8);
                    response[5] = (byte)(payloadLength & 0xFF);
                    response[6] = header[6];
                    pdu.CopyTo(response, 7);

                    await stream.WriteAsync(response, token);
                    await stream.FlushAsync(token);
                }
            }
        }

        /// <summary>依請求的 PDU 產生回應 PDU（不含前面 7 個位元組的 MBAP）。</summary>
        private byte[] Respond(byte[] pdu)
        {
            var function = pdu[0];
            switch (function)
            {
                case 0x02: // 讀離散輸入
                {
                    Interlocked.Increment(ref _discreteReads);
                    var address = (pdu[1] << 8) | pdu[2];
                    var count = (pdu[3] << 8) | pdu[4];
                    return ReadBits(_discretes, function, address, count);
                }

                case 0x01: // 讀線圈
                {
                    var address = (pdu[1] << 8) | pdu[2];
                    var count = (pdu[3] << 8) | pdu[4];
                    return ReadBits(_coils, function, address, count);
                }

                case 0x05: // 寫單一線圈
                {
                    var address = (pdu[1] << 8) | pdu[2];
                    var on = pdu[3] == 0xFF;
                    if (address < _coils.Length)
                    {
                        lock (_gate)
                        {
                            _coils[address] = on;
                            _coilWrites.Add((address, on));
                        }
                    }

                    // 正常回應＝回音整個請求。
                    return [.. pdu];
                }

                default:
                {
                    // 不支援的功能碼：回例外回應（FC | 0x80 + 例外碼 01）。
                    return [(byte)(function | 0x80), 0x01];
                }
            }
        }

        private byte[] ReadBits(bool[] source, byte function, int address, int count)
        {
            var byteCount = (count + 7) / 8;
            var response = new byte[2 + byteCount];
            response[0] = function;
            response[1] = (byte)byteCount;
            lock (_gate)
            {
                for (var i = 0; i < count; i++)
                {
                    var index = address + i;
                    if (index >= 0 && index < source.Length && source[index])
                    {
                        response[2 + (i / 8)] |= (byte)(1 << (i % 8));
                    }
                }
            }

            return response;
        }

        public async ValueTask DisposeAsync()
        {
            _cts?.Cancel();
            _listener?.Stop();
            if (_accept is not null)
            {
                try
                {
                    await _accept.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception)
                {
                    // 收尾路徑：伺服器已停。
                }
            }

            _cts?.Dispose();
        }
    }
}
