using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

/// <summary>
/// 攝影機繼電器輸出（<see cref="OnvifIoOutputWriter"/>）契約。
///
/// <para>
/// 為什麼要這一層：ONVIF 的 <c>SetRelayOutputState</c> 早就實作好，但只有 Web API 在用，
/// 桌面端的規則鏈接的是空寫入器——DI 觸發後警報事件有寫、繼電器不會動，現場的閘門與
/// 警燈毫無反應，而畫面（引擎狀態）看起來一切正常。
/// </para>
/// <para>
/// 最容易出錯的是「埠的接線端子號 → ONVIF 不透明權杖」的對應：猜錯就是驅動錯的繼電器，
/// 所以三段規則（權杖完全相同／尾數相符／1 起算序號）要逐一釘住，且找不到時寧可什麼都不做。
/// </para>
/// </summary>
public sealed class OnvifIoOutputWriterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly IoPortRepository _ports;
    private readonly ChannelRepository _channels;
    private readonly DeviceRepository _devices;
    private readonly AuditLogRepository _audit;
    private readonly int _channelId;
    private readonly int _deviceId;

    private readonly List<(int DeviceId, string Token, bool Active)> _switches = [];
    private readonly List<int> _listCalls = [];
    private IReadOnlyList<OnvifRelayRef> _relays = [new("RelayOutputToken_1", "Relay 1")];
    private Exception? _listThrows;
    private Exception? _switchThrows;

    public OnvifIoOutputWriterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-ioout-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _ports = new IoPortRepository(_store);
        _channels = new ChannelRepository(_store);
        _audit = new AuditLogRepository(_store);
        _devices = new DeviceRepository(_store, _audit);

        _deviceId = _devices.Add("測試攝影機", "192.168.1.50", 80, "admin", "pw", "onvif");
        _channelId = _channels.Add("攝影機 1", "rtsp://127.0.0.1/cam1", deviceId: _deviceId);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private OnvifIoOutputWriter Writer() => new(
        _ports,
        _channels,
        _devices,
        ListRelays,
        SwitchRelay,
        _audit);

    private Task<IReadOnlyList<OnvifRelayRef>> ListRelays(DeviceRecord device, CancellationToken ct)
    {
        _listCalls.Add(device.Id);
        if (_listThrows is not null)
        {
            throw _listThrows;
        }

        return Task.FromResult(_relays);
    }

    private Task SwitchRelay(DeviceRecord device, string token, bool active, CancellationToken ct)
    {
        if (_switchThrows is not null)
        {
            throw _switchThrows;
        }

        _switches.Add((device.Id, token, active));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 規則觸發的DO會真的切換設備繼電器()
    {
        var doPort = _ports.Add(_channelId, IoPortKind.Do, 1, "閘門", IoPolarity.NormallyOpen);
        var writer = Writer();

        await writer.WriteAsync(doPort, logicalOn: true);

        var call = Assert.Single(_switches);
        Assert.Equal(_deviceId, call.DeviceId);
        Assert.Equal("RelayOutputToken_1", call.Token);
        Assert.True(call.Active);
        Assert.Null(writer.LastError);
    }

    [Fact]
    public async Task 邏輯關閉會以非作動狀態寫入()
    {
        var doPort = _ports.Add(_channelId, IoPortKind.Do, 1, "閘門", IoPolarity.NormallyOpen);

        await Writer().WriteAsync(doPort, logicalOn: false);

        Assert.False(Assert.Single(_switches).Active);
    }

    /// <summary>
    /// 端子號優先用「權杖尾數」對應，而不是清單順序——清單順序是設備自己排的，
    /// 攝影機重開或換型號就可能變動；拿它當主要依據等於驅動錯的繼電器。
    /// </summary>
    /// <remarks>
    /// 案例刻意讓兩者不同：端子 9 是清單中的第二顆（token_9）。若原始碼退回只看序號，
    /// 端子 9 會超出清單長度而回 null（不動作），這個測試就會紅。
    /// </remarks>
    [Fact]
    public async Task 端子號對應以權杖尾數優先於清單順序()
    {
        _relays = [new("RelayOutputToken_5", "大門"), new("RelayOutputToken_9", "後門")];
        var doPort = _ports.Add(_channelId, IoPortKind.Do, 9, "後門警燈", IoPolarity.NormallyOpen);

        await Writer().WriteAsync(doPort, logicalOn: true);

        Assert.Equal("RelayOutputToken_9", Assert.Single(_switches).Token);
    }

    [Fact]
    public void 權杖完全相同時優先採用()
    {
        var portId = _ports.Add(_channelId, IoPortKind.Do, 1, "RelayOutputToken_9", IoPolarity.NormallyOpen);
        var relays = new List<OnvifRelayRef> { new("RelayOutputToken_1", "Relay 1"), new("RelayOutputToken_9", "Relay 9") };

        Assert.Equal("RelayOutputToken_9", OnvifIoOutputWriter.RelayTokenFor(_ports.Get(portId)!, relays));
    }

    [Fact]
    public void 沒有線索時以端子號當一起算序號並在超出時回空()
    {
        var relays = new List<OnvifRelayRef> { new("alpha", "第一顆"), new("beta", "第二顆") };

        var firstId = _ports.Add(_channelId, IoPortKind.Do, 1, "無線索", IoPolarity.NormallyOpen);
        Assert.Equal("alpha", OnvifIoOutputWriter.RelayTokenFor(_ports.Get(firstId)!, relays));

        var thirdId = _ports.Add(_channelId, IoPortKind.Do, 3, "無線索", IoPolarity.NormallyOpen);
        Assert.Null(OnvifIoOutputWriter.RelayTokenFor(_ports.Get(thirdId)!, relays));
    }

    [Fact]
    public void 尾數解析只看結尾數字()
    {
        Assert.Equal(2, OnvifIoOutputWriter.TrailingNumber("Relay_2"));
        Assert.Equal(10, OnvifIoOutputWriter.TrailingNumber("Relay 10"));
        Assert.Null(OnvifIoOutputWriter.TrailingNumber("relay"));
        Assert.Null(OnvifIoOutputWriter.TrailingNumber(""));
        Assert.Null(OnvifIoOutputWriter.TrailingNumber(null));
    }

    [Fact]
    public async Task 頻道未綁定設備時不動作也不丟例外並寫稽核()
    {
        var channelId = _channels.Add("未綁定頻道", "rtsp://127.0.0.1/cam2");
        var doPort = _ports.Add(channelId, IoPortKind.Do, 1, "閘門", IoPolarity.NormallyOpen);
        var writer = Writer();
        var failures = new List<string>();
        writer.Failed += (_, reason) => failures.Add(reason);

        await writer.WriteAsync(doPort, logicalOn: true);

        Assert.Empty(_switches);
        Assert.Equal("channel-not-bound", writer.LastError);
        Assert.Single(failures);
        Assert.Equal(1, _audit.Count(new AuditLogQuery { Action = "io.output.failed" }));
    }

    [Fact]
    public async Task 找不到對應繼電器時不切換也不丟例外()
    {
        _relays = [];
        var doPort = _ports.Add(_channelId, IoPortKind.Do, 1, "閘門", IoPolarity.NormallyOpen);
        var writer = Writer();

        await writer.WriteAsync(doPort, logicalOn: true);

        Assert.Empty(_switches);
        Assert.Equal("relay-not-found", writer.LastError);
    }

    [Fact]
    public async Task 攝影機失敗不會往外丟_規則鏈不中斷()
    {
        _switchThrows = new InvalidOperationException("camera offline");
        var doPort = _ports.Add(_channelId, IoPortKind.Do, 1, "閘門", IoPolarity.NormallyOpen);
        var writer = Writer();

        // 不應擲出：DO 失敗不能讓同一批動作鏈（例如警報事件）半途而廢。
        await writer.WriteAsync(doPort, logicalOn: true);

        Assert.Contains("camera offline", writer.LastError);
        Assert.Empty(_switches);
    }

    [Fact]
    public async Task 列舉繼電器失敗同樣只記錄不丟()
    {
        _listThrows = new TimeoutException("onvif timeout");
        var doPort = _ports.Add(_channelId, IoPortKind.Do, 1, "閘門", IoPolarity.NormallyOpen);
        var writer = Writer();

        await writer.WriteAsync(doPort, logicalOn: true);

        Assert.Contains("onvif timeout", writer.LastError);
        Assert.Empty(_switches);
    }

    [Fact]
    public async Task 非DO埠與已刪除埠都直接略過()
    {
        var diPort = _ports.Add(_channelId, IoPortKind.Di, 1, "門磁", IoPolarity.NormallyOpen);
        var writer = Writer();

        await writer.WriteAsync(diPort, logicalOn: true);   // DI 不該驅動繼電器
        await writer.WriteAsync(9999, logicalOn: true);     // 已刪除的埠

        Assert.Empty(_switches);
        Assert.Empty(_listCalls);   // 連問設備都沒有：不該為非 DO 產生任何 ONVIF 流量
        Assert.Null(writer.LastError);
    }
}
