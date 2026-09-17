namespace HeliVMS.Storage;

/// <summary>
/// 告警規則存取（alert_rules 表；M37）。排序以 id 遞增為「第一匹配優先」：
/// NotificationService 依順序取第一條命中規則套用渠道白名單。
/// </summary>
public sealed class AlertRuleRepository
{
    private readonly SqliteStore _store;

    public AlertRuleRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>新增一筆規則，回傳新 ID。</summary>
    public long Add(string name, string? eventType, int? channelId, string? keyword, string? channels)
    {
        _store.Execute(
            """
            INSERT INTO alert_rules (name, event_type, channel_id, keyword, channels, enabled)
            VALUES ($name, $t, $c, $k, $ch, 1);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$t", (object?)eventType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", (object?)channelId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$k", (object?)keyword ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ch", (object?)channels ?? DBNull.Value);
            });

        return _store.Query(
            "SELECT last_insert_rowid();",
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            });
    }

    /// <summary>全部規則（id 遞增＝優先序）。</summary>
    public IReadOnlyList<AlertRule> ListAll()
    {
        return QueryAll(enabledOnly: false);
    }

    /// <summary>啟用中的規則（id 遞增＝優先序）。</summary>
    public IReadOnlyList<AlertRule> ListEnabled()
    {
        return QueryAll(enabledOnly: true);
    }

    private IReadOnlyList<AlertRule> QueryAll(bool enabledOnly)
    {
        return _store.Query(
            enabledOnly
                ? """
                SELECT id, name, event_type, channel_id, keyword, channels, enabled
                FROM alert_rules
                WHERE enabled = 1
                ORDER BY id;
                """
                : """
                SELECT id, name, event_type, channel_id, keyword, channels, enabled
                FROM alert_rules
                ORDER BY id;
                """,
            static r =>
            {
                var list = new List<AlertRule>();
                while (r.Read())
                {
                    list.Add(new AlertRule(
                        r.GetInt64(0),
                        r.GetString(1),
                        r.IsDBNull(2) ? null : r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetInt32(3),
                        r.IsDBNull(4) ? null : r.GetString(4),
                        r.IsDBNull(5) ? null : r.GetString(5),
                        r.GetInt32(6) != 0));
                }

                return list;
            });
    }

    /// <summary>停用／啟用一筆規則。</summary>
    public void SetEnabled(long id, bool enabled)
    {
        _store.Execute(
            "UPDATE alert_rules SET enabled = $e WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>刪除一筆規則。</summary>
    public void Delete(long id)
    {
        _store.Execute(
            "DELETE FROM alert_rules WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }
}