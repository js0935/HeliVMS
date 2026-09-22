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

    #region FailoverReconcile engine (M98, 實體接管)

    private sealed class RecordingController : IFailoverRoleController
    {
        public int TakeOverCount { get; private set; }
        public int RelinquishCount { get; private set; }
        public DateTime LastTakeOverAt { get; private set; }
        public DateTime LastRelinquishAt { get; private set; }

        public void TakeOver(DateTime utcNow)
        {
            TakeOverCount++;
            LastTakeOverAt = utcNow;
        }

        public void Relinquish(DateTime utcNow)
        {
            RelinquishCount++;
            LastRelinquishAt = utcNow;
        }
    }

    private sealed class MemoryEventLog : IFailoverEventLog
    {
        public List<(string ServerId, FailoverEventMode Mode, string Detail, DateTime AtUtc)> Entries { get; } = new();

        public long Append(string serverId, FailoverEventMode mode, string detail, DateTime atUtc)
        {
            Entries.Add((serverId, mode, detail, atUtc));
            return Entries.Count;
        }
    }

    private static (FailoverCoordinator C, RecordingController Ctl, MemoryEventLog Log) Rig(string node)
    {
        var ctl = new RecordingController();
        var log = new MemoryEventLog();
        return (new FailoverCoordinator(new MemoryLeaseStore(), node), ctl, log);
    }

    [Fact]
    public void Reconcile_EmptyStore_ElectsLeaderAndTakesOver()
    {
        var (c, ctl, log) = Rig("nodeA");

        var role = c.Reconcile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), T(), ctl, log);

        Assert.Equal(FailoverRole.Leader, role);
        Assert.Equal(1, ctl.TakeOverCount);
        Assert.Equal(0, ctl.RelinquishCount);
        var e = Assert.Single(log.Entries);
        Assert.Equal(FailoverEventMode.Leader, e.Mode);
        Assert.Equal("nodeA", e.ServerId);
    }

    [Fact]
    public void Reconcile_SteadyRenew_NoDuplicateTransition()
    {
        var (c, ctl, log) = Rig("nodeA");
        c.Reconcile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), T(), ctl, log);

        var role = c.Reconcile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), T(1), ctl, log);

        Assert.Equal(FailoverRole.Leader, role);
        Assert.Equal(1, ctl.TakeOverCount);   // 不重複接管
        Assert.Single(log.Entries);            // 不濫寫事件
    }

    [Fact]
    public void Reconcile_OtherValidLease_StaysPassive()
    {
        var store = new MemoryLeaseStore();
        store.Upsert(new FailoverLease("nodeB", T(30)));
        var ctl = new RecordingController();
        var log = new MemoryEventLog();
        var c = new FailoverCoordinator(store, "nodeA");

        var role = c.Reconcile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), T(), ctl, log);

        Assert.Equal(FailoverRole.Standby, role);
        Assert.Equal(0, ctl.TakeOverCount);
        Assert.Equal(0, ctl.RelinquishCount);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Reconcile_OtherExpiredInsideWindow_StandsBy()
    {
        var store = new MemoryLeaseStore();
        store.Upsert(new FailoverLease("nodeB", T(5)));   // 到期 T5
        var ctl = new RecordingController();
        var log = new MemoryEventLog();
        var c = new FailoverCoordinator(store, "nodeA");

        var role = c.Reconcile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), T(6), ctl, log); // 逾時 1s<窗 5s

        Assert.Equal(FailoverRole.Standby, role);
        Assert.Equal(0, ctl.TakeOverCount);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Reconcile_OtherExpiredBeyondWindow_TakesOver()
    {
        var store = new MemoryLeaseStore();
        store.Upsert(new FailoverLease("nodeB", T(5)));
        var ctl = new RecordingController();
        var log = new MemoryEventLog();
        var c = new FailoverCoordinator(store, "nodeA");

        Assert.Equal(FailoverRole.Standby, c.Reconcile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), T(1), ctl, log));
        var role = c.Reconcile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), T(11), ctl, log); // 逾時 6s≥窗 5s

        Assert.Equal(FailoverRole.Leader, role);
        Assert.Equal(1, ctl.TakeOverCount);
        var e = Assert.Single(log.Entries);
        Assert.Equal(FailoverEventMode.Takeover, e.Mode);
        Assert.Equal("nodeA", store.GetLease()!.ServerId);
    }

    [Fact]
    public void Reconcile_LeaderLeaseCededToOther_Relinquishes()
    {
        var store = new MemoryLeaseStore();
        var ctl = new RecordingController();
        var log = new MemoryEventLog();
        var c = new FailoverCoordinator(store, "nodeA");
        c.Reconcile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), T(), ctl, log);
        Assert.Equal(FailoverRole.Leader, c.GetStatus(T()).Role);

        store.Upsert(new FailoverLease("nodeB", T(30))); // 他人取得有效租約

        var role = c.Reconcile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), T(1), ctl, log);

        Assert.Equal(FailoverRole.Standby, role);
        Assert.Equal(1, ctl.RelinquishCount);
        var e = Assert.Single(log.Entries, x => x.Mode == FailoverEventMode.Relinquish);
        Assert.Equal("nodeA", e.ServerId);
    }

    [Fact]
    public void Reconcile_ReleaseThenReelect_LeaderEvent()
    {
        var store = new MemoryLeaseStore();
        var ctl = new RecordingController();
        var log = new MemoryEventLog();
        var c = new FailoverCoordinator(store, "nodeA");
        c.Reconcile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), T(), ctl, log);
        Assert.True(c.Release(T(1)));

        var role = c.Reconcile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), T(2), ctl, log);

        Assert.Equal(FailoverRole.Leader, role);
        Assert.Equal(2, ctl.TakeOverCount);
        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, e => Assert.Equal(FailoverEventMode.Leader, e.Mode));
    }

    [Fact]
    public void Reconcile_NegativeWindow_Throws()
    {
        var (c, ctl, log) = Rig("nodeA");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            c.Reconcile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(-1), T(), ctl, log));
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

    #region FailoverEventRepository integration (M98, v35)

    [Fact]
    public void EventRepo_AppendRoundTrip_ListAfter()
    {
        using var fx = new RepoFixture();
        var events = new FailoverEventRepository(fx.Store);

        var id = events.Append("nodeB", FailoverEventMode.Takeover, "acquired leader lease", T(11));
        events.Append("nodeB", FailoverEventMode.Leader, "re-elected", T(9));

        var list = events.ListAfter(T());
        Assert.Collection(
            list,
            first =>
            {
                Assert.Equal("nodeB", first.ServerId);         // DESC：T11 在前
                Assert.Equal(FailoverEventMode.Takeover, first.Mode);
                Assert.Equal(T(11), first.AtUtc);
            },
            second => Assert.Equal(FailoverEventMode.Leader, second.Mode));
        Assert.True(id > 0);
        var at10 = Assert.Single(events.ListAfter(T(10)));  // from 過濾（含）
        Assert.Equal(FailoverEventMode.Takeover, at10.Mode);
        Assert.Empty(events.ListAfter(T(12)));
    }

    [Fact]
    public void EventRepo_AppendSingle_StoresModeString()
    {
        using var fx = new RepoFixture();
        var events = new FailoverEventRepository(fx.Store);
        events.Append("nodeA", FailoverEventMode.Relinquish, "ceded leader to nodeB", T(3));

        var mode = fx.Store.Query(
            "SELECT mode FROM failover_events WHERE server_id = 'nodeA';",
            static r => { r.Read(); return r.GetString(0); });

        Assert.Equal("Relinquish", mode);
    }

    #endregion

    #endregion
}