namespace HeliVMS.Storage.Tests;

/// <summary>M62（§5.10）：複合事件規則設定層 CRUD 與 schema v24。</summary>
public class RuleRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;
    private readonly RuleRepository _rules;

    public RuleRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-rules-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _audit = new AuditLogRepository(_store);
        _rules = new RuleRepository(_store);
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
    public void SchemaVersion_IsV44()
    {
        var version = _store.Query(
            "PRAGMA user_version;",
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            });
        Assert.Equal(44, version);
    }

    [Fact]
    public void Add_List_RoundTripsAllColumns()
    {
        var id = _rules.Add(
            "siren 複合規則",
            """{"all":[{"match":{"eventType":"motion"}},{"within":{"seconds":60,"match":{"eventType":"ai_intrusion"}}}]}""",
            """{"severity":"critical","tag":"escalated","notify":true}""");

        var all = _rules.List();
        var rule = Assert.Single(all);
        Assert.Equal(id, rule.Id);
        Assert.Equal("siren 複合規則", rule.Name);
        Assert.Contains("\"eventType\"", rule.ExpressionJson);
        Assert.Contains("\"severity\"", rule.ActionsJson);
        Assert.True(rule.Enabled);
        Assert.False(string.IsNullOrEmpty(rule.CreatedAt));
    }

    [Fact]
    public void Crud_RecordAudit()
    {
        var id = _rules.Add("audited", "{}", "{}", actor: "tester");

        var added = Assert.Single(_audit.List(new AuditLogQuery
        {
            Category = AuditCategories.Config,
            Action = "rule.add",
        }));
        Assert.Equal("tester", added.Actor);
        Assert.Equal("ai_rule", added.TargetType);
        Assert.Equal(id, added.TargetId);
        Assert.Equal("name=audited", added.Detail);

        _rules.SetEnabled(id, false, "editor");

        var toggled = Assert.Single(_audit.List(new AuditLogQuery
        {
            Category = AuditCategories.Config,
            Action = "rule.toggle",
        }));
        Assert.Equal("editor", toggled.Actor);
        Assert.Equal(id, toggled.TargetId);
        Assert.Equal("enabled=False", toggled.Detail);

        _rules.Delete(id, "admin");

        var deleted = Assert.Single(_audit.List(new AuditLogQuery
        {
            Category = AuditCategories.Config,
            Action = "rule.delete",
        }));
        Assert.Equal("admin", deleted.Actor);
        Assert.Equal(id, deleted.TargetId);
    }

    [Fact]
    public void Add_EmptyNameOrExpression_Throws()
    {
        Assert.Throws<ArgumentException>(() => _rules.Add("  ", "{}", "{}"));
        Assert.Throws<ArgumentException>(() => _rules.Add("ok", "  ", "{}"));
    }

    [Fact]
    public void ListEnabled_ExcludesDisabled()
    {
        var a = _rules.Add("a", """{"match":{"eventType":"motion"}}""", "{}", enabled: true);
        var b = _rules.Add("b", """{"match":{"eventType":"motion"}}""", "{}", enabled: false);

        var enabled = Assert.Single(_rules.ListEnabled());
        Assert.Equal(a, enabled.Id);
        Assert.Equal(1, _rules.List().Count(x => x.Name == "b"));
        _ = b;
    }

    [Fact]
    public void SetEnabled_Toggles()
    {
        var id = _rules.Add("t", "{}", "{}");

        _rules.SetEnabled(id, false);
        Assert.False(Assert.Single(_rules.List()).Enabled);
        Assert.Empty(_rules.ListEnabled());

        _rules.SetEnabled(id, true);
        Assert.True(Assert.Single(_rules.ListEnabled()).Enabled);
    }

    [Fact]
    public void Delete_RemovesRow()
    {
        var id = _rules.Add("d", "{}", "{}");

        _rules.Delete(id);
        Assert.Empty(_rules.List());
    }

    [Fact]
    public void Get_ReturnsNull_ForMissing()
    {
        Assert.Null(_rules.Get(424242));
    }
}