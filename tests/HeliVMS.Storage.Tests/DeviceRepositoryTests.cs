using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>§7 devices 表（M25：Get 回帶帳密供 ONVIF 直連）。</summary>
public class DeviceRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly DeviceRepository _repo;

    public DeviceRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-test-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new DeviceRepository(_store);
    }

    [Fact]
    public void Get_ReturnsCredentials_ForOnvifDirectConnection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var id = _repo.Add("庭院一號", "192.168.1.50", 8000, "admin", "secret-pw", "MyCam");

        var record = _repo.Get(id);

        Assert.NotNull(record);
        Assert.Equal("庭院一號", record.Name);
        Assert.Equal("192.168.1.50", record.Ip);
        Assert.Equal(8000, record.Port);
        Assert.Equal("admin", record.Username);
        Assert.NotNull(record.PasswordEncrypted);
        Assert.Equal("secret-pw", DeviceRepository.Unprotect(record.PasswordEncrypted));
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