namespace HeliVMS.Storage.Tests;

public class AuditLogRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    public AuditLogRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-audit-{Guid.NewGuid():N}.db");
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
    public void SchemaVersion_IsV42()
    {
        var version = _store.Query<int>(
            "PRAGMA user_version;", r => r.Read() ? r.GetInt32(0) : -1);
        Assert.Equal(42, version);
    }

    [Fact]
    public void Record_RoundTripsAllColumns()
    {
        var when = new DateTime(2026, 9, 22, 3, 4, 5, DateTimeKind.Utc);

        var id = _audit.Record(
            "admin", "settings.update", AuditCategories.Config,
            targetType: "channel", targetId: 7, detail: "motion on -> off",
            occurredAtUtc: when);

        Assert.True(id >= 1);
        var row = Assert.Single(_audit.List(new AuditLogQuery()));
        Assert.Equal(id, row.Id);
        Assert.Equal(when, row.OccurredAtUtc);
        Assert.Equal("admin", row.Actor);
        Assert.Equal("settings.update", row.Action);
        Assert.Equal(AuditCategories.Config, row.Category);
        Assert.Equal("channel", row.TargetType);
        Assert.Equal(7, row.TargetId);
        Assert.Equal("motion on -> off", row.Detail);
    }

    [Fact]
    public void Record_Minimal_AllowsNullTargets()
    {
        var id = _audit.Record("sys", "prune", AuditCategories.Retention);

        var row = Assert.Single(_audit.List(new AuditLogQuery()));
        Assert.Equal(id, row.Id);
        Assert.Null(row.TargetType);
        Assert.Null(row.TargetId);
        Assert.Null(row.Detail);
    }

    [Theory]
    [InlineData("", "action", "cat")]
    [InlineData(null, "action", "cat")]
    [InlineData("actor", "", "cat")]
    [InlineData("actor", null, "cat")]
    [InlineData("actor", "action", "")]
    [InlineData("actor", "action", "  ")]
    public void Record_BlankRequiredFields_Throws(string? actor, string? action, string category)
    {
        Assert.ThrowsAny<ArgumentException>(() => _audit.Record(actor!, action!, category));
    }

    [Fact]
    public void List_FiltersByCategoryActorAndTimeRange()
    {
        var t0 = new DateTime(2026, 9, 22, 1, 0, 0, DateTimeKind.Utc);
        _audit.Record("alice", "login.ok", AuditCategories.Auth, occurredAtUtc: t0);
        _audit.Record("alice", "export.run", AuditCategories.Export, occurredAtUtc: t0.AddHours(1));
        _audit.Record("bob", "login.fail", AuditCategories.Auth, occurredAtUtc: t0.AddHours(2));

        Assert.Equal(2, _audit.Count(new AuditLogQuery { Category = AuditCategories.Auth }));
        Assert.Equal(1, _audit.Count(new AuditLogQuery { Actor = "bob" }));
        Assert.Equal(1, _audit.Count(new AuditLogQuery { Action = "export.run" }));

        var ranged = _audit.List(new AuditLogQuery { FromUtc = t0.AddMinutes(30), ToUtc = t0.AddHours(3) });
        Assert.Equal(2, ranged.Count);

        var halfOpen = _audit.List(new AuditLogQuery
        {
            FromUtc = t0.AddMinutes(30),
            ToUtc = t0.AddHours(1).AddMinutes(1),
        });
        Assert.Single(halfOpen);
    }

    [Fact]
    public void List_OrdersNewestFirst_AndSupportsLimitOffset()
    {
        var t0 = new DateTime(2026, 9, 22, 1, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            _audit.Record("a", $"step{i}", AuditCategories.Evidence, occurredAtUtc: t0.AddMinutes(i));
        }

        var firstTwo = _audit.List(new AuditLogQuery { Limit = 2 });
        Assert.Equal(2, firstTwo.Count);
        Assert.Equal("step4", firstTwo[0].Action);
        Assert.Equal("step3", firstTwo[1].Action);

        var nextTwo = _audit.List(new AuditLogQuery { Limit = 2, Offset = 2 });
        Assert.Equal("step2", nextTwo[0].Action);

        Assert.Equal(5, _audit.Count(new AuditLogQuery()));
    }

    [Fact]
    public void PruneOlderThan_RemovesOldKeepsRecent()
    {
        var cutoff = new DateTime(2026, 9, 22, 2, 0, 0, DateTimeKind.Utc);
        _audit.Record("a", "old", AuditCategories.Config, occurredAtUtc: cutoff.AddHours(-5));
        _audit.Record("a", "recent", AuditCategories.Config, occurredAtUtc: cutoff.AddHours(1));

        var deleted = _audit.PruneOlderThan(cutoff);

        Assert.Equal(1, deleted);
        Assert.Equal(1, _audit.Count(new AuditLogQuery()));
        Assert.Equal("recent", Assert.Single(_audit.List(new AuditLogQuery())).Action);
    }
}