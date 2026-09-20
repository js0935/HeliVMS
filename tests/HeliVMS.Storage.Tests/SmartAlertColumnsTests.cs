using System.IO;

namespace HeliVMS.Storage.Tests;

/// <summary>M54（§14.7 #8）：alert_rules 智慧警報三欄存取。</summary>
public class SmartAlertColumnsTests : IDisposable
{
    private readonly string _dbPath;
    private SqliteStore _store;
    private readonly AlertRuleRepository _repo;

    public SmartAlertColumnsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-alert-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new AlertRuleRepository(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private AlertRule FirstRule() => _repo.ListAll().Single();

    [Fact]
    public void Add_WithSmartFields_RoundTrip()
    {
        var id = _repo.Add("agg", "ai_intrusion", 27, null, "smtp", "ai_intrusion,ai_line_cross", 5, 3);
        var rule = FirstRule();
        Assert.Equal(id, rule.Id);
        Assert.Equal("ai_intrusion", rule.EventType);
        Assert.Equal(27, rule.ChannelId);
        Assert.Equal("ai_intrusion,ai_line_cross", rule.MatchEventTypes);
        Assert.Equal(5, rule.FrameMinutes);
        Assert.Equal(3, rule.MinEventsInWindow);
    }

    [Fact]
    public void LegacyAdd_UsesDefaults()
    {
        _repo.Add("plain", "motion", null, null, null);
        var rule = FirstRule();
        Assert.Null(rule.MatchEventTypes);
        Assert.Equal(0, rule.FrameMinutes);
        Assert.Equal(1, rule.MinEventsInWindow);
    }

    [Fact]
    public void Update_SmartFields()
    {
        var id = _repo.Add("agg", "ai_intrusion", null, null, null, "ai_intrusion", 10, 4);
        _repo.Update(id, "agg2", "ai_crowd", 7, "dense", "webhook,smtp", false, "ai_crowd", 15, 5);
        var rule = FirstRule();
        Assert.Equal("agg2", rule.Name);
        Assert.Equal("ai_crowd", rule.EventType);
        Assert.Equal(7, rule.ChannelId);
        Assert.Equal("webhook,smtp", rule.Channels);
        Assert.False(rule.Enabled);
        Assert.Equal("ai_crowd", rule.MatchEventTypes);
        Assert.Equal(15, rule.FrameMinutes);
        Assert.Equal(5, rule.MinEventsInWindow);
    }

    [Fact]
    public void Reinitialize_IsIdempotent_WithColumns()
    {
        _repo.Add("agg", "ai_line_cross", null, null, null, "ai_line_cross", 5, 2);
        _store.Dispose();
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _store.Initialize();

        var repo2 = new AlertRuleRepository(_store);
        var rule = repo2.ListAll().Single();
        Assert.Equal("ai_line_cross", rule.MatchEventTypes);
        Assert.Equal(5, rule.FrameMinutes);
        Assert.Equal(2, rule.MinEventsInWindow);
    }

    [Fact]
    public void ListEnabled_FiltersByEnabled()
    {
        var id = _repo.Add("agg", "ai_intrusion", null, null, null, "ai_intrusion", 5, 3);
        Assert.Single(_repo.ListEnabled());
        _repo.SetEnabled(id, false);
        Assert.Empty(_repo.ListEnabled());
        Assert.Single(_repo.ListAll());
    }
}