namespace HeliVMS.Storage.Tests;

public class DoorEventRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly DoorEventRepository _repo;

    private static DateTime T(int hour) =>
        new DateTime(2026, 9, 22, hour, 0, 0, DateTimeKind.Utc);

    public DoorEventRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-door-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new DoorEventRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Insert_RoundTripsAllColumns()
    {
        var id = _repo.Insert(3, 7, "CARD-1001", "In", true, "ok", T(8));

        var rows = _repo.Query(new DoorEventQuery(CardId: "CARD-1001"));
        var row = Assert.Single(rows);
        Assert.Equal(id, row.Id);
        Assert.Equal(3, row.DeviceId);
        Assert.Equal(7, row.DoorId);
        Assert.Equal("In", row.Direction);
        Assert.True(row.Granted);
        Assert.Equal("ok", row.Reason);
        Assert.Equal(T(8), row.OccurredAtUtc);
    }

    [Fact]
    public void Query_ByCardAndRange()
    {
        _repo.Insert(1, 1, "C1", "In", true, "ok", T(7));
        _repo.Insert(1, 1, "C1", "Out", true, "ok", T(8));
        _repo.Insert(1, 1, "C1", "In", false, "denied", T(9));

        var rows = _repo.Query(new DoorEventQuery(CardId: "C1", FromUtc: T(8), ToUtc: T(10)));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.OccurredAtUtc >= T(8) && r.OccurredAtUtc < T(10)));
        Assert.Equal("denied", rows[0].Reason); // DESC：最晚在前
    }

    [Fact]
    public void Query_ByDeviceAndGranted()
    {
        _repo.Insert(1, 1, "A", "In", true, "ok", T(7));
        _repo.Insert(2, 1, "B", "In", false, "denied", T(7));
        _repo.Insert(2, 2, "C", "In", true, "ok", T(7));

        var rows = _repo.Query(new DoorEventQuery(DeviceId: 2, Granted: true));

        var row = Assert.Single(rows);
        Assert.Equal("C", row.CardId);
    }

    [Fact]
    public void Query_ByDoor_Filters()
    {
        _repo.Insert(1, 1, "A", "In", true, "ok", T(7));
        _repo.Insert(1, 2, "B", "In", true, "ok", T(7));

        var rows = _repo.Query(new DoorEventQuery(DoorId: 2));

        var row = Assert.Single(rows);
        Assert.Equal("B", row.CardId);
    }

    [Fact]
    public void CountByCard_RespectsRange()
    {
        _repo.Insert(1, 1, "C1", "In", true, "ok", T(7));
        _repo.Insert(1, 1, "C1", "In", true, "ok", T(9));
        _repo.Insert(1, 1, "C1", "In", true, "ok", T(11));

        Assert.Equal(3, _repo.CountByCard("C1", null, null));
        Assert.Equal(2, _repo.CountByCard("C1", T(8), T(12))); // 左閉右開：T(9)＋T(11)
        Assert.Equal(0, _repo.CountByCard("C1", T(12), null));
    }

    [Fact]
    public void Query_LimitEnforced()
    {
        for (var h = 0; h < 5; h++)
        {
            _repo.Insert(1, 1, "C1", "In", true, "ok", T(h));
        }

        var rows = _repo.Query(new DoorEventQuery(CardId: "C1", Limit: 3));

        Assert.Equal(3, rows.Count);
        Assert.Equal(T(4), rows[0].OccurredAtUtc); // DESC 最晚在前
    }

    [Fact]
    public void Query_UnknownCard_Empty()
    {
        _repo.Insert(1, 1, "C1", "In", true, "ok", T(7));

        Assert.Empty(_repo.Query(new DoorEventQuery(CardId: "NOPE")));
    }
}