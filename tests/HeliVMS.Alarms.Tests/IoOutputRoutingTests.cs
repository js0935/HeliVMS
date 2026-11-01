using HeliVMS.Storage;

namespace HeliVMS.Alarms.Tests;

/// <summary>
/// DO 輸出的路由契約：接點來源決定打到哪種硬體（Modbus 線圈 / 攝影機繼電器）。
/// </summary>
/// <remarks>
/// 接點統一登記在 <c>io_ports</c> 之後，連動規則只看埠；實際硬體由路由器決定。
/// 沒有路由時，Modbus 接點的輸出會被誤送到攝影機繼電器——症狀是「規則觸發了，
/// 閘門不動」，而且日誌乾淨。
/// </remarks>
public sealed class IoOutputRoutingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly IoPortRepository _ports;
    private readonly List<(long PortId, bool On)> _cameraWrites = [];
    private readonly List<(int IoChannelId, bool On)> _modbusWrites = [];

    public IoOutputRoutingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-ioroute-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _ports = new IoPortRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private IIoOutputWriter MakeRouter()
    {
        var camera = new ProbeWriter(writes => _cameraWrites.Add(writes));
        var modbus = new ModbusIoOutputWriter(
            _ports,
            (ioChannelId, on) =>
            {
                _modbusWrites.Add((ioChannelId, on));
                return Task.FromResult(true);
            });
        return new RoutingIoOutputWriter(_ports, camera, modbus);
    }

    private sealed class ProbeWriter(Action<(long PortId, bool On)> onWrite) : IIoOutputWriter
    {
        public Task WriteAsync(long outputPortId, bool logicalOn, CancellationToken cancellationToken = default)
        {
            onWrite((outputPortId, logicalOn));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task 攝影機接點的輸出走ONVIF而不打Modbus()
    {
        var portId = _ports.Add(1, IoPortKind.Do, 1, "攝影機繼電器", IoPolarity.NormallyOpen);
        var router = MakeRouter();

        await router.WriteAsync(portId, logicalOn: true);

        Assert.Single(_cameraWrites);
        Assert.Equal((portId, true), _cameraWrites[0]);
        Assert.Empty(_modbusWrites);
    }

    [Fact]
    public async Task Modbus接點的輸出走線圈而不打攝影機繼電器()
    {
        var (_, created) = _ports.EnsureForIoChannel(1, 9001, IoPortKind.Do, 1, "Modbus 繼電器", IoPolarity.NormallyOpen);
        Assert.True(created);

        var portId = _ports.FindByIoChannel(9001)!.Id;
        var router = MakeRouter();

        await router.WriteAsync(portId, logicalOn: true);

        Assert.Single(_modbusWrites);
        Assert.Equal((9001, true), _modbusWrites[0]);
        Assert.Empty(_cameraWrites);
    }

    [Fact]
    public async Task Modbus輸出失敗時回報原因而不丟例外()
    {
        var (_, _) = _ports.EnsureForIoChannel(1, 9002, IoPortKind.Do, 1, "Modbus 繼電器", IoPolarity.NormallyOpen);
        var portId = _ports.FindByIoChannel(9002)!.Id;

        var failures = new List<string>();
        var writer = new ModbusIoOutputWriter(
            _ports,
            (_, _) => Task.FromResult(false),
            reason => failures.Add(reason));

        await writer.WriteAsync(portId, logicalOn: true);

        Assert.NotEmpty(failures);
        Assert.NotNull(writer.LastError);
    }
}
