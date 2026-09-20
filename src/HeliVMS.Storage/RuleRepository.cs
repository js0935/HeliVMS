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

/// <summary>複合事件規則存取（M62，§5.10：規則編輯器與複合事件——設定層）。</summary>
public sealed class RuleRepository
{
    private const string Columns = "id, name, expression_json, actions_json, enabled, created_at";

    private readonly SqliteStore _store;

    public RuleRepository(SqliteStore store)
    {
        _store = store;
    }

    public int Add(string name, string expressionJson, string actionsJson, bool enabled = true, DateTime? createdUtc = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("規則名稱不可為空", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(expressionJson))
        {
            throw new ArgumentException("規則條件不可為空", nameof(expressionJson));
        }

        return _store.Query(
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

    public void SetEnabled(int id, bool enabled)
        => _store.Execute(
            "UPDATE ai_rules SET enabled = $e WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });

    public void Delete(int id)
        => _store.Execute(
            "DELETE FROM ai_rules WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));

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