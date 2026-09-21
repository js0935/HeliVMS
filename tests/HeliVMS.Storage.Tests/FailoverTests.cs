namespace HeliVMS.Storage.Tests;

public class FailoverTests
{
    private static DateTime T(int secondOffset = 0) =>
        new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc).AddSeconds(secondOffset);

    #region In-memory fake store

    private sealed class MemoryLeaseStore : IFailoverLeaseStore
    {
        private FailoverLease? _lease;
        public bool Deleted { get; private set; }
        public int UpsertCount { get; private set; }

        public FailoverLease? GetLease() => _lease;

        public void Upsert(FailoverLease lease)
        {
            _lease = lease;
            UpsertCount++;
            Deleted = false;
        }

        public void Delete()
        {
            _lease = null;
            Deleted = true;
        }
    }

    #endregion

    #region FailoverCoordinator engine

    [Fact]
    public void AcquireOrRenew_EmptyStore_BecomesLeader()
    {
        var store = new MemoryLeaseStore();
        var c = new FailoverCoordinator(store, "nodeA");

        var role = c.AcquireOrRenew(TimeSpan.FromSeconds(10), T());

        Assert.Equal(FailoverRole.Leader, role);
        Assert.Equal("nodeA", store.GetLease()!.ServerId);
        Assert.Equal(T(10), store.GetLease()!.ExpiresUtc);
    }

    [Fact]
    public void AcquireOrRenew_ValidLeaseByOther_StaysStandby()
    {
        var store = new MemoryLeaseStore();
        var other = new FailoverCoordinator(store, "nodeA");
        other.AcquireOrRenew(TimeSpan.FromSeconds(20), T());

        var self = new FailoverCoordinator(store, "nodeB");
        var role = self.AcquireOrRenew(TimeSpan.FromSeconds(10), T(1));

        Assert.Equal(FailoverRole.Standby, role);
        Assert.Equal("nodeA", store.GetLease()!.ServerId);
    }

    [Fact]
    public void AcquireOrRenew_ExpiredLease_TakesOver()
    {
        var store = new MemoryLeaseStore();
        var other = new FailoverCoordinator(store, "nodeA");
        other.AcquireOrRenew(TimeSpan.FromSeconds(5), T());

        var self = new FailoverCoordinator(store, "nodeB");
        var role = self.AcquireOrRenew(TimeSpan.FromSeconds(10), T(6));

        Assert.Equal(FailoverRole.Leader, role);
        Assert.Equal("nodeB", store.GetLease()!.ServerId);
        Assert.Equal(T(16), store.GetLease()!.ExpiresUtc);
    }

    [Fact]
    public void AcquireOrRenew_SameServerRenew_ExtendsExpiry()
    {
        var store = new MemoryLeaseStore();
        var c = new FailoverCoordinator(store, "nodeA");
        c.AcquireOrRenew(TimeSpan.FromSeconds(10), T());
        Assert.Equal(T(10), store.GetLease()!.ExpiresUtc);

        var role = c.AcquireOrRenew(TimeSpan.FromSeconds(30), T(5));

        Assert.Equal(FailoverRole.Leader, role);
        Assert.Equal(T(35), store.GetLease()!.ExpiresUtc);
    }

    [Fact]
    public void AcquireOrRenew_ExactBoundaryNowEqualsExpiry_ConsideredExpired()
    {
        var store = new MemoryLeaseStore();
        var other = new FailoverCoordinator(store, "nodeA");
        other.AcquireOrRenew(TimeSpan.FromSeconds(10), T());

        var self = new FailoverCoordinator(store, "nodeB");
        var role = self.AcquireOrRenew(TimeSpan.FromSeconds(10), T(10));

        Assert.Equal(FailoverRole.Leader, role);
        Assert.Equal("nodeB", store.GetLease()!.ServerId);
    }

    [Fact]
    public void GetStatus_EmptyStore_ReturnsNone()
    {
        var c = new FailoverCoordinator(new MemoryLeaseStore(), "nodeA");

        var s = c.GetStatus(T());

        Assert.Equal(FailoverRole.None, s.Role);
        Assert.Null(s.LeaderId);
        Assert.Equal(TimeSpan.Zero, s.LeaseRemaining);
        Assert.Null(s.LeaseExpiresUtc);
    }

    [Fact]
    public void GetStatus_LeaseOwnedByOther_ReturnsStandbyWithRemaining()
    {
        var store = new MemoryLeaseStore();
        new FailoverCoordinator(store, "nodeA").AcquireOrRenew(TimeSpan.FromSeconds(20), T());

        var s = new FailoverCoordinator(store, "nodeB").GetStatus(T(5));

        Assert.Equal(FailoverRole.Standby, s.Role);
        Assert.Equal("nodeA", s.LeaderId);
        Assert.Equal("nodeB", s.ServerId);
        Assert.Equal(TimeSpan.FromSeconds(15), s.LeaseRemaining);
        Assert.Equal(T(20), s.LeaseExpiresUtc);
    }

    [Fact]
    public void Release_AsLeader_ClearsLease()
    {
        var store = new MemoryLeaseStore();
        var c = new FailoverCoordinator(store, "nodeA");
        c.AcquireOrRenew(TimeSpan.FromSeconds(10), T());
        Assert.NotNull(store.GetLease());

        var cleared = c.Release(T(1));

        Assert.True(cleared);
        Assert.Null(store.GetLease());
    }

    [Fact]
    public void Release_AsNonLeader_DoesNotOverwrite()
    {
        var store = new MemoryLeaseStore();
        new FailoverCoordinator(store, "nodeA").AcquireOrRenew(TimeSpan.FromSeconds(10), T());

        var cleared = new FailoverCoordinator(store, "nodeB").Release(T());

        Assert.False(cleared);
        Assert.Equal("nodeA", store.GetLease()!.ServerId);
        Assert.False(store.Deleted);
    }

    [Fact]
    public void AcquireOrRenew_NegativeLease_Throws()
    {
        var c = new FailoverCoordinator(new MemoryLeaseStore(), "nodeA");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            c.AcquireOrRenew(TimeSpan.FromSeconds(-1), T()));
    }

    [Fact]
    public void Constructor_EmptyServerId_Throws()
    {
        var store = new MemoryLeaseStore();
        Assert.Throws<ArgumentException>(() => new FailoverCoordinator(store, ""));
        Assert.Throws<ArgumentException>(() => new FailoverCoordinator(store, " "));
        Assert.Throws<ArgumentNullException>(() => new FailoverCoordinator(null!, "nodeA"));
    }

    #endregion

    #region FailoverRepository integration (real SqliteStore)

    private sealed class RepoFixture : IDisposable
    {
        private readonly string _dbPath;
        public readonly SqliteStore Store;
        public readonly FailoverRepository Repo;

        public RepoFixture()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-failover-{Guid.NewGuid():N}.db");
            Store = new SqliteStore(_dbPath);
            Store.Initialize(); // schema v28
            Repo = new FailoverRepository(Store);
        }

        public void Dispose()
        {
            Store.Dispose();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
    }

    [Fact]
    public void Repository_GetNoneInitially_ReturnsNull()
    {
        using var fx = new RepoFixture();
        Assert.Null(fx.Repo.GetLease());
    }

    [Fact]
    public void Repository_UpsertRoundTrip_ReadsBackIso()
    {
        using var fx = new RepoFixture();
        var lease = new FailoverLease("nodeA", new DateTime(2026, 9, 22, 12, 5, 30, 123, DateTimeKind.Utc));

        fx.Repo.Upsert(lease);
        var back = fx.Repo.GetLease();

        Assert.NotNull(back);
        Assert.Equal("nodeA", back!.ServerId);
        Assert.Equal(lease.ExpiresUtc, back.ExpiresUtc);
        Assert.Equal(SqliteStore.Iso(lease.ExpiresUtc), fx.Store.Query(
            "SELECT lease_expires_utc FROM failover_state WHERE id = 1;",
            static r => { r.Read(); return r.GetString(0); }));
    }

    [Fact]
    public void Repository_UpsertOverwriteSingleRow()
    {
        using var fx = new RepoFixture();
        fx.Repo.Upsert(new FailoverLease("nodeA", T(10)));
        fx.Repo.Upsert(new FailoverLease("nodeB", T(20)));

        var back = fx.Repo.GetLease()!;
        Assert.Equal("nodeB", back.ServerId);
        Assert.Equal(T(20), back.ExpiresUtc);
    }

    [Fact]
    public void Repository_Delete_RemovesRow()
    {
        using var fx = new RepoFixture();
        fx.Repo.Upsert(new FailoverLease("nodeA", T(10)));
        Assert.NotNull(fx.Repo.GetLease());

        fx.Repo.Delete();
        Assert.Null(fx.Repo.GetLease());
    }

    [Fact]
    public void Repository_Delete_NoRow_NoOp()
    {
        using var fx = new RepoFixture();
        fx.Repo.Delete(); // 應無副作用
        Assert.Null(fx.Repo.GetLease());
    }

    [Fact]
    public void Coordinator_Integration_AcquireThenTakeover()
    {
        using var fx = new RepoFixture();
        var nodeA = new FailoverCoordinator(fx.Repo, "nodeA");
        var nodeB = new FailoverCoordinator(fx.Repo, "nodeB");

        // A 先取得
        Assert.Equal(FailoverRole.Leader, nodeA.AcquireOrRenew(TimeSpan.FromSeconds(10), T()));
        // B 無法取得
        Assert.Equal(FailoverRole.Standby, nodeB.AcquireOrRenew(TimeSpan.FromSeconds(10), T()));
        // 過期後 B 可接管
        Assert.Equal(FailoverRole.Leader, nodeB.AcquireOrRenew(TimeSpan.FromSeconds(10), T(11)));
        Assert.Equal("nodeB", fx.Repo.GetLease()!.ServerId);
    }

    #endregion
}