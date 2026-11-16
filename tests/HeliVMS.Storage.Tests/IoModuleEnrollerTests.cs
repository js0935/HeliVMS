using HeliVMS.Shared;
using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 「新增一顆 ADAM-6250」＝一次呼叫就要把模組與它的 8 DI／8 DO 全部建好。
/// </summary>
/// <remarks>
/// 現場需求是「我加一顆 ADAM-6250，要在 IO 裡看到 DI/DO 接點、要能掛規則、要能放上地圖」。
/// 模組（<c>io_devices</c>）與通道（<c>io_channels</c>）是兩張表，只寫一張的症狀是
/// 「模組加好了，接點一條都沒有」——兩邊都寫入成功，所以沒有任何錯誤訊息。
/// </remarks>
public sealed class IoModuleEnrollerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-iomod-{Guid.NewGuid():N}.db");
    private readonly SqliteStore _store;
    private readonly IoRepository _io;

    public IoModuleEnrollerTests()
    {
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _io = new IoRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void 新增ADAM6250會建立八個DI與八個DO()
    {
        var result = IoModuleEnroller.Enroll(_io, IoModuleProfile.Adam6250, "192.168.0.50");

        Assert.True(result.DeviceCreated);
        Assert.Equal(8, result.DiCount);
        Assert.Equal(8, result.DoCount);
        Assert.Equal(16, result.ChannelsCreated);

        var channels = _io.ListChannels(deviceId: result.DeviceId);
        Assert.Equal(["DI 1", "DI 2", "DI 3", "DI 4", "DI 5", "DI 6", "DI 7", "DI 8"],
            channels.Where(c => c.Direction == "DI").OrderBy(c => c.IoIndex).Select(c => c.Name));
        Assert.Equal(["DO 1", "DO 8"],
            new[] { channels.Where(c => c.Direction == "DO").OrderBy(c => c.IoIndex).First().Name,
                    channels.Where(c => c.Direction == "DO").OrderBy(c => c.IoIndex).Last().Name });
    }

    [Fact]
    public void 連線參數沿用機型預設值()
    {
        var result = IoModuleEnroller.Enroll(_io, IoModuleProfile.Adam6250, " 192.168.0.51 ");
        var device = _io.GetDevice(result.DeviceId)!;

        Assert.Equal("ADAM-6250", device.Name);
        Assert.Equal("192.168.0.51", device.Host);   // 前後空白不該變成位址的一部分
        Assert.Equal(502, device.Port);
        Assert.Equal(1, device.UnitId);
        Assert.Equal(500, device.PollMs);
    }

    [Fact]
    public void 同一個位址再新增一次只補缺的通道且不動既有設定()
    {
        var first = IoModuleEnroller.Enroll(_io, IoModuleProfile.Adam6250, "192.168.0.52");
        var di = _io.ListChannels(deviceId: first.DeviceId, direction: "DI");
        var di3 = di.Single(c => c.IoIndex == 2);
        var di5 = di.Single(c => c.IoIndex == 4);

        // 操作員把第 3 點改名、停用第 5 點，然後發現自己其實有 16 個 DI 而重新新增。
        _store.Execute("UPDATE io_channels SET name = $n WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", "大門磁簧");
                cmd.Parameters.AddWithValue("$id", di3.Id);
            });
        _io.SetChannelEnabled(di5.Id, enabled: false);

        var again = IoModuleEnroller.Enroll(_io, IoModuleProfile.Adam6250, "192.168.0.52", diCount: 16);

        Assert.Equal(
            ["192.168.0.52:502"],
            _io.ListDevices().Select(d => $"{d.Host}:{d.Port}").ToArray());
        Assert.False(again.DeviceCreated);           // 同一台模組，不是第二台
        Assert.Equal(first.DeviceId, again.DeviceId);
        Assert.Equal(8, again.ChannelsCreated);      // 只補了 8 個新的 DI
        Assert.Equal(16, again.DiCount);
        Assert.Equal(8, again.DoCount);

        var after = _io.ListChannels(deviceId: first.DeviceId, direction: "DI");
        Assert.Equal("大門磁簧", after.Single(c => c.IoIndex == 2).Name);   // 名字不被重建蓋掉
        Assert.False(after.Single(c => c.IoIndex == 4).Enabled);            // 停用狀態保留
    }

    [Fact]
    public void 點數可以依表單覆寫()
    {
        var result = IoModuleEnroller.Enroll(_io, IoModuleProfile.Adam6250, "192.168.0.53", diCount: 4, doCount: 2);

        Assert.Equal(4, result.DiCount);
        Assert.Equal(2, result.DoCount);
    }

    [Fact]
    public void 認不得的機型退回一般Modbus而不是失敗()
    {
        // 舊資料或手動改過的 kind 不該讓模組打不開：預設 8/8 至少是能用的起點。
        var profile = IoModuleProfile.FromKind("adam-9999");

        Assert.Equal(IoModuleProfile.GenericModbus.Kind, profile.Kind);

        var result = IoModuleEnroller.Enroll(_io, profile, "192.168.0.54");
        Assert.Equal(8, result.DiCount);
        Assert.Equal(8, result.DoCount);
    }

    [Fact]
    public void 點數被夾在上限之內()
    {
        // 表單填 10000 不該讓伺服器嘗試讀 10000 個位元（Modbus 單次讀取上限 2000，而且會把 UI 卡住）。
        var profile = IoModuleProfile.Adam6250;

        Assert.Equal(IoModuleProfile.MaxChannelCount, profile.ResolveDiCount(10_000));
        Assert.Equal(profile.DefaultDiCount, profile.ResolveDiCount(0));
        Assert.Equal(profile.DefaultDiCount, profile.ResolveDiCount(null));
    }
}
