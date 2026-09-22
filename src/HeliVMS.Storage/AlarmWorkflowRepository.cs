using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>警報分診工作流之進度註記（M102，§14.7 #3）：分診人員對單一事件的操作紀錄串。</summary>
public sealed record AlarmNote(long Id, long EventId, string Author, string Note, DateTime CreatedAtUtc);

/// <summary>警報升階紀錄（M102）：逾時後策略升階的軌跡（level 由 1 起遞增）。</summary>
public sealed record AlarmEscalationRecord(
    long Id,
    long EventId,
    int Level,
    string FromPriority,
    string ToPriority,
    DateTime DueUtc,
    DateTime CreatedAtUtc);

/// <summary>
/// 警報管理器分診工作流倉儲（M102，SQLite v38）：進度註記（alarm_notes）與逾時升階紀錄
/// （alarm_escalations），搭配 M47 分診面板（優先序/SLA 截止/逾期）成「指派→進度→升階」大面板資料。
/// </summary>
public sealed class AlarmWorkflowRepository
{
    private readonly SqliteStore _store;

    public AlarmWorkflowRepository(SqliteStore store) => _store = store;

    public long AddNote(long eventId, string author, string note, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("註記內容不得為空白。", nameof(note));
        }

        _store.Execute(
            """
            INSERT INTO alarm_notes (event_id, author, note, created_at)
            VALUES ($e, $a, $n, $t);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", eventId);
                cmd.Parameters.AddWithValue("$a", author);
                cmd.Parameters.AddWithValue("$n", note);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(createdAtUtc));
            });
        return _store.Query("SELECT last_insert_rowid();", static r => r.Read() ? r.GetInt64(0) : 0);
    }

    /// <summary>某事件的註記串（舊→新）。</summary>
    public IReadOnlyList<AlarmNote> NotesByEvent(long eventId)
    {
        return _store.Query(
            """
            SELECT id, event_id, author, note, created_at
            FROM alarm_notes
            WHERE event_id = $e
            ORDER BY id;
            """,
            static r =>
            {
                var list = new List<AlarmNote>();
                while (r.Read())
                {
                    list.Add(new AlarmNote(
                        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
                        SqliteStore.FromIso(r.GetString(4))));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$e", eventId));
    }

    public int NoteCountByEvent(long eventId)
        => _store.Query(
            "SELECT COUNT(*) FROM alarm_notes WHERE event_id = $e;",
            static r => r.Read() ? r.GetInt32(0) : 0,
            cmd => cmd.Parameters.AddWithValue("$e", eventId));

    /// <summary>移除一筆註記；回 true＝實際刪除。</summary>
    public bool RemoveNote(long noteId)
    {
        _store.Execute("DELETE FROM alarm_notes WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", noteId));
        return _store.Query("SELECT changes();", static r => r.Read() && r.GetInt32(0) > 0);
    }

    /// <summary>
    /// 記錄一次升階（level＝既有最大值＋1）。<paramref name="decision"/> 由
    /// <see cref="AlarmEscalationPolicy.Decide"/> 產生；NewDueUtc 為升階後 SLA 截止。
    /// </summary>
    public long RecordEscalation(long eventId, AlarmSlaDecision decision, DateTime createdAtUtc)
    {
        var level = MaxLevel(eventId) + 1;
        _store.Execute(
            """
            INSERT INTO alarm_escalations (event_id, level, from_priority, to_priority, due_utc, created_at)
            VALUES ($e, $l, $f, $t, $d, $c);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", eventId);
                cmd.Parameters.AddWithValue("$l", level);
                cmd.Parameters.AddWithValue("$f", decision.FromPriority);
                cmd.Parameters.AddWithValue("$t", decision.ToPriority);
                cmd.Parameters.AddWithValue("$d", SqliteStore.Iso(decision.NewDueUtc));
                cmd.Parameters.AddWithValue("$c", SqliteStore.Iso(createdAtUtc));
            });
        return _store.Query("SELECT last_insert_rowid();", static r => r.Read() ? r.GetInt64(0) : 0);
    }

    /// <summary>某事件升階軌跡（舊→新）。</summary>
    public IReadOnlyList<AlarmEscalationRecord> EscalationsByEvent(long eventId)
    {
        return _store.Query(
            """
            SELECT id, event_id, level, from_priority, to_priority, due_utc, created_at
            FROM alarm_escalations
            WHERE event_id = $e
            ORDER BY id;
            """,
            static r =>
            {
                var list = new List<AlarmEscalationRecord>();
                while (r.Read())
                {
                    list.Add(new AlarmEscalationRecord(
                        r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.GetString(4),
                        SqliteStore.FromIso(r.GetString(5)), SqliteStore.FromIso(r.GetString(6))));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$e", eventId));
    }

    private int MaxLevel(long eventId)
        => _store.Query(
            "SELECT COALESCE(MAX(level), 0) FROM alarm_escalations WHERE event_id = $e;",
            static r => r.Read() ? r.GetInt32(0) : 0,
            cmd => cmd.Parameters.AddWithValue("$e", eventId));
}