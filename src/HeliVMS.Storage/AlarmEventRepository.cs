using Microsoft.Data.Sqlite;
using HeliVMS.Shared.Models;

namespace HeliVMS.Storage;

/// <summary>一筆尚未結束的離線事件（M29：斷線補送）。</summary>
public sealed record OpenOfflineEvent(long Id, DateTime StartUtc);

/// <summary>
/// 事件（警報/運動/斷線）存取（§4 alarm_events 表）。
/// </summary>
public sealed class AlarmEventRepository
{
    private readonly SqliteStore _store;

    public AlarmEventRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>寫入一筆事件，回傳新 ID。</summary>
    public long Insert(int channelId, string eventType, DateTime startUtc, string? snapshotPath = null, string? detail = null)
    {
        _store.Execute(
            """
            INSERT INTO alarm_events (channel_id, event_type, start_time, snapshot_path, detail)
            VALUES ($c, $t, $s, $snap, $d);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$t", eventType);
                cmd.Parameters.AddWithValue("$s", SqliteStore.Iso(startUtc));
                cmd.Parameters.AddWithValue("$snap", (object?)snapshotPath ?? DBNull.Value);
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

    /// <summary>更新事件的結束時間與快照路徑（運動事件收尾）。</summary>
    public void UpdateEnd(long id, DateTime endUtc, string? snapshotPath)
    {
        _store.Execute(
            """
            UPDATE alarm_events
            SET end_time = $e, snapshot_path = $s
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", SqliteStore.Iso(endUtc));
                cmd.Parameters.AddWithValue("$s", (object?)snapshotPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>依時間範圍（與可選頻道）列出事件，由新到舊。包含事件之間隔，以 start_time 倒序。</summary>
    public IReadOnlyList<AlarmEventRecord> ListByRange(int? channelId, DateTime fromUtc, DateTime toUtc)
    {
        var where = new List<string> { "start_time >= $from", "start_time <= $to" };
        if (channelId is int c)
        {
            where.Add("channel_id = $c");
        }

        var sql = string.Join(" AND ", where);
        return _store.Query(
            $"""
            SELECT id, channel_id, event_type, start_time, end_time, snapshot_path, detail, acknowledged
            FROM alarm_events
            WHERE {sql}
            ORDER BY start_time DESC;
            """,
            static r =>
            {
                var list = new List<AlarmEventRecord>();
                while (r.Read())
                {
                    list.Add(new AlarmEventRecord
                    {
                        Id = r.GetInt64(0),
                        ChannelId = r.GetInt32(1),
                        EventType = r.GetString(2),
                        StartUtc = SqliteStore.FromIso(r.GetString(3)),
                        EndUtc = r.IsDBNull(4) ? null : SqliteStore.FromIso(r.GetString(4)),
                        SnapshotPath = r.IsDBNull(5) ? null : r.GetString(5),
                        Detail = r.IsDBNull(6) ? null : r.GetString(6),
                        Acknowledged = r.GetInt32(7) != 0,
                    });
                }

                return list;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
                if (channelId is int c2)
                {
                    cmd.Parameters.AddWithValue("$c", c2);
                }
            });
    }

    /// <summary>確認／取消確認事件。</summary>
    public void Acknowledge(long id, bool acknowledged)
    {
        _store.Execute(
            "UPDATE alarm_events SET acknowledged = $a WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$a", acknowledged ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>未確認事件筆數。</summary>
    public int CountUnacknowledged()
    {
        return _store.Query(
            "SELECT COUNT(1) FROM alarm_events WHERE acknowledged = 0;",
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            });
    }

    /// <summary>事件中心查詢參數（M28：類型篩選＋分頁）。</summary>
    public sealed class QueryArgs
    {
        public int? ChannelId { get; init; }

        public string? EventType { get; init; }

        public DateTime FromUtc { get; init; }

        public DateTime ToUtc { get; init; }

        public int Limit { get; init; } = 200;

        public int Offset { get; init; }
    }

    /// <summary>組出共用 WHERE 子句（時間窗＋可選頻道＋可選類型）。</summary>
    private List<string> BuildWhere(QueryArgs q)
    {
        var where = new List<string> { "start_time >= $from", "start_time <= $to" };
        if (q.ChannelId is int c)
        {
            where.Add("channel_id = $c");
        }

        if (!string.IsNullOrWhiteSpace(q.EventType))
        {
            where.Add("event_type = $et");
        }

        return where;
    }

    private void BindWhere(QueryArgs q, SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(q.FromUtc));
        cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(q.ToUtc));
        if (q.ChannelId is int c)
        {
            cmd.Parameters.AddWithValue("$c", c);
        }

        if (!string.IsNullOrWhiteSpace(q.EventType))
        {
            cmd.Parameters.AddWithValue("$et", q.EventType);
        }
    }

    private AlarmEventRecord ReadRecord(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        ChannelId = r.GetInt32(1),
        EventType = r.GetString(2),
        StartUtc = SqliteStore.FromIso(r.GetString(3)),
        EndUtc = r.IsDBNull(4) ? null : SqliteStore.FromIso(r.GetString(4)),
        SnapshotPath = r.IsDBNull(5) ? null : r.GetString(5),
        Detail = r.IsDBNull(6) ? null : r.GetString(6),
        Acknowledged = r.GetInt32(7) != 0,
    };

    /// <summary>依 QueryArgs（類型／頻道／時間窗／頁）列出事件，由新到舊。</summary>
    public IReadOnlyList<AlarmEventRecord> ListByQuery(QueryArgs q)
    {
        var where = BuildWhere(q);
        var sql = string.Join(" AND ", where);
        return _store.Query(
            $"""
            SELECT id, channel_id, event_type, start_time, end_time, snapshot_path, detail, acknowledged
            FROM alarm_events
            WHERE {sql}
            ORDER BY start_time DESC
            LIMIT {Math.Max(1, q.Limit)} OFFSET {Math.Max(0, q.Offset)};
            """,
            r =>
            {
                var list = new List<AlarmEventRecord>();
                while (r.Read())
                {
                    list.Add(ReadRecord(r));
                }

                return list;
            },
            cmd => BindWhere(q, cmd));
    }

    /// <summary>同 ListByQuery 條件的總筆數（分頁計數用；忽略 Limit/Offset）。</summary>
    public int CountByQuery(QueryArgs q)
    {
        var where = BuildWhere(q);
        var sql = string.Join(" AND ", where);
        return _store.Query(
            $"SELECT COUNT(1) FROM alarm_events WHERE {sql};",
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            },
            cmd => BindWhere(q, cmd));
    }

    /// <summary>既有事件類型清單（去重、排序）。</summary>
    public IReadOnlyList<string> ListEventTypes()
    {
        return _store.Query(
            "SELECT DISTINCT event_type FROM alarm_events ORDER BY event_type;",
            static r =>
            {
                var list = new List<string>();
                while (r.Read())
                {
                    list.Add(r.GetString(0));
                }

                return list;
            });
    }

    /// <summary>指定頻道最新一筆尚未結束（end_time IS NULL）的離線事件。</summary>
    public OpenOfflineEvent? FindOpenOffline(int channelId)
    {
        return _store.Query(
            """
            SELECT id, start_time
            FROM alarm_events
            WHERE channel_id = $c AND event_type = 'offline' AND end_time IS NULL
            ORDER BY id DESC
            LIMIT 1;
            """,
            static r =>
            {
                if (!r.Read())
                {
                    return null;
                }

                return new OpenOfflineEvent(r.GetInt64(0), SqliteStore.FromIso(r.GetString(1)));
            },
            cmd => cmd.Parameters.AddWithValue("$c", channelId));
    }

    /// <summary>收斂所有尚未結束的事件（App 啟動時呼叫；上次離線已因關閉而結束）。</summary>
    public void CloseOpenEvents(DateTime endUtc)
    {
        _store.Execute(
            "UPDATE alarm_events SET end_time = $e WHERE end_time IS NULL;",
            cmd => cmd.Parameters.AddWithValue("$e", SqliteStore.Iso(endUtc)));
    }
}