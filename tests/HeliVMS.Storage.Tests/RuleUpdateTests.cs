namespace HeliVMS.Storage.Tests;

/// <summary>
/// 複合事件規則的「編輯」（Update）。編輯走整列覆寫而不是「刪掉重加」：
/// 刪加會換 Id——清單看到的條目、稽核軌跡（rule.update 對著原 Id 記）都會斷線。
/// </summary>
public class RuleUpdateTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly RuleRepository _repo;

    public RuleUpdateTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-ruleupd-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _repo = new RuleRepository(_store);
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
    public void 編輯規則保留Id與建立時間並更新內容()
    {
        var createdUtc = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var id = _repo.Add("夜間人員", "{\"match\":{\"eventType\":\"ai_person\"}}", "{}", createdUtc: createdUtc);

        Assert.True(_repo.Update(
            id,
            "夜間人員（加強）",
            "{\"match\":{\"eventType\":\"ai_person\",\"channelId\":3}}",
            "{\"severity\":\"critical\",\"notify\":true}",
            enabled: false));

        var rule = _repo.Get(id);
        Assert.NotNull(rule);
        Assert.Equal(id, rule!.Id);
        Assert.Equal("夜間人員（加強）", rule.Name);
        Assert.Contains("channelId", rule.ExpressionJson);
        Assert.Contains("critical", rule.ActionsJson);
        Assert.False(rule.Enabled);
        Assert.Equal(SqliteStore.Iso(createdUtc), rule.CreatedAt);
    }

    [Fact]
    public void 編輯會寫稽核()
    {
        var id = _repo.Add("規則A", "{\"match\":{\"eventType\":\"motion\"}}", "{}");

        _repo.Update(id, "規則B", "{\"match\":{\"eventType\":\"motion\"}}", "{}", enabled: true);

        var audit = new AuditLogRepository(_store);
        var updates = audit.List(new AuditLogQuery { Action = "rule.update", Limit = 100 });
        Assert.Contains(updates, e => e.TargetType == "ai_rule" && e.TargetId == id);
    }

    [Fact]
    public void 編輯不存在的規則回傳false不寫稽核()
    {
        Assert.False(_repo.Update(9999, "幽靈", "{\"match\":{\"eventType\":\"motion\"}}", "{}", enabled: true));

        var audit = new AuditLogRepository(_store);
        var updates = audit.List(new AuditLogQuery { Action = "rule.update", Limit = 100 });
        Assert.DoesNotContain(updates, e => e.TargetId == 9999);
    }
}
