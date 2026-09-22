namespace HeliVMS.Storage.Tests;

public class EdgeSmartEventRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly EdgeSmartEventRepository _repo;

    private static DateTime T(int hour) =>
        new DateTime(2026, 9, 22, hour, 0, 0, DateTimeKind.Utc);

    public EdgeSmartEventRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-edgeai-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new EdgeSmartEventRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Append_RoundTripsAllColumns()
    {
        var id = _repo.Append(3, "person", "T-1", "LtR", 10, 20, 30, 40, T(8));

        var row = Assert.Single(_repo.Query(new EdgeSmartEventQuery(DeviceId: 3)));
        Assert.Equal(id, row.Id);
        Assert.Equal("person", row.ClassName);
        Assert.Equal("T-1", row.TrackId);
        Assert.Equal("LtR", row.Direction);
        Assert.Equal(10, row.X1);
        Assert.Equal(20, row.Y1);
        Assert.Equal(30, row.X2);
        Assert.Equal(40, row.Y2);
        Assert.Equal(T(8), row.OccurredAtUtc);
    }

    [Fact]
    public void Query_FiltersDirection()
    {
        _repo.Append(1, "person", "A", "LtR", 0, 0, 1, 1, T(8));
        _repo.Append(1, "person", "B", "RtL", 0, 0, 1, 1, T(8));

        var rows = _repo.Query(new EdgeSmartEventQuery(Direction: "RtL"));

        var row = Assert.Single(rows);
        Assert.Equal("B", row.TrackId);
    }

    [Fact]
    public void Query_FiltersClassAndDevice()
    {
        _repo.Append(1, "car", "A", "TtoB", 0, 0, 1, 1, T(8));
        _repo.Append(2, "car", "B", "TtoB", 0, 0, 1, 1, T(8));
        _repo.Append(1, "person", "C", "TtoB", 0, 0, 1, 1, T(8));

        var rows = _repo.Query(new EdgeSmartEventQuery(DeviceId: 1, ClassName: "car"));

        var row = Assert.Single(rows);
        Assert.Equal("A", row.TrackId);
    }

    [Fact]
    public void Query_RangeIsLeftClosedRightOpen()
    {
        _repo.Append(1, "person", "A", "LtR", 0, 0, 1, 1, T(8));
        _repo.Append(1, "person", "B", "LtR", 0, 0, 1, 1, T(9));
        _repo.Append(1, "person", "C", "LtR", 0, 0, 1, 1, T(10));

        var rows = _repo.Query(new EdgeSmartEventQuery(FromUtc: T(9), ToUtc: T(11)));

        Assert.Equal(2, rows.Count);
        Assert.Equal("C", rows[0].TrackId); // DESC 最晚在前
        Assert.Equal("B", rows[1].TrackId);
    }

    [Fact]
    public void Query_LimitEnforced()
    {
        for (var h = 0; h < 4; h++)
        {
            _repo.Append(1, "person", $"T{h}", "LtR", 0, 0, 1, 1, T(h));
        }

        var rows = _repo.Query(new EdgeSmartEventQuery(Limit: 2));

        Assert.Equal(2, rows.Count);
        Assert.Equal(T(3), rows[0].OccurredAtUtc);
        Assert.Equal(T(2), rows[1].OccurredAtUtc);
    }

    [Fact]
    public void AggregateByDirection_CountsInRange()
    {
        _repo.Append(1, "person", "A", "LtR", 0, 0, 1, 1, T(8));
        _repo.Append(1, "person", "B", "LtR", 0, 0, 1, 1, T(10));
        _repo.Append(1, "person", "C", "RtL", 0, 0, 1, 1, T(9));
        _repo.Append(1, "person", "D", "Stationary", 0, 0, 1, 1, T(9));
        _repo.Append(2, "person", "E", "LtR", 0, 0, 1, 1, T(9));

        var all = _repo.AggregateByDirection(null, null, null);
        Assert.Equal(3, all.Count);
        Assert.Equal(3, Assert.Single(all, s => s.Direction == "LtR").Count);

        var dev1 = _repo.AggregateByDirection(1, T(9), T(11));
        Assert.Equal(3, dev1.Count); // T9:B、T10:C、T9:D 三向
        Assert.Equal(1, Assert.Single(dev1, s => s.Direction == "LtR").Count);
        Assert.Equal(1, Assert.Single(dev1, s => s.Direction == "Stationary").Count);
    }
}