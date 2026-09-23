using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>§7 devices 表（M25：Get 回帶帳密供 ONVIF 直連）。</summary>
public class DeviceRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;
    private readonly DeviceRepository _repo;

    public DeviceRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-test-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _audit = new AuditLogRepository(_store);
        _repo = new DeviceRepository(_store, _audit);
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

    [Fact]
    public void Update_ChangesFields_AndRecordsAudit()
    {
        var id = _repo.Add("CamA", "10.0.0.1", 8000, "admin", "pw", "VendorA", "tester");

        var ok = _repo.Update(id, "CamA-Renamed", "10.0.0.2", 554, "VendorB", false, "tester");

        Assert.True(ok);
        var rec = _repo.Get(id);
        Assert.NotNull(rec);
        Assert.Equal("CamA-Renamed", rec.Name);
        Assert.Equal("10.0.0.2", rec.Ip);
        Assert.Equal(554, rec.Port);
        Assert.False(rec.Enabled);

        var logs = _audit.List(new AuditLogQuery { Category = AuditCategories.Config, Action = "device.update" });
        Assert.Single(logs);
        Assert.Equal("device", logs[0].TargetType);
        Assert.Equal(id, logs[0].TargetId);
        Assert.Equal("tester", logs[0].Actor);
    }

    [Fact]
    public void Add_Delete_RecordAuditTrail()
    {
        var id = _repo.Add("CamB", "10.0.0.3", 8000, "admin", "pw", "VendorC", "tester");
        _repo.Delete(id, "tester");

        Assert.Null(_repo.Get(id));
        var adds = _audit.List(new AuditLogQuery { Action = "device.add" });
        var dels = _audit.List(new AuditLogQuery { Action = "device.delete" });
        Assert.Single(adds);
        Assert.Single(dels);
        Assert.Equal(id, adds[0].TargetId);
        Assert.Equal(id, dels[0].TargetId);
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