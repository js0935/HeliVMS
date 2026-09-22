namespace HeliVMS.Storage.Tests;

/// <summary>M110：稽核掛載點——登入/參數變更/匯出/共享/法務保留寫入 audit_log。</summary>
public class AuditMountTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    public AuditMountTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-mount-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _audit = new AuditLogRepository(_store);
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
    public void Auth_LoginOk_RecordsAudit()
    {
        var users = new UserRepository(_store);
        users.CreateUser("alice", PasswordHasher.Hash("pw1"), "admin");
        var auth = new AuthService(_store);
        var t0 = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc);

        var result = auth.Authenticate("alice", "pw1", t0);

        Assert.True(result.Succeeded);
        var row = Assert.Single(_audit.List(new AuditLogQuery { Category = AuditCategories.Auth }));
        Assert.Equal("alice", row.Actor);
        Assert.Equal("login.ok", row.Action);
        Assert.Equal(t0, row.OccurredAtUtc);
        Assert.Contains("role=admin", row.Detail);
    }

    [Fact]
    public void Auth_LoginFail_RecordsAudit()
    {
        var users = new UserRepository(_store);
        users.CreateUser("bob", PasswordHasher.Hash("pw1"), "viewer");
        var auth = new AuthService(_store);
        var t0 = new DateTime(2026, 9, 22, 8, 5, 0, DateTimeKind.Utc);

        var result = auth.Authenticate("bob", "wrong", t0);

        Assert.False(result.Succeeded);
        var row = Assert.Single(_audit.List(new AuditLogQuery { Action = "login.fail" }));
        Assert.Equal("bob", row.Actor);
        Assert.Equal(t0, row.OccurredAtUtc);
        Assert.Contains("密碼錯誤", row.Detail);
    }

    [Fact]
    public void Auth_UnknownUser_RecordsAuditWithoutTarget()
    {
        var auth = new AuthService(_store);
        var t0 = new DateTime(2026, 9, 22, 8, 10, 0, DateTimeKind.Utc);

        auth.Authenticate("nobody", "x", t0);

        var row = Assert.Single(_audit.List(new AuditLogQuery { Action = "login.fail" }));
        Assert.Equal("nobody", row.Actor);
        Assert.Null(row.TargetId);
        Assert.Equal("使用者不存在", row.Detail);
    }

    [Fact]
    public void Auth_Disabled_RecordsAudit()
    {
        var users = new UserRepository(_store);
        users.CreateUser("c", PasswordHasher.Hash("pw"), "viewer");
        var auth = new AuthService(_store);
        users.SetEnabled(users.GetByUsername("c")!.Id, false);

        auth.Authenticate("c", "pw", new DateTime(2026, 9, 22, 8, 15, 0, DateTimeKind.Utc));

        var row = Assert.Single(_audit.List(new AuditLogQuery { Action = "login.fail" }));
        Assert.Equal("帳號停用", row.Detail);
    }

    [Fact]
    public void Settings_Set_RecordsAudit()
    {
        var settings = new SettingsRepository(_store);

        settings.Set("rec.buffer.sec", "30");

        var row = Assert.Single(_audit.List(new AuditLogQuery { Action = "settings.set" }));
        Assert.Equal(AuditCategories.Config, row.Category);
        Assert.Equal("settings", row.TargetType);
        Assert.Equal("rec.buffer.sec", row.Detail);
        Assert.Equal("system", row.Actor);
    }

    [Fact]
    public void Export_EnqueueAndDone_RecordsAudit()
    {
        var exports = new ExportJobRepository(_store);
        new ChannelRepository(_store).EnsureSeedChannels();
        var from = new DateTime(2026, 9, 22, 1, 0, 0, DateTimeKind.Utc);
        var to = from.AddMinutes(5);

        var id = exports.Enqueue(2, "main", from, to);
        exports.SetResult(id, "C:\\out\\a.mp4", "abc123", 1024, to);

        var enqueued = _audit.List(new AuditLogQuery { Action = "export.enqueue" });
        var done = _audit.List(new AuditLogQuery { Action = "export.done" });
        var enq = Assert.Single(enqueued);
        Assert.Equal(id, enq.TargetId);
        Assert.Contains("ch=2", enq.Detail);
        Assert.Equal(AuditCategories.Export, enq.Category);
        var d = Assert.Single(done);
        Assert.Equal("sha256=abc123", d.Detail);
    }

    [Fact]
    public void Share_CreateAndRevoke_RecordsAudit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"helivms-mount-share-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "clip.mp4");
        File.WriteAllText(file, "x");
        try
        {
            var svc = new ShareLinkService(_store);
            var t0 = new DateTime(2026, 9, 22, 5, 0, 0, DateTimeKind.Utc);
            var rec = svc.Create("segment", file, new[] { root }, t0, createdBy: "admin");
            svc.Revoke(rec.Id);

            var created = Assert.Single(_audit.List(new AuditLogQuery { Action = "share.create" }));
            Assert.Equal("admin", created.Actor);
            Assert.Equal("kind=segment", created.Detail);
            Assert.Equal(t0, created.OccurredAtUtc);
            var revoked = Assert.Single(_audit.List(new AuditLogQuery { Action = "share.revoke" }));
            Assert.Equal(rec.Id, revoked.TargetId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegalHold_AddAndRevoke_RecordsAudit()
    {
        var holds = new LegalHoldRepository(_store);
        var from = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(3);
        var id = holds.Add(5, from, to, "偵查中", "檢察官", from);

        Assert.True(holds.Revoke(id, "admin", "結案", to));

        var created = Assert.Single(_audit.List(new AuditLogQuery { Action = "legal_hold.create" }));
        Assert.Equal("檢察官", created.Actor);
        Assert.Equal(id, created.TargetId);
        Assert.Equal(AuditCategories.LegalHold, created.Category);
        var revoked = Assert.Single(_audit.List(new AuditLogQuery { Action = "legal_hold.revoke" }));
        Assert.Equal("admin", revoked.Actor);
        Assert.Equal("結案", revoked.Detail);
    }
}