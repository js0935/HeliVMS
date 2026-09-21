namespace HeliVMS.Storage.Tests;

using Microsoft.Data.Sqlite;

public class EventSearchRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AlarmEventRepository _events;
    private readonly EventSearchRepository _search;

    private static DateTime T(int minute) =>
        new DateTime(2026, 9, 22, 9, 0, 0, DateTimeKind.Utc).AddMinutes(minute);

    public EventSearchRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-evtsearch-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _events = new AlarmEventRepository(_store);
        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, 'c1', 'rtsp://cam1', NULL, 'h264', 1, 'copy');
            """);
        _search = new EventSearchRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public void RebuildIndex_HealsStaleIndex()
    {
        _events.Insert(1, "motion", T(0), null, "alpha beta motion");
        _events.Insert(1, "tamper", T(1), null, "gamma delta tamper");
        Assert.Single(_search.Search("alpha"));

        _store.Execute("DELETE FROM alarm_events_fts;"); // 製造索引與主表不一致（match 失效、count 不變）

        var repo = new EventSearchRepository(_store); // 建構即重建（正規 'rebuild'）自癒

        Assert.Single(repo.Search("alpha"));
        Assert.Single(repo.Search("gamma"));
        Assert.Contains(repo.Search("gamma"), h => h.EventType == "tamper");
    }

    [Fact]
    public void Trigger_NewInsertsSearchable()
    {
        _events.Insert(1, "motion", T(0), null, "hello world left");
        _events.Insert(1, "motion", T(1), null, "hello world right");

        var hits = _search.Search("hello world");

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void Search_WordTokensCaseInsensitive()
    {
        _events.Insert(1, "motion", T(0), null, "Hello World 12345");

        Assert.Single(_search.Search("hello"));
        Assert.Single(_search.Search("WORLD"));
        Assert.Single(_search.Search("12345"));
    }

    [Fact]
    public void Search_NoMatch_Empty()
    {
        _events.Insert(1, "motion", T(0), null, "alpha beta");

        Assert.Empty(_search.Search("nonexistenttoken"));
    }

    [Fact]
    public void Search_BlankQuery_Throws()
    {
        Assert.Throws<ArgumentException>(() => _search.Search(""));
        Assert.Throws<ArgumentException>(() => _search.Search("   "));
    }

    [Fact]
    public void Search_FromToRangeFilter()
    {
        _events.Insert(1, "motion", T(0), null, "alpha");
        _events.Insert(1, "motion", T(5), null, "alpha");
        _events.Insert(1, "motion", T(10), null, "alpha");

        var afterT5 = _search.Search("alpha", fromUtc: T(5)); // 左閉
        Assert.Equal(2, afterT5.Count);

        var beforeT5 = _search.Search("alpha", toUtc: T(5)); // 右開：不含 T(5)
        var beforeHit = Assert.Single(beforeT5);
        Assert.Equal(T(0), beforeHit.OccurredAtUtc);

        var between = _search.Search("alpha", fromUtc: T(2), toUtc: T(8));
        var hit = Assert.Single(between);
        Assert.Equal(T(5), hit.OccurredAtUtc);
    }

    [Fact]
    public void Search_DeleteRemovesFromIndex()
    {
        var id = _events.Insert(1, "motion", T(0), null, "alpha beta");
        Assert.Single(_search.Search("alpha"));

        _store.Execute("DELETE FROM alarm_events WHERE id = $id;", cmd =>
            cmd.Parameters.AddWithValue("$id", id));

        Assert.Empty(_search.Search("alpha"));
    }

    [Fact]
    public void Search_UpdateReflectsNewDetail()
    {
        var id = _events.Insert(1, "motion", T(0), null, "alpha beta");

        _store.Execute("UPDATE alarm_events SET detail = $d WHERE id = $id;", cmd =>
        {
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$d", "gamma delta");
        });

        Assert.Empty(_search.Search("alpha"));
        Assert.Single(_search.Search("gamma"));
    }

    [Fact]
    public void Search_RanksBetterHitFirst()
    {
        _events.Insert(1, "motion", T(0), null, "alpha");
        _events.Insert(1, "motion", T(1), null, "alpha beta"); // 兩 token 都中 → bm25 較相關

        var hits = _search.Search("alpha OR beta");

        Assert.Equal(2, hits.Count);
        Assert.True(hits[0].Rank < hits[1].Rank);
        Assert.Equal(T(1), hits[0].OccurredAtUtc);
    }

    [Fact]
    public void Search_LimitEnforced()
    {
        for (var i = 0; i < 5; i++)
        {
            _events.Insert(1, "motion", T(i), null, "alpha");
        }

        var hits = _search.Search("alpha", limit: 2);

        Assert.Equal(2, hits.Count);
    }
}