using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage.Tests;

public class MapRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly MapRepository _repo;

    public MapRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-map-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new MapRepository(_store);
    }

    [Fact]
    public void AddMap_PersistsAllFields()
    {
        var id = _repo.AddMap("一樓平面圖", @"C:\data\maps\floor1.png", width: 1920, height: 1080, sortOrder: 2);

        var map = _repo.GetMap(id);
        Assert.NotNull(map);
        Assert.Equal("一樓平面圖", map!.Name);
        Assert.Equal("plan", map.Type);
        Assert.Equal(@"C:\data\maps\floor1.png", map.ImagePath);
        Assert.Equal(1920, map.Width);
        Assert.Equal(1080, map.Height);
        Assert.Equal(2, map.SortOrder);
        Assert.True(map.Enabled);
    }

    [Fact]
    public void ListMaps_OrdersBySortOrderThenId()
    {
        var a = _repo.AddMap("二樓", "b.png", 10, 10, sortOrder: 2);
        var b = _repo.AddMap("一樓", "a.png", 10, 10, sortOrder: 1);

        var maps = _repo.ListMaps();
        Assert.Equal(new[] { b, a }, maps.Select(m => m.Id));
    }

    [Fact]
    public void GetMap_MissingReturnsNull()
    {
        Assert.Null(_repo.GetMap(999));
    }

    [Fact]
    public void SetMapEnabled_UpdatesFlag()
    {
        var id = _repo.AddMap("測試", "t.png", 10, 10);

        _repo.SetMapEnabled(id, enabled: false);

        Assert.False(_repo.GetMap(id)!.Enabled);
    }

    [Fact]
    public void DeleteMap_CascadesDevices()
    {
        var mapId = _repo.AddMap("一樓", "1.png", 800, 600);
        _repo.AddDevice(mapId, "camera", 3, x: 0.1, y: 0.2);
        _repo.AddDevice(mapId, "io", 5, x: 0.8, y: 0.9);

        _repo.DeleteMap(mapId);

        Assert.Null(_repo.GetMap(mapId));
        Assert.Empty(_repo.ListDevices(mapId));
    }

    [Fact]
    public void AddDevice_PersistsAllFields()
    {
        var mapId = _repo.AddMap("一樓", "1.png", 800, 600);
        var devId = _repo.AddDevice(
            mapId, "camera", 42, x: 0.25, y: 0.75, angle: 30, fovDeg: 120, fovDepth: 4);

        var dev = _repo.GetDevice(devId);
        Assert.NotNull(dev);
        Assert.Equal(mapId, dev!.MapId);
        Assert.Equal("camera", dev.DeviceType);
        Assert.Equal(42, dev.ChannelId);
        Assert.Equal(0.25, dev.X);
        Assert.Equal(0.75, dev.Y);
        Assert.Equal(30, dev.Angle);
        Assert.Equal(120, dev.FovDeg);
        Assert.Equal(4, dev.FovDepth);
        Assert.True(dev.Enabled);
    }

    [Fact]
    public void SetDevicePosition_UpdatesCoordinates()
    {
        var mapId = _repo.AddMap("一樓", "1.png", 800, 600);
        var devId = _repo.AddDevice(mapId, "io", 2, x: 0.1, y: 0.1);

        _repo.SetDevicePosition(devId, x: 0.62, y: 0.38);

        var dev = _repo.GetDevice(devId);
        Assert.Equal(0.62, dev!.X);
        Assert.Equal(0.38, dev.Y);
    }

    [Fact]
    public void SetDeviceEnabled_UpdatesFlag()
    {
        var mapId = _repo.AddMap("一樓", "1.png", 800, 600);
        var devId = _repo.AddDevice(mapId, "camera", 9);

        _repo.SetDeviceEnabled(devId, enabled: false);

        Assert.False(_repo.GetDevice(devId)!.Enabled);
    }

    [Fact]
    public void DeleteDevice_RemovesPin()
    {
        var mapId = _repo.AddMap("一樓", "1.png", 800, 600);
        var devId = _repo.AddDevice(mapId, "camera", 9);

        _repo.DeleteDevice(devId);

        Assert.Null(_repo.GetDevice(devId));
        Assert.Empty(_repo.ListDevices(mapId));
    }

    [Fact]
    public void AddDevice_DuplicateTypeAndChannelThrows()
    {
        var mapId = _repo.AddMap("一樓", "1.png", 800, 600);
        _repo.AddDevice(mapId, "camera", 9);

        Assert.Throws<SqliteException>(() => _repo.AddDevice(mapId, "camera", 9));
        Assert.Throws<SqliteException>(() => _repo.AddDevice(mapId, "camera", 9, x: 0.9));
    }

    [Fact]
    public void FindMapByChannel_ReturnsSmallestAllowedMap()
    {
        var mapA = _repo.AddMap("一樓", "1.png", 800, 600, sortOrder: 2);
        var mapB = _repo.AddMap("二樓", "2.png", 800, 600, sortOrder: 1);
        var camB = _repo.AddDevice(mapB, "camera", 7, x: 0.5, y: 0.5);
        _repo.AddDevice(mapA, "camera", 7, x: 0.5, y: 0.5);
        _repo.AddDevice(mapB, "io", 3, x: 0.2, y: 0.2);

        Assert.Equal(mapB, _repo.FindMapByChannel(7, "camera"));
        Assert.Equal(mapB, _repo.FindMapByChannel(3, "io"));

        _repo.SetDeviceEnabled(camB, enabled: false);
        Assert.Equal(mapA, _repo.FindMapByChannel(7, "camera"));
    }

    [Fact]
    public void FindMapByChannel_NoMatchReturnsNull()
    {
        var mapId = _repo.AddMap("一樓", "1.png", 800, 600);
        _repo.AddDevice(mapId, "camera", 1);

        Assert.Null(_repo.FindMapByChannel(1, "io"));
        Assert.Null(_repo.FindMapByChannel(99, "camera"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}