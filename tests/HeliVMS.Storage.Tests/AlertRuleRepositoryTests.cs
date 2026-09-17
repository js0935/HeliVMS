using HeliVMS.Storage;

namespace HeliVMS.Storage.Tests;

/// <summary>M37 alert_rules 表（告警規則存取）。</summary>
public class AlertRuleRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AlertRuleRepository _repo;

    public AlertRuleRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-test-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new AlertRuleRepository(_store);
    }

    [Fact]
    public void Add_ThenListAll_ReturnsStoredValues()
    {
        var id = _repo.Add("motion 規則", "motion", 7, "peak", "webhook,snmp");

        var rules = _repo.ListAll();
        var rule = Assert.Single(rules);
        Assert.Equal(id, rule.Id);
        Assert.Equal("motion 規則", rule.Name);
        Assert.Equal("motion", rule.EventType);
        Assert.Equal(7, rule.ChannelId);
        Assert.Equal("peak", rule.Keyword);
        Assert.Equal("webhook,snmp", rule.Channels);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void Add_WithNullConditions_StoresNulls()
    {
        _repo.Add("全條件不限", null, null, null, null);

        var rule = Assert.Single(_repo.ListAll());
        Assert.Null(rule.EventType);
        Assert.Null(rule.ChannelId);
        Assert.Null(rule.Keyword);
        Assert.Null(rule.Channels);
    }

    [Fact]
    public void SetEnabled_PersistsFlag()
    {
        var id = _repo.Add("r", "motion", null, null, null);

        _repo.SetEnabled(id, enabled: false);
        Assert.Empty(_repo.ListEnabled());
        Assert.False(Assert.Single(_repo.ListAll()).Enabled);

        _repo.SetEnabled(id, enabled: true);
        Assert.Single(_repo.ListEnabled());
    }

    [Fact]
    public void Delete_RemovesRule()
    {
        var id = _repo.Add("r", "motion", null, null, null);

        _repo.Delete(id);
        Assert.Empty(_repo.ListAll());
        Assert.Empty(_repo.ListEnabled());
    }

    [Fact]
    public void ListEnabled_OrdersByIdAscending()
    {
        var a = _repo.Add("r1", "motion", null, null, null);
        var b = _repo.Add("r2", "person", null, null, null);
        _repo.SetEnabled(a, enabled: false);

        var list = _repo.ListEnabled();
        Assert.Single(list);
        Assert.Equal(b, list[0].Id);

        _repo.SetEnabled(a, enabled: true);
        Assert.Equal(new[] { a, b }, _repo.ListEnabled().Select(r => r.Id));
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 視窗鎖檔可忽略
        }
    }
}