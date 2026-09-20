using System.Globalization;

namespace HeliVMS.Storage.Tests;

/// <summary>M56（§5.2）：事件中心關鍵字／條件查詢。</summary>
public class AlarmEventQueryTests : IDisposable
{
    private readonly string _dbPath;
    private SqliteStore _store;
    private readonly AlarmEventRepository _repo;
    private readonly int _chId;

    public AlarmEventQueryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-evtq-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new AlarmEventRepository(_store);
        _store.Execute(
            "INSERT INTO channels (name, main_rtsp) VALUES ('harness-q', 'rtsp://query-probe');");
        var channelId = _store.Query(
            "SELECT last_insert_rowid();",
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            });
        InsertProbe(channelId, "ai_line_cross", "probe alpha");
        InsertProbe(channelId, "ai_intrusion", "probe beta");
        InsertProbe(channelId, "motion", "other");
        _chId = channelId;
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private void InsertProbe(int channelId, string type, string detail)
    {
        _repo.Insert(channelId, type, new DateTime(2026, 9, 20, 1, 0, 0, DateTimeKind.Utc), null, detail);
    }

    private AlarmEventRepository.QueryArgs Wide(string? keyword = null, string? eventType = null, int? channelId = null)
    {
        return new AlarmEventRepository.QueryArgs
        {
            FromUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Keyword = keyword,
            EventType = eventType,
            ChannelId = channelId,
        };
    }

    [Fact]
    public void Keyword_MatchingDetail_Filters()
    {
        var result = _repo.ListByQuery(Wide(keyword: "probe"));
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Contains("probe", r.Detail));
    }

    [Fact]
    public void Keyword_NoMatch_IsEmpty()
    {
        Assert.Empty(_repo.ListByQuery(Wide(keyword: "not-present-keyword-xyz")));
    }

    [Fact]
    public void Keyword_IsCaseInsensitive()
    {
        Assert.Single(_repo.ListByQuery(Wide(keyword: "ALPHA")));
    }

    [Fact]
    public void Combined_ChannelEventKeyword()
    {
        var result = _repo.ListByQuery(Wide(keyword: "probe", eventType: "ai_intrusion", channelId: _chId));
        var single = result.Single();
        Assert.Equal("ai_intrusion", single.EventType);
        Assert.Equal("probe beta", single.Detail);
    }

    [Fact]
    public void CountByQuery_MatchesKeyword()
    {
        Assert.Equal(2, _repo.CountByQuery(Wide(keyword: "probe")));
    }

    [Fact]
    public void Limit_AppliesToQuery()
    {
        var limited = new AlarmEventRepository.QueryArgs
        {
            FromUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Keyword = "probe",
            Limit = 1,
        };
        Assert.Single(_repo.ListByQuery(limited));
    }
}