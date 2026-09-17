namespace HeliVMS.Storage.Tests;

public class AuthServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AuthService _auth;
    private readonly SettingsRepository _settings;
    private readonly UserRepository _users;

    public AuthServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-auth-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _auth = new AuthService(_store);
        _settings = new SettingsRepository(_store);
        _users = new UserRepository(_store);
    }

    [Fact]
    public void IsAuthEnabled_DefaultsToFalse()
    {
        Assert.False(_auth.IsAuthEnabled);
    }

    [Fact]
    public void IsAuthEnabled_ReflectsSetting()
    {
        _settings.Set("auth.enabled", "1");
        Assert.True(_auth.IsAuthEnabled);

        _settings.Set("auth.enabled", "0");
        Assert.False(_auth.IsAuthEnabled);
    }

    [Fact]
    public void Policy_Defaults_ThresholdAndMinutes()
    {
        Assert.Equal(5, _auth.LockoutThreshold);
        Assert.Equal(5, _auth.LockoutMinutes);

        _settings.Set("auth.lockout.threshold", "3");
        _settings.Set("auth.lockout.minutes", "10");
        Assert.Equal(3, _auth.LockoutThreshold);
        Assert.Equal(10, _auth.LockoutMinutes);
    }

    [Fact]
    public void Authenticate_ValidCredentials_SucceedsAndRecordsLastLogin()
    {
        var now = new DateTime(2026, 4, 4, 12, 0, 0, DateTimeKind.Utc);
        _users.CreateUser("admin", PasswordHasher.Hash("pw"), "admin", "管理員");

        var result = _auth.Authenticate("admin", "pw", now);

        Assert.True(result.Succeeded);
        Assert.Equal("admin", result.Role);
        Assert.Equal("管理員", result.DisplayName);
        Assert.Equal(SqliteStore.Iso(now), _users.GetByUsername("admin")!.LastLogin);
    }

    [Fact]
    public void Authenticate_WrongPassword_FailsAndIncrementsCounter()
    {
        _users.CreateUser("bob", PasswordHasher.Hash("good"), "viewer");

        var result = _auth.Authenticate("bob", "bad", null);

        Assert.False(result.Succeeded);
        Assert.Contains("密碼錯誤", result.Error);
        Assert.Equal(1, _users.GetByUsername("bob")!.FailedLogins);
    }

    [Fact]
    public void Authenticate_UnknownUser_Fails()
    {
        var result = _auth.Authenticate("ghost", "x", null);

        Assert.False(result.Succeeded);
        Assert.Contains("錯誤", result.Error);
        Assert.Equal("viewer", result.Role);
    }

    [Fact]
    public void Authenticate_RepeatedFailures_LocksAccount()
    {
        var now = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        _settings.Set("auth.lockout.threshold", "3");
        _settings.Set("auth.lockout.minutes", "5");
        _users.CreateUser("carol", PasswordHasher.Hash("pw"), "viewer");

        AuthResult? result = null;
        for (var i = 0; i < 3; i++)
        {
            result = _auth.Authenticate("carol", "bad", now);
        }

        Assert.False(result!.Succeeded);
        Assert.Contains("已鎖定", result.Error);
        Assert.True(_users.IsLocked(_users.GetByUsername("carol")!.Id, now.AddMinutes(1)));
    }

    [Fact]
    public void Authenticate_WhileLocked_Fails()
    {
        var now = new DateTime(2026, 6, 6, 0, 0, 0, DateTimeKind.Utc);
        _settings.Set("auth.lockout.threshold", "2");
        _users.CreateUser("dave", PasswordHasher.Hash("pw"), "viewer");
        var daveId = _users.GetByUsername("dave")!.Id;
        _users.RecordFailedLogin(daveId, threshold: 2, lockoutMinutes: 30, now);
        _users.RecordFailedLogin(daveId, threshold: 2, lockoutMinutes: 30, now);

        var result = _auth.Authenticate("dave", "pw", now.AddMinutes(5));

        Assert.False(result.Succeeded);
        Assert.Contains("鎖定", result.Error);
        Assert.Equal(2, _users.GetByUsername("dave")!.FailedLogins);
    }

    [Fact]
    public void Authenticate_AfterLockExpires_AllowsRetry()
    {
        var now = new DateTime(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc);
        _settings.Set("auth.lockout.threshold", "2");
        _users.CreateUser("erin", PasswordHasher.Hash("pw"), "viewer");
        var erinId = _users.GetByUsername("erin")!.Id;
        _users.RecordFailedLogin(erinId, threshold: 2, lockoutMinutes: 30, now);
        _users.RecordFailedLogin(erinId, threshold: 2, lockoutMinutes: 30, now);

        var result = _auth.Authenticate("erin", "pw", now.AddMinutes(31));

        Assert.True(result.Succeeded);
        Assert.Equal(0, _users.GetByUsername("erin")!.FailedLogins);
    }

    [Fact]
    public void Authenticate_DisabledUser_Fails()
    {
        var id = _users.CreateUser("frank", PasswordHasher.Hash("pw"), "viewer");
        _users.SetEnabled(id, enabled: false);

        var result = _auth.Authenticate("frank", "pw", null);

        Assert.False(result.Succeeded);
        Assert.Contains("停用", result.Error);
    }

    [Fact]
    public void Authenticate_SuccessResetsPreviousFailures()
    {
        _users.CreateUser("grace", PasswordHasher.Hash("pw"), "viewer");
        _users.RecordFailedLogin(_users.GetByUsername("grace")!.Id, threshold: 5, lockoutMinutes: 5);

        var result = _auth.Authenticate("grace", "pw", null);

        Assert.True(result.Succeeded);
        var user = _users.GetByUsername("grace")!;
        Assert.Equal(0, user.FailedLogins);
        Assert.Null(user.LockedUntil);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
            // 清理失敗可忽略
        }
    }
}