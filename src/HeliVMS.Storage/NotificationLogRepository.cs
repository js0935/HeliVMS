namespace HeliVMS.Storage;

/// <summary>通知送達登錄（§16.3 notification_log 表；M23）。</summary>
public sealed record NotificationLogRecord(
    long Id,
    DateTime TsUtc,
    int ChannelId,
    string EventType,
    string Route,
    bool Ok,
    int Attempts,
    string? Detail);

/// <summary>
/// 通知送達紀錄存取（notification_log 表）。送達判定由 NotificationService 寫入：
/// 成功（ok=1）／最終失敗（ok=0，maxAttempts 已達）。
/// </summary>
public sealed class NotificationLogRepository
{
    private readonly SqliteStore _store;

    public NotificationLogRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>寫入一筆送達紀錄，回傳新 ID。</summary>
    public long Add(int channelId, string eventType, string route, bool ok, int attempts, string? detail)
    {
        _store.Execute(
            """
            INSERT INTO notification_log (ts, channel_id, event_type, route, ok, attempts, detail)
            VALUES ($ts, $c, $t, $r, $ok, $a, $d);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$ts", SqliteStore.Iso(DateTime.UtcNow));
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$t", eventType);
                cmd.Parameters.AddWithValue("$r", route);
                cmd.Parameters.AddWithValue("$ok", ok ? 1 : 0);
                cmd.Parameters.AddWithValue("$a", attempts);
                cmd.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
            });

        return _store.Query(
            "SELECT last_insert_rowid();",
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            });
    }

    /// <summary>最近 N 筆送達紀錄（新→舊）。</summary>
    public IReadOnlyList<NotificationLogRecord> ListRecent(int limit)
    {
        return _store.Query(
            """
            SELECT id, ts, channel_id, event_type, route, ok, attempts, detail
            FROM notification_log
            ORDER BY id DESC
            LIMIT $n;
            """,
            static r =>
            {
                var list = new List<NotificationLogRecord>();
                while (r.Read())
                {
                    list.Add(new NotificationLogRecord(
                        r.GetInt64(0),
                        SqliteStore.FromIso(r.GetString(1)),
                        r.GetInt32(2),
                        r.GetString(3),
                        r.GetString(4),
                        r.GetInt32(5) != 0,
                        r.GetInt32(6),
                        r.IsDBNull(7) ? null : r.GetString(7)));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$n", limit));
    }

    /// <summary>總筆數（E2E／測試用）。</summary>
    public int Count()
    {
        return _store.Query(
            "SELECT COUNT(1) FROM notification_log;",
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            });
    }
}