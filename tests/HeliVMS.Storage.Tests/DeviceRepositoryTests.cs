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

    [Fact]
    public void FindByIp_ReturnsExistingDevice_WithCredentials()
    {
        var id = _repo.Add("CamIp", "10.0.0.9", 80, "admin", "pw-ip", "onvif");

        var found = _repo.FindByIp("10.0.0.9");

        Assert.NotNull(found);
        Assert.Equal(id, found.Id);
        Assert.Equal("admin", found.Username);
        Assert.NotNull(found.PasswordEncrypted);
        Assert.Null(_repo.FindByIp("10.0.0.999"));
    }

    [Fact]
    public void GetRtspCredentials_DecryptsPassword_ForStreamConnection()
    {
        var id = _repo.Add("CamRtsp", "10.0.0.10", 80, "admin", "rtsp-pw", "onvif");

        var (username, password) = _repo.GetRtspCredentials(id);

        Assert.Equal("admin", username);
        Assert.Equal("rtsp-pw", password);
    }

    [Fact]
    public void GetRtspCredentials_UnknownDevice_ReturnsEmpty_WithoutThrowing()
    {
        var (username, password) = _repo.GetRtspCredentials(9999);

        Assert.Equal(string.Empty, username);
        Assert.Equal(string.Empty, password);
    }

    [Fact]
    public void GetRtspCredentials_NoUsername_ReturnsEmptyPassword()
    {
        var id = _repo.Add("CamAnon", "10.0.0.11", 80, string.Empty, string.Empty, "onvif");

        var (username, password) = _repo.GetRtspCredentials(id);

        Assert.Equal(string.Empty, username);
        Assert.Equal(string.Empty, password);
    }

    [Fact]
    public void GetRtspCredentials_UndecryptableValue_ReturnsStoredValueInsteadOfThrowing()
    {
        // 舊資料可能為明文或由其他機器／帳戶加密，解密失敗時應原樣回傳，不讓拉流中斷。
        var id = _repo.Add("CamLegacy", "10.0.0.12", 80, "admin", "pw", "onvif");
        _store.Execute(
            "UPDATE devices SET password_encrypted = $p WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$p", "plaintext-not-dpapi");
                cmd.Parameters.AddWithValue("$id", id);
            });

        var (username, password) = _repo.GetRtspCredentials(id);

        Assert.Equal("admin", username);
        Assert.Equal("plaintext-not-dpapi", password);
    }

    [Fact]
    public void SetRtspCredentials_ReplacesStoredSecret_AndKeepsItDecryptable()
    {
        // 重新加入同一台攝影機時要能就地修正手誤／缺漏的憑證，否則會沿用壞掉的密碼。
        var id = _repo.Add("cam", "10.0.0.9", 80, "root", "old-pass", "onvif");

        Assert.True(_repo.SetRtspCredentials(id, "admin", "new-pass"));

        var (username, password) = _repo.GetRtspCredentials(id);
        Assert.Equal("admin", username);
        Assert.Equal("new-pass", password);
    }

    [Fact]
    public void SetRtspCredentials_MissingDevice_ReturnsFalse()
    {
        Assert.False(_repo.SetRtspCredentials(4242, "root", "pass"));
    }

    [Fact]
    public void SetSdUrl_RoundTripsThroughGetAndList_AndClears()
    {
        // M94 邊緣補抓需要設備端 SD 串流位址；此欄位原不存在，補抓 resolver 因而無從取值。
        var id = _repo.Add("cam", "10.0.0.20", 80, "root", "pw", "onvif");

        Assert.True(_repo.SetSdUrl(id, "rtsp://10.0.0.20:554/streaming/playback", "tester"));

        Assert.Equal("rtsp://10.0.0.20:554/streaming/playback", _repo.Get(id)!.SdUrl);
        Assert.Equal("rtsp://10.0.0.20:554/streaming/playback", Assert.Single(_repo.List(), d => d.Id == id).SdUrl);

        Assert.True(_repo.SetSdUrl(id, "   ", "tester"));
        Assert.Null(_repo.Get(id)!.SdUrl);
    }

    [Fact]
    public void SetSdUrl_RecordsAudit()
    {
        var id = _repo.Add("cam", "10.0.0.21", 80, "root", "pw", "onvif");

        _repo.SetSdUrl(id, "rtsp://x/playback", "tester");

        var logs = _audit.List(new AuditLogQuery { Category = AuditCategories.Config, Action = "device.sd_url" });
        Assert.Single(logs);
        Assert.Equal("device", logs[0].TargetType);
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