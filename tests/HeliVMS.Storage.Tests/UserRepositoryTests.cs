using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage.Tests;

public class UserRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly UserRepository _repo;

    public UserRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-users-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new UserRepository(_store);
    }

    [Fact]
    public void CreateUser_PersistsAllFields()
    {
        var id = _repo.CreateUser("管理員", PasswordHasher.Hash("pw1"), "admin", "System Admin");

        var user = _repo.GetById(id);
        Assert.NotNull(user);
        Assert.Equal("管理員", user!.Username);
        Assert.Equal("admin", user.Role);
        Assert.Equal("System Admin", user.DisplayName);
        Assert.True(user.Enabled);
        Assert.Equal(0, user.FailedLogins);
        Assert.Null(user.LockedUntil);
        Assert.Null(user.LastLogin);
        Assert.NotEqual("pw1", user.PasswordHash);
    }

    [Fact]
    public void CreateUser_DuplicateUsername_Throws()
    {
        _repo.CreateUser("alice", "h1", "viewer");

        Assert.Throws<SqliteException>(() => _repo.CreateUser("alice", "h2", "viewer"));
    }

    [Fact]
    public void CreateUser_UsernameIsCaseInsensitiveUnique()
    {
        _repo.CreateUser("alice", "h1", "viewer");

        Assert.Throws<SqliteException>(() => _repo.CreateUser("ALICE", "h2", "viewer"));
    }

    [Fact]
    public void CreateUser_InvalidRole_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _repo.CreateUser("bob", "h1", "root"));
    }

    [Fact]
    public void SetRole_InvalidRole_Throws()
    {
        var id = _repo.CreateUser("bob", "h1", "viewer");

        Assert.Throws<ArgumentOutOfRangeException>(() => _repo.SetRole(id, "root"));
    }

    [Fact]
    public void GetByUsername_IsCaseInsensitive()
    {
        _repo.CreateUser("Alice", "h1", "admin", "A");

        Assert.NotNull(_repo.GetByUsername("alice"));
        Assert.NotNull(_repo.GetByUsername("ALICE"));
        Assert.Null(_repo.GetByUsername("nobody"));
    }

    [Fact]
    public void ListUsers_OrdersByUsername()
    {
        _repo.CreateUser("zeta", "h1", "viewer");
        _repo.CreateUser("alpha", "h2", "viewer");

        var users = _repo.ListUsers();
        Assert.Equal(new[] { "alpha", "zeta" }, users.Select(u => u.Username));
    }

    [Fact]
    public void SetEnabled_UpdatesFlag()
    {
        var id = _repo.CreateUser("bob", "h1", "viewer");

        _repo.SetEnabled(id, enabled: false);

        Assert.False(_repo.GetById(id)!.Enabled);
    }

    [Fact]
    public void SetRole_Updates()
    {
        var id = _repo.CreateUser("carol", "h1", "viewer");

        _repo.SetRole(id, "admin");

        Assert.Equal("admin", _repo.GetById(id)!.Role);
    }

    [Fact]
    public void SetDisplayName_UpdatesAndClears()
    {
        var id = _repo.CreateUser("dave", "h1", "viewer");

        _repo.SetDisplayName(id, "Dave");
        Assert.Equal("Dave", _repo.GetById(id)!.DisplayName);

        _repo.SetDisplayName(id, null);
        Assert.Null(_repo.GetById(id)!.DisplayName);
    }

    [Fact]
    public void DeleteUser_Removes()
    {
        var id = _repo.CreateUser("erin", "h1", "viewer");

        _repo.DeleteUser(id);

        Assert.Null(_repo.GetById(id));
    }

    [Fact]
    public void RecordFailedLogin_IncrementsAndReports()
    {
        var id = _repo.CreateUser("frank", "h1", "viewer");

        Assert.Equal(1, _repo.RecordFailedLogin(id, threshold: 5, lockoutMinutes: 5));
        Assert.Equal(2, _repo.RecordFailedLogin(id, threshold: 5, lockoutMinutes: 5));
        Assert.Equal(2, _repo.GetById(id)!.FailedLogins);
        Assert.Null(_repo.GetById(id)!.LockedUntil);
    }

    [Fact]
    public void RecordFailedLogin_ReachesThreshold_LocksWithExpiry()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = _repo.CreateUser("gina", "h1", "viewer");

        var attempts = 0;
        for (var i = 0; i < 3; i++)
        {
            attempts = _repo.RecordFailedLogin(id, threshold: 3, lockoutMinutes: 5, now);
        }

        Assert.Equal(3, attempts);
        Assert.NotNull(_repo.GetById(id)!.LockedUntil);
        Assert.True(_repo.IsLocked(id, now.AddMinutes(1)));
        Assert.False(_repo.IsLocked(id, now.AddMinutes(6)));
    }

    [Fact]
    public void RecordLoginSuccess_ClearsFailuresAndWritesLastLogin()
    {
        var now = new DateTime(2026, 2, 2, 8, 30, 0, DateTimeKind.Utc);
        var id = _repo.CreateUser("hank", "h1", "viewer");
        _repo.RecordFailedLogin(id, threshold: 5, lockoutMinutes: 5, now.AddMinutes(-10));

        _repo.RecordLoginSuccess(id, now);

        var user = _repo.GetById(id)!;
        Assert.Equal(0, user.FailedLogins);
        Assert.Null(user.LockedUntil);
        Assert.Equal(SqliteStore.Iso(now), user.LastLogin);
    }

    [Fact]
    public void ClearLock_ResetsFailuresAndLock()
    {
        var now = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);
        var id = _repo.CreateUser("iris", "h1", "viewer");
        _repo.RecordFailedLogin(id, threshold: 1, lockoutMinutes: 10, now);
        Assert.True(_repo.IsLocked(id, now));

        _repo.ClearLock(id);

        Assert.Equal(0, _repo.GetById(id)!.FailedLogins);
        Assert.Null(_repo.GetById(id)!.LockedUntil);
        Assert.False(_repo.IsLocked(id, now));
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