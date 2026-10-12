namespace HeliVMS.Storage;

/// <summary>
/// 告警規則存取（alert_rules 表；M37）。排序以 id 遞增為「第一匹配優先」：
/// NotificationService 依順序取第一條命中規則套用渠道白名單。
/// </summary>
public sealed class AlertRuleRepository
{
    private readonly SqliteStore _store;
    private readonly AuditLogRepository _audit;

    /// <summary>預設自建 <see cref="AuditLogRepository"/>（同 <c>ChannelRepository</c>）：告警規則異動寫入稽核。</summary>
    public AlertRuleRepository(SqliteStore store, AuditLogRepository? audit = null)
    {
        _store = store;
        _audit = audit ?? new AuditLogRepository(store);
    }

    /// <summary>新增一筆規則，回傳新 ID。</summary>
    public long Add(
        string name,
        string? eventType,
        int? channelId,
        string? keyword,
        string? channels,
        string? matchEventTypes = null,
        int frameMinutes = 0,
        int minEventsInWindow = 1,
        string? actor = null)
    {
        _store.Execute(
            """
            INSERT INTO alert_rules (name, event_type, channel_id, keyword, channels, enabled, match_event_types, frame_minutes, min_events_in_window)
            VALUES ($name, $t, $c, $k, $ch, 1, $mt, $fm, $mi);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$t", (object?)eventType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", (object?)channelId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$k", (object?)keyword ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ch", (object?)channels ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$mt", (object?)matchEventTypes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fm", frameMinutes);
                cmd.Parameters.AddWithValue("$mi", minEventsInWindow);
            });

        var id = _store.Query(
            "SELECT last_insert_rowid();",
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            });
        _audit.Record(actor, "alert_rule.add", AuditCategories.Config, "alert_rule", id, $"name={name}");
        return id;
    }

    /// <summary>更新一筆規則（含 M54 三欄）。</summary>
    public void Update(
        long id,
        string name,
        string? eventType,
        int? channelId,
        string? keyword,
        string? channels,
        bool enabled,
        string? matchEventTypes = null,
        int frameMinutes = 0,
        int minEventsInWindow = 1,
        string? actor = null)
    {
        _store.Execute(
            """
            UPDATE alert_rules
            SET name = $name, event_type = $t, channel_id = $c, keyword = $k, channels = $ch,
                enabled = $e, match_event_types = $mt, frame_minutes = $fm, min_events_in_window = $mi
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$t", (object?)eventType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", (object?)channelId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$k", (object?)keyword ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ch", (object?)channels ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$mt", (object?)matchEventTypes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fm", frameMinutes);
                cmd.Parameters.AddWithValue("$mi", minEventsInWindow);
            });

        if (Changes() > 0)
        {
            _audit.Record(actor, "alert_rule.update", AuditCategories.Config, "alert_rule", id, $"name={name}");
        }
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
                SELECT id, name, event_type, channel_id, keyword, channels, enabled, match_event_types, frame_minutes, min_events_in_window
                FROM alert_rules
                WHERE enabled = 1
                ORDER BY id;
                """
                : """
                SELECT id, name, event_type, channel_id, keyword, channels, enabled, match_event_types, frame_minutes, min_events_in_window
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
                        r.GetInt32(6) != 0,
                        r.IsDBNull(7) ? null : r.GetString(7),
                        r.GetInt32(8),
                        r.GetInt32(9)));
                }

                return list;
            });
    }

    /// <summary>停用／啟用一筆規則。</summary>
    public void SetEnabled(long id, bool enabled, string? actor = null)
    {
        _store.Execute(
            "UPDATE alert_rules SET enabled = $e WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });

        if (Changes() > 0)
        {
            _audit.Record(actor, enabled ? "alert_rule.enable" : "alert_rule.disable",
                AuditCategories.Config, "alert_rule", id, $"enabled={enabled}");
        }
    }

    /// <summary>刪除一筆規則。</summary>
    public void Delete(long id, string? actor = null)
    {
        _store.Execute(
            "DELETE FROM alert_rules WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));

        if (Changes() > 0)
        {
            _audit.Record(actor, "alert_rule.delete", AuditCategories.Config, "alert_rule", id);
        }
    }

    private int Changes()
        => _store.Query("SELECT changes();", static r => r.Read() ? r.GetInt32(0) : 0);
}