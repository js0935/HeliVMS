namespace HeliVMS.Storage;

/// <summary>
/// failover_events 接管軌跡存取（M98，§14.7 #9 L1）。事件以 UTC ISO8601 記錄；
/// <see cref="ListAfter"/> 依時間 DESC 回傳（供監控視窗/稽核顯示），並實作
/// <see cref="IFailoverEventLog"/> 供協調器直接寫入。
/// </summary>
public sealed class FailoverEventRepository : IFailoverEventLog
{
    private readonly SqliteStore _store;

    public FailoverEventRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>追加一筆接管事件並回傳 id。</summary>
    public long Append(string serverId, FailoverEventMode mode, string detail, DateTime atUtc)
    {
        return _store.Query(
            """
            INSERT INTO failover_events (server_id, mode, detail, at_utc)
            VALUES ($s, $m, $d, $t);
            SELECT last_insert_rowid();
            """,
            static r => { r.Read(); return r.GetInt64(0); },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$s", serverId);
                cmd.Parameters.AddWithValue("$m", mode.ToString());
                cmd.Parameters.AddWithValue("$d", detail);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(atUtc));
            });
    }

    /// <summary>列出自 <paramref name="fromUtc"/>（含）起的事件，時間 DESC、上限 <paramref name="limit"/>。</summary>
    public IReadOnlyList<FailoverEvent> ListAfter(DateTime fromUtc, int limit = 100)
    {
        return _store.Query(
            """
            SELECT id, server_id, mode, detail, at_utc
            FROM failover_events
            WHERE at_utc >= $from
            ORDER BY at_utc DESC, id DESC
            LIMIT $lim;
            """,
            static r =>
            {
                var events = new List<FailoverEvent>();
                while (r.Read())
                {
                    events.Add(new FailoverEvent(
                        r.GetInt64(0),
                        r.GetString(1),
                        Enum.Parse<FailoverEventMode>(r.GetString(2)),
                        r.GetString(3),
                        SqliteStore.FromIso(r.GetString(4))));
                }

                return events;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$lim", limit);
            });
    }
}