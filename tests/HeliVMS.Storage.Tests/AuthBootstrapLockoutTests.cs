namespace HeliVMS.Storage.Tests;

/// <summary>
/// 「啟用登入驗證後還有沒有人能登入」的行為保證（M42／§18.6 的鎖死防線）。
///
/// 事件背景：<c>auth.enabled=1</c> 卻沒有任何啟用中的 admin 時，桌面端啟動會卡在
/// <c>LoginWindow</c>，而沒有帳號可以通過驗證——操作者只能手動改 DB 才救得回來。
/// 這裡鎖住 <see cref="AuthService"/> 的判定語意，讓 UI／啟動流程的守衛有可信的依據。
/// </summary>
public class AuthBootstrapLockoutTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AuthService _auth;
    private readonly SettingsRepository _settings;
    private readonly UserRepository _users;

    public AuthBootstrapLockoutTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-bootstrap-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _auth = new AuthService(_store);
        _settings = new SettingsRepository(_store);
        _users = new UserRepository(_store);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch (IOException)
            {
            }
        }
    }

    private int CreateAdmin(string name = "root", bool enabled = true)
    {
        var id = _users.CreateUser(name, PasswordHasher.Hash("pw"), "admin");
        if (!enabled)
        {
            _users.SetEnabled(id, false);
        }

        return id;
    }

    [Fact]
    public void 沒有使用者時不算有可登入的管理員()
    {
        Assert.False(_auth.HasEnabledAdmin());
    }

    [Fact]
    public void 停用的管理員不算可登入()
    {
        CreateAdmin(enabled: false);

        Assert.False(_auth.HasEnabledAdmin());
    }

    [Fact]
    public void viewer不算可登入的管理員()
    {
        _users.CreateUser("look", PasswordHasher.Hash("pw"), "viewer");

        Assert.False(_auth.HasEnabledAdmin());
    }

    [Fact]
    public void 啟用中的管理員才算可登入()
    {
        CreateAdmin();

        Assert.True(_auth.HasEnabledAdmin());
    }

    [Fact]
    public void 驗證未啟用時停用管理員不算鎖死()
    {
        var id = CreateAdmin();

        // auth.enabled 預設為 0：停用唯一 admin 不會鎖死，因為根本不要求登入。
        Assert.False(_auth.WouldRemoveLastEnabledAdmin(id));
    }

    [Fact]
    public void 驗證啟用後唯一的管理員不可被停用()
    {
        var id = CreateAdmin();
        _settings.Set("auth.enabled", "1");

        Assert.True(_auth.WouldRemoveLastEnabledAdmin(id));
    }

    [Fact]
    public void 還有第二個管理員時可以停用其中一個()
    {
        var first = CreateAdmin("root1");
        CreateAdmin("root2");
        _settings.Set("auth.enabled", "1");

        Assert.False(_auth.WouldRemoveLastEnabledAdmin(first));
    }

    [Fact]
    public void 停用的管理員不計入可登入人數()
    {
        var active = CreateAdmin("active");
        var dormant = CreateAdmin("dormant", enabled: false);
        _settings.Set("auth.enabled", "1");

        // dormant 本來就登不進來，停用它不是鎖死原因；active 才是最後一個。
        Assert.False(_auth.WouldRemoveLastEnabledAdmin(dormant));
        Assert.True(_auth.WouldRemoveLastEnabledAdmin(active));
    }

    [Fact]
    public void 停用viewer永遠不算鎖死()
    {
        CreateAdmin();
        var viewer = _users.CreateUser("look", PasswordHasher.Hash("pw"), "viewer");
        _settings.Set("auth.enabled", "1");

        Assert.False(_auth.WouldRemoveLastEnabledAdmin(viewer));
    }
}
