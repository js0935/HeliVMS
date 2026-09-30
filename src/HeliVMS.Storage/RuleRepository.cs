using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>複合事件規則（M62，§5.10 設定層 ai_rules 資料列）。</summary>
public sealed record AiRuleRecord(
    int Id,
    string Name,
    string ExpressionJson,
    string ActionsJson,
    bool Enabled,
    string CreatedAt);

/// <summary>複合事件規則存取（M62，§5.10：規則編輯器與複合事件——設定層）。M159：CRUD 寫稽核。</summary>
public sealed class RuleRepository
{
    private const string Columns = "id, name, expression_json, actions_json, enabled, created_at";

    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    public RuleRepository(SqliteStore store)
    {
        _store = store;
        _audit = new AuditLogRepository(store);
    }

    public int Add(string name, string expressionJson, string actionsJson, bool enabled = true, DateTime? createdUtc = null, string actor = "system")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("規則名稱不可為空", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(expressionJson))
        {
            throw new ArgumentException("規則條件不可為空", nameof(expressionJson));
        }

        var id = _store.Query(
            $"""
            INSERT INTO ai_rules (name, expression_json, actions_json, enabled, created_at)
            VALUES ($n, $e, $a, $en, $t);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return (int)r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$e", expressionJson);
                cmd.Parameters.AddWithValue("$a", string.IsNullOrWhiteSpace(actionsJson) ? "{}" : actionsJson);
                cmd.Parameters.AddWithValue("$en", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(createdUtc ?? DateTime.UtcNow));
            });
        _audit.Record(actor, "rule.add", AuditCategories.Config, targetType: "ai_rule", targetId: id, detail: $"name={name}");
        return id;
    }

    public IReadOnlyList<AiRuleRecord> List()
        => _store.Query(
            $"SELECT {Columns} FROM ai_rules ORDER BY id;",
            ReadAll);

    public IReadOnlyList<AiRuleRecord> ListEnabled()
        => _store.Query(
            $"SELECT {Columns} FROM ai_rules WHERE enabled = 1 ORDER BY id;",
            ReadAll);

    public AiRuleRecord? Get(int id)
        => _store.Query(
            $"SELECT {Columns} FROM ai_rules WHERE id = $id;",
            static r => r.Read() ? Read(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));

    public void SetEnabled(int id, bool enabled, string actor = "system")
    {
        var affected = _store.Query<int>(
            """
            UPDATE ai_rules SET enabled = $e WHERE id = $id;
            SELECT changes();
            """,
            static r => r.Read() ? r.GetInt32(0) : 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });
        if (affected > 0)
        {
            _audit.Record(actor, "rule.toggle", AuditCategories.Config, targetType: "ai_rule", targetId: id, detail: $"enabled={enabled}");
        }
    }

    public void Delete(int id, string actor = "system")
    {
        var affected = _store.Query<int>(
            """
            DELETE FROM ai_rules WHERE id = $id;
            SELECT changes();
            """,
            static r => r.Read() ? r.GetInt32(0) : 0,
            cmd => cmd.Parameters.AddWithValue("$id", id));
        if (affected > 0)
        {
            _audit.Record(actor, "rule.delete", AuditCategories.Config, targetType: "ai_rule", targetId: id);
        }
    }

    private static IReadOnlyList<AiRuleRecord> ReadAll(SqliteDataReader r)
    {
        var list = new List<AiRuleRecord>();
        while (r.Read())
        {
            list.Add(Read(r));
        }

        return list;
    }

    private static AiRuleRecord Read(SqliteDataReader r)
        => new(
            r.GetInt32(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.GetInt32(4) != 0,
            r.GetString(5));
}