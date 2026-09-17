namespace HeliVMS.Storage.Tests;

public class IoRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly IoRepository _repo;

    public IoRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-io-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new IoRepository(_store);
    }

    [Fact]
    public void AddDevice_AndListReturnsIt()
    {
        var id = _repo.AddDevice("門口主機", "192.168.1.50", port: 502, unitId: 1, pollMs: 300);

        var dev = Assert.Single(_repo.ListDevices());
        Assert.Equal(id, dev.Id);
        Assert.Equal("門口主機", dev.Name);
        Assert.Equal("modbus_tcp", dev.Protocol);
        Assert.Equal("192.168.1.50", dev.Host);
        Assert.Equal(502, dev.Port);
        Assert.Equal(1, dev.UnitId);
        Assert.Equal(300, dev.PollMs);
        Assert.True(dev.Enabled);
    }

    [Fact]
    public void SetDeviceEnabled_UpdatesFlag()
    {
        var id = _repo.AddDevice("煙霧主機", "192.168.1.51");

        _repo.SetDeviceEnabled(id, enabled: false);

        var dev = _repo.GetDevice(id);
        Assert.NotNull(dev);
        Assert.False(dev!.Enabled);
    }

    [Fact]
    public void GetDevice_MissingReturnsNull()
    {
        Assert.Null(_repo.GetDevice(999));
    }

    [Fact]
    public void DeleteDevice_CascadesChannels()
    {
        var devId = _repo.AddDevice("測試模組", "127.0.0.1");
        _repo.AddChannel(devId, "DI", 0, "門磁");
        _repo.AddChannel(devId, "DO", 0, "警報燈");

        _repo.DeleteDevice(devId);

        Assert.Empty(_repo.ListChannels(deviceId: devId));
        Assert.Empty(_repo.ListDevices());
    }

    [Fact]
    public void AddChannel_PersistsAllFields()
    {
        var devId = _repo.AddDevice("門禁模組", "192.168.1.52");
        var chId = _repo.AddChannel(
            devId, "DI", 3, "大門磁簧", debounceMs: 150, polarity: true, cameraId: 7, alarmPriority: "emergency");

        var ch = _repo.GetChannel(chId);
        Assert.NotNull(ch);
        Assert.Equal("DI", ch!.Direction);
        Assert.Equal(3, ch.IoIndex);
        Assert.Equal("大門磁簧", ch.Name);
        Assert.Equal(150, ch.DebounceMs);
        Assert.True(ch.Polarity);
        Assert.Equal(7, ch.CameraId);
        Assert.Equal("emergency", ch.AlarmPriority);
        Assert.True(ch.Enabled);
    }

    [Fact]
    public void ListChannels_FiltersByDeviceAndDirection()
    {
        var d1 = _repo.AddDevice("模組一", "127.0.0.1");
        var d2 = _repo.AddDevice("模組二", "127.0.0.2");
        _repo.AddChannel(d1, "DI", 0, "輸入0");
        _repo.AddChannel(d1, "DI", 1, "輸入1");
        _repo.AddChannel(d1, "DO", 0, "輸出0");
        _repo.AddChannel(d2, "DI", 0, "別台");

        var d1All = _repo.ListChannels(deviceId: d1);
        Assert.Equal(3, d1All.Count);

        var d1Di = _repo.ListChannels(deviceId: d1, direction: "DI");
        Assert.Equal(2, d1Di.Count);
        Assert.DoesNotContain(d1Di, c => c.Direction != "DI");
    }

    [Fact]
    public void SetChannelEnabled_AndDeleteChannel()
    {
        var devId = _repo.AddDevice("測試", "127.0.0.1");
        var chId = _repo.AddChannel(devId, "DI", 2, "輸入2");

        _repo.SetChannelEnabled(chId, enabled: false);
        Assert.False(_repo.GetChannel(chId)!.Enabled);
        Assert.Empty(_repo.ListChannels(deviceId: devId, enabledOnly: true));

        _repo.DeleteChannel(chId);
        Assert.Null(_repo.GetChannel(chId));
    }

    [Fact]
    public void AddChannel_DuplicateIndex_Throws()
    {
        var devId = _repo.AddDevice("測試", "127.0.0.1");
        _repo.AddChannel(devId, "DI", 0, "第一");

        Assert.ThrowsAny<Exception>(() => _repo.AddChannel(devId, "DI", 0, "重複"));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}