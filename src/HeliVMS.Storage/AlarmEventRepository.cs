using HeliVMS.Shared.Models;

namespace HeliVMS.Storage;

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
}