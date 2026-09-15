using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

public class NotificationLogRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly NotificationLogRepository _repo;

    public NotificationLogRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-nlog-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new NotificationLogRepository(_store);
    }

    [Fact]
    public void AddAndListRecent_RoundTrip()
    {
        var id1 = _repo.Add(3, "motion", "webhook", true, 1, null);
        var id2 = _repo.Add(7, "offline", "smtp", false, 3, "smtp 失敗");

        Assert.True(id2 > id1);
        var rows = _repo.ListRecent(10);
        Assert.Equal(2, rows.Count);
        Assert.Equal(7, rows[0].ChannelId);
        Assert.Equal("offline", rows[0].EventType);
        Assert.Equal("smtp", rows[0].Route);
        Assert.False(rows[0].Ok);
        Assert.Equal(3, rows[0].Attempts);
        Assert.Equal("smtp 失敗", rows[0].Detail);
        Assert.Equal(3, rows[1].ChannelId);
        Assert.True(rows[1].Ok);
        Assert.Null(rows[1].Detail);
    }

    [Fact]
    public void ListRecent_LimitsAndOrdersNewestFirst()
    {
        for (var i = 0; i < 5; i++)
        {
            _repo.Add(1, "motion", "webhook", true, 1, null);
        }

        var rows = _repo.ListRecent(3);
        Assert.Equal(3, rows.Count);
        Assert.Equal(5, rows[0].Id);
        Assert.Equal(3, rows[2].Id);
        Assert.Equal(5, _repo.Count());
    }

    [Fact]
    public void Count_ZeroOnEmpty()
    {
        Assert.Equal(0, _repo.Count());
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
        }
    }
}