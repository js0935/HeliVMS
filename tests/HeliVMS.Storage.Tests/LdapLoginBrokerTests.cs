namespace HeliVMS.Storage.Tests;

public class LdapLoginBrokerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private static DateTime T() => new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeBinder : ILdapBinder
    {
        public bool Fail { get; set; }
        public List<string>? Groups { get; set; }
        public int BindCalls { get; private set; }

        public IReadOnlyList<string>? Bind(LdapSettings settings, string username, string password)
        {
            BindCalls++;
            return Fail ? null : Groups;
        }
    }

    public LdapLoginBrokerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-ldaplogin-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        var settings = new SettingsRepository(_store);
        settings.Set("auth.lockout.threshold", "2");
        settings.Set("auth.lockout.minutes", "5");
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static AuthProviderRecord Provider(params string[] adminGroups)
    {
        var settings = new LdapSettings
        {
            Host = "dc.example.com",
            BaseDn = "dc=example,dc=com",
            AdminGroups = adminGroups,
            DefaultRole = "viewer",
        };
        return new AuthProviderRecord(0, "corp", AuthProviderRepository.KindLdap, true, settings.ToJson(), "");
    }

    [Fact]
    public void Login_Success_CreatesMirrorAndActiveSession()
    {
        var broker = new LdapLoginBroker(_store);
        var binder = new FakeBinder { Groups = new List<string> { "staff" } };

        var result = broker.Login(Provider(), "alice", "pw", binder, Ttl, T());

        Assert.True(result.Succeeded);
        Assert.Equal("viewer", result.Role);
        Assert.NotEmpty(result.SessionId!);
        var sessions = new LoginSessionRepository(_store);
        Assert.True(sessions.IsActive(result.SessionId!, T()));
        var user = new UserRepository(_store).GetByUsername("alice");
        Assert.NotNull(user);
        Assert.Equal("viewer", user!.Role);
        Assert.Equal(LdapLoginBroker.NoLocalPasswordHash, user.PasswordHash); // 鏡像不可本機密碼登入
    }

    [Fact]
    public void Login_AdminGroup_PromotesToAdmin()
    {
        var broker = new LdapLoginBroker(_store);
        var binder = new FakeBinder { Groups = new List<string> { "staff", "HeliVMS-Admins" } };

        var result = broker.Login(Provider("HeliVMS-Admins"), "bob", "pw", binder, Ttl, T());

        Assert.True(result.Succeeded);
        Assert.Equal("admin", result.Role);
    }

    [Fact]
    public void Login_ReusesMirror_UpdatesRoleAndResetsLock()
    {
        var users = new UserRepository(_store);
        var id = users.CreateUser("carol", LdapLoginBroker.NoLocalPasswordHash, "viewer", "carol");
        users.RecordFailedLogin(id, 2, 5, T()); // failed_logins=1
        var broker = new LdapLoginBroker(_store);
        var binder = new FakeBinder { Groups = new List<string> { "Carol-Admins" } };

        var result = broker.Login(Provider("Carol-Admins"), "carol", "pw", binder, Ttl, T());

        Assert.True(result.Succeeded);
        Assert.Equal("admin", result.Role);
        var updated = users.GetByUsername("carol")!;
        Assert.Equal("admin", updated.Role);
        Assert.Equal(0, updated.FailedLogins);
    }

    [Fact]
    public void Login_BindFail_NoLocalMirror_DoesNotLock()
    {
        var broker = new LdapLoginBroker(_store);
        var binder = new FakeBinder { Fail = true };

        var result = broker.Login(Provider(), "ghost", "pw", binder, Ttl, T());

        Assert.False(result.Succeeded);
        Assert.Contains("LDAP 驗證失敗", result.Error);
        Assert.Null(new UserRepository(_store).GetByUsername("ghost"));
    }

    [Fact]
    public void Login_BindFail_CountsToLockout()
    {
        var users = new UserRepository(_store);
        users.CreateUser("dave", LdapLoginBroker.NoLocalPasswordHash, "viewer", "dave");
        var broker = new LdapLoginBroker(_store);
        var binder = new FakeBinder { Fail = true };

        var first = broker.Login(Provider(), "dave", "bad1", binder, Ttl, T());
        Assert.False(first.Succeeded);
        Assert.DoesNotContain("鎖定", first.Error);          // 剩 1 次

        var second = broker.Login(Provider(), "dave", "bad2", binder, Ttl, T());
        Assert.False(second.Succeeded);
        Assert.Contains("鎖定", second.Error);
        Assert.True(users.IsLocked(users.GetByUsername("dave")!.Id, T()));
    }

    [Fact]
    public void Login_DisabledMirror_RejectedBeforeBind()
    {
        var users = new UserRepository(_store);
        var id = users.CreateUser("erin", LdapLoginBroker.NoLocalPasswordHash, "viewer", "erin");
        users.SetEnabled(id, false);
        var broker = new LdapLoginBroker(_store);
        var binder = new FakeBinder();

        var result = broker.Login(Provider(), "erin", "pw", binder, Ttl, T());

        Assert.False(result.Succeeded);
        Assert.Contains("停用", result.Error);
        Assert.Equal(0, binder.BindCalls);                     // 連綁定都不做
    }

    [Fact]
    public void Login_NonLdapProvider_Rejected()
    {
        var broker = new LdapLoginBroker(_store);
        var provider = new AuthProviderRecord(0, "sso", AuthProviderRepository.KindOidc, true, "{}", "");

        var result = broker.Login(provider, "x", "pw", new FakeBinder(), Ttl, T());

        Assert.False(result.Succeeded);
        Assert.Contains("不是 LDAP", result.Error);
    }

    [Fact]
    public void SessionRepo_ActiveUntilExpiry()
    {
        var sessions = new LoginSessionRepository(_store);
        var id = sessions.Create(1, "alice", "viewer", "ldap", TimeSpan.FromSeconds(10), T());

        Assert.True(sessions.IsActive(id, T()));
        Assert.False(sessions.IsActive(id, T().AddSeconds(11)));
        Assert.Null(sessions.Get("nope"));
    }

    [Fact]
    public void SessionRepo_Revoke_Invalidates()
    {
        var sessions = new LoginSessionRepository(_store);
        var id = sessions.Create(1, "alice", "viewer", "ldap", TimeSpan.FromMinutes(10), T());

        sessions.Revoke(id, T().AddSeconds(1));
        Assert.False(sessions.IsActive(id, T().AddSeconds(2)));
        Assert.NotNull(sessions.Get(id)!.RevokedAt);
    }

    [Fact]
    public void SessionRepo_PurgeExpired_RemovesOnlyExpired()
    {
        var sessions = new LoginSessionRepository(_store);
        var keep = sessions.Create(1, "alice", "viewer", "ldap", TimeSpan.FromMinutes(10), T());
        var drop = sessions.Create(2, "bob", "viewer", "ldap", TimeSpan.FromSeconds(5), T());

        var removed = sessions.PurgeExpired(T().AddMinutes(1));

        Assert.Equal(1, removed);
        Assert.True(sessions.IsActive(keep, T().AddMinutes(1)));
        Assert.False(sessions.IsActive(drop, T().AddMinutes(1)));
    }
}