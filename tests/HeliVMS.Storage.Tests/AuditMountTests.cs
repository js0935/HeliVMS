using HeliVMS.Shared.Models;

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

    [Fact]
    public void Channel_AddUpdateDelete_RecordsAudit()
    {
        var channels = new ChannelRepository(_store);

        var id = channels.Add("前門", "rtsp://cam/main", actor: "admin");
        channels.Update(
            new ChannelInfo
            {
                Id = id,
                Name = "前門（整併）",
                MainStreamUrl = "rtsp://cam/main",
                Enabled = false,
            },
            actor: "admin");
        channels.Delete(id, actor: "admin");

        var add = Assert.Single(_audit.List(new AuditLogQuery { Action = "channel.add" }));
        Assert.Equal(AuditCategories.Config, add.Category);
        Assert.Equal("channel", add.TargetType);
        Assert.Equal(id, add.TargetId);
        Assert.Equal("admin", add.Actor);
        Assert.Equal("前門", add.Detail);

        var update = Assert.Single(_audit.List(new AuditLogQuery { Action = "channel.update" }));
        Assert.Equal(id, update.TargetId);
        Assert.Equal("admin", update.Actor);
        Assert.Contains("前門（整併）", update.Detail);
        Assert.Contains("enabled=False", update.Detail);

        var delete = Assert.Single(_audit.List(new AuditLogQuery { Action = "channel.delete" }));
        Assert.Equal(id, delete.TargetId);
        Assert.Equal("前門（整併）", delete.Detail);
    }

    [Fact]
    public void Channel_EnsureSeedChannels_DoesNotAudit()
    {
        var seeded = new ChannelRepository(_store).EnsureSeedChannels();

        Assert.Equal(2, seeded);
        Assert.Equal(0, _audit.Count(new AuditLogQuery { Category = AuditCategories.Config }));
    }

    [Fact]
    public void Account_CreateEnableRoleDelete_RecordsAudit()
    {
        var users = new UserRepository(_store);

        var id = users.CreateUser("dave", PasswordHasher.Hash("pw"), "viewer", actor: "admin");
        users.SetEnabled(id, false, actor: "admin");
        users.SetRole(id, "admin", actor: "admin");
        users.DeleteUser(id, actor: "admin");

        var create = Assert.Single(_audit.List(new AuditLogQuery { Action = "account.create" }));
        Assert.Equal(AuditCategories.Config, create.Category);
        Assert.Equal("user", create.TargetType);
        Assert.Equal(id, create.TargetId);
        Assert.Equal("admin", create.Actor);
        Assert.Equal("role=viewer", create.Detail);
        Assert.DoesNotContain("pw", create.Detail);

        var disabled = Assert.Single(_audit.List(new AuditLogQuery { Action = "account.disable" }));
        Assert.Equal(id, disabled.TargetId);

        var role = Assert.Single(_audit.List(new AuditLogQuery { Action = "account.role" }));
        Assert.Equal("role=admin", role.Detail);

        var deleted = Assert.Single(_audit.List(new AuditLogQuery { Action = "account.delete" }));
        Assert.Equal(id, deleted.TargetId);
        Assert.Equal("dave", deleted.Detail);
    }

    [Fact]
    public void Account_NoOpUpdate_DoesNotAudit()
    {
        var users = new UserRepository(_store);

        users.SetEnabled(999, false);
        users.SetRole(999, "admin");

        Assert.Equal(0, _audit.Count(new AuditLogQuery { Category = AuditCategories.Config }));
    }

    [Fact]
    public void Provider_AddEnableDelete_RecordsAudit()
    {
        var providers = new AuthProviderRepository(_store);

        var id = providers.Add("corp-ldap", AuthProviderRepository.KindLdap, "{\"host\":\"x\"}", actor: "admin");
        providers.SetEnabled(id, false, actor: "admin");
        providers.Delete(id, actor: "admin");

        var add = Assert.Single(_audit.List(new AuditLogQuery { Action = "provider.add" }));
        Assert.Equal(AuditCategories.Config, add.Category);
        Assert.Equal("auth_provider", add.TargetType);
        Assert.Equal(id, add.TargetId);
        Assert.Equal("admin", add.Actor);
        Assert.Contains("kind=ldap", add.Detail);
        Assert.DoesNotContain("{\"host\"", add.Detail);

        var disabled = Assert.Single(_audit.List(new AuditLogQuery { Action = "provider.disable" }));
        Assert.Equal(id, disabled.TargetId);

        var deleted = Assert.Single(_audit.List(new AuditLogQuery { Action = "provider.delete" }));
        Assert.Equal("corp-ldap", deleted.Detail);
    }

    [Fact]
    public void Patrol_SaveAndDelete_RecordsAudit()
    {
        var patrols = new PatrolRepository(_store);
        var t0 = new DateTime(2026, 9, 22, 9, 0, 0, DateTimeKind.Utc);

        var id = patrols.Save(
            null, "夜巡", 3, true, "22:00", "06:00", t0,
            new[] { new PatrolStepRow("大門", 5) },
            actor: "admin");
        Assert.True(patrols.Delete(id, actor: "admin"));

        var save = Assert.Single(_audit.List(new AuditLogQuery { Action = "patrol.save" }));
        Assert.Equal(AuditCategories.Config, save.Category);
        Assert.Equal("patrol", save.TargetType);
        Assert.Equal(id, save.TargetId);
        Assert.Equal("admin", save.Actor);
        Assert.Contains("name=夜巡", save.Detail);
        Assert.Contains("channel=3", save.Detail);

        var delete = Assert.Single(_audit.List(new AuditLogQuery { Action = "patrol.delete" }));
        Assert.Equal(id, delete.TargetId);
        Assert.Equal("admin", delete.Actor);
    }

    [Fact]
    public void Patrol_DeleteMissing_DoesNotAudit()
    {
        var patrols = new PatrolRepository(_store);

        Assert.False(patrols.Delete(999));

        Assert.Equal(0, _audit.Count(new AuditLogQuery { Category = AuditCategories.Config }));
    }

    [Fact]
    public void AlertRule_AddUpdateToggleDelete_RecordsAudit()
    {
        var rules = new AlertRuleRepository(_store);

        var id = rules.Add("motion", "motion", 1, null, null, actor: "admin");
        rules.Update(id, "motion2", "motion", 1, null, null, false, actor: "admin");
        rules.SetEnabled(id, true, actor: "admin");
        rules.Delete(id, actor: "admin");

        var add = Assert.Single(_audit.List(new AuditLogQuery { Action = "alert_rule.add" }));
        Assert.Equal(AuditCategories.Config, add.Category);
        Assert.Equal("alert_rule", add.TargetType);
        Assert.Equal(id, add.TargetId);
        Assert.Equal("admin", add.Actor);
        Assert.Contains("name=motion", add.Detail);

        var update = Assert.Single(_audit.List(new AuditLogQuery { Action = "alert_rule.update" }));
        Assert.Equal(id, update.TargetId);
        Assert.Contains("name=motion2", update.Detail);

        var enable = Assert.Single(_audit.List(new AuditLogQuery { Action = "alert_rule.enable" }));
        Assert.Equal(id, enable.TargetId);

        var delete = Assert.Single(_audit.List(new AuditLogQuery { Action = "alert_rule.delete" }));
        Assert.Equal(id, delete.TargetId);
    }

    [Fact]
    public void AlertRule_NoOpToggle_DoesNotAudit()
    {
        var rules = new AlertRuleRepository(_store);

        rules.SetEnabled(999, true);
        rules.Delete(999);

        Assert.Equal(0, _audit.Count(new AuditLogQuery { Category = AuditCategories.Config }));
    }

    [Fact]
    public void Io_DeviceAndChannel_RecordsAudit()
    {
        var io = new IoRepository(_store);

        var device = io.AddDevice("門禁模組", "10.0.0.9", 502, 1, 500, actor: "admin");
        io.SetDeviceEnabled(device, false, actor: "admin");
        var channel = io.AddChannel(device, "DI", 0, "大門開關", actor: "admin");
        io.SetChannelEnabled(channel, false, actor: "admin");
        io.DeleteChannel(channel, actor: "admin");
        io.DeleteDevice(device, actor: "admin");

        var deviceAdd = Assert.Single(_audit.List(new AuditLogQuery { Action = "io.device.add" }));
        Assert.Equal(AuditCategories.Config, deviceAdd.Category);
        Assert.Equal("io_device", deviceAdd.TargetType);
        Assert.Equal(device, deviceAdd.TargetId);
        Assert.Contains("10.0.0.9:502", deviceAdd.Detail);
        Assert.Single(_audit.List(new AuditLogQuery { Action = "io.device.disable" }));
        Assert.Single(_audit.List(new AuditLogQuery { Action = "io.device.delete" }));

        var channelAdd = Assert.Single(_audit.List(new AuditLogQuery { Action = "io.channel.add" }));
        Assert.Equal("io_channel", channelAdd.TargetType);
        Assert.Equal(channel, channelAdd.TargetId);
        Assert.Contains("dir=DI", channelAdd.Detail);
        Assert.Single(_audit.List(new AuditLogQuery { Action = "io.channel.disable" }));
        Assert.Single(_audit.List(new AuditLogQuery { Action = "io.channel.delete" }));
    }

    [Fact]
    public void Redaction_AddAndRemove_RecordsAudit()
    {
        var redactions = new RedactionRepository(_store);
        var t0 = new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc);

        var id = redactions.Add(
            RedactionSources.Clip, 42, 1, t0, 10, 20, 30, 40, filled: true,
            createdAtUtc: t0, actor: "admin");
        Assert.True(redactions.Remove(id, actor: "admin"));

        var add = Assert.Single(_audit.List(new AuditLogQuery { Action = "redaction.add" }));
        Assert.Equal(AuditCategories.Evidence, add.Category);
        Assert.Equal("redaction", add.TargetType);
        Assert.Equal(id, add.TargetId);
        Assert.Equal("admin", add.Actor);
        Assert.Contains("channel=1", add.Detail);
        Assert.Contains("source=clip", add.Detail);

        var remove = Assert.Single(_audit.List(new AuditLogQuery { Action = "redaction.remove" }));
        Assert.Equal(AuditCategories.Evidence, remove.Category);
        Assert.Equal(id, remove.TargetId);
    }
}