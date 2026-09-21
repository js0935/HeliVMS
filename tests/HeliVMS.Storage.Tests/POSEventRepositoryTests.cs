namespace HeliVMS.Storage.Tests;

public class POSEventRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly POSEventRepository _repo;

    private static DateTime T(int hour) =>
        new DateTime(2026, 9, 22, hour, 0, 0, DateTimeKind.Utc);

    public POSEventRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-pos-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new POSEventRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Insert_RoundTripsAllColumns()
    {
        var id = _repo.Insert(3, "R1", "TXN-9", 1299, T(8));

        var row = Assert.Single(_repo.Query(deviceId: 3));
        Assert.Equal(id, row.Id);
        Assert.Equal(3, row.DeviceId);
        Assert.Equal("R1", row.RegisterId);
        Assert.Equal("TXN-9", row.TransactionNo);
        Assert.Equal(1299, row.AmountCents);
        Assert.Equal(T(8), row.OccurredAtUtc);
    }

    [Fact]
    public void Query_ByDeviceAndRange()
    {
        _repo.Insert(1, "R1", "A", 100, T(7));
        _repo.Insert(1, "R1", "B", 200, T(8));
        _repo.Insert(1, "R1", "C", 300, T(9));
        _repo.Insert(2, "R2", "D", 400, T(8));

        var rows = _repo.Query(deviceId: 1, fromUtc: T(8), toUtc: T(10));

        Assert.Equal(2, rows.Count);
        Assert.Equal("C", rows[0].TransactionNo); // DESC 最晚在前
        Assert.Equal("B", rows[1].TransactionNo);
    }

    [Fact]
    public void Query_LimitEnforced()
    {
        for (var h = 0; h < 4; h++)
        {
            _repo.Insert(1, "R1", $"T{h}", 10, T(h));
        }

        var rows = _repo.Query(deviceId: 1, limit: 2);

        Assert.Equal(2, rows.Count);
        Assert.Equal(T(3), rows[0].OccurredAtUtc);
        Assert.Equal(T(2), rows[1].OccurredAtUtc);
    }

    [Fact]
    public void Query_UnknownDevice_Empty()
    {
        _repo.Insert(1, "R1", "A", 100, T(8));

        Assert.Empty(_repo.Query(deviceId: 99));
    }

    [Fact]
    public void Matcher_PairsWithinWindow_OrderedByDelta()
    {
        var pos = T(10);
        var candidates = new[]
        {
            (Ts: T(10).AddMinutes(5), Tag: "5min"),
            (Ts: T(10).AddMinutes(1), Tag: "1min"),
            (Ts: T(10).AddMinutes(-2), Tag: "-2min"),
        };

        var hits = POSEventMatcher.Match(pos, candidates, c => c.Ts, TimeSpan.FromMinutes(3));

        Assert.Equal(2, hits.Count);
        Assert.Equal("1min", hits[0].Item.Tag);
        Assert.Equal("-2min", hits[1].Item.Tag);
    }

    [Fact]
    public void Matcher_ExcludesBeyondWindow()
    {
        var pos = T(10);
        var candidates = new[]
        {
            (Ts: T(10).AddMinutes(4), Tag: "far"),
            (Ts: T(10).AddMinutes(3).AddSeconds(1), Tag: "just-over"),
        };

        var hits = POSEventMatcher.Match(pos, candidates, c => c.Ts, TimeSpan.FromMinutes(3));

        Assert.Empty(hits);
    }

    [Fact]
    public void Matcher_NoCandidates_Empty()
    {
        var hits = POSEventMatcher.Match(T(10), Array.Empty<(DateTime Ts, string Tag)>(), c => c.Ts, TimeSpan.FromMinutes(3));

        Assert.Empty(hits);
    }
}