using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>事件優先序（M47 §14.7 #3；比照 AlarmEventStatus 的常數/Label/IsValid）。</summary>
public static class AlarmPriority
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
    public const string Critical = "critical";

    public static readonly IReadOnlyList<string> All = [Low, Normal, High, Critical];

    public static string Label(string? value) => value switch
    {
        Low => "低",
        High => "高",
        Critical => "緊急",
        Normal => "一般",
        _ => value ?? Normal,
    };

    public static bool IsValid(string? value) => value is Low or Normal or High or Critical;
}

/// <summary>事件分診設定（alarm_triage 一事件一列）。</summary>
public sealed record AlarmTriageRecord(
    long EventId, string Priority, DateTime? DueUtc, string? Owner, DateTime UpdatedAt);

/// <summary>分診面板列（事件＋處置＋分診）。</summary>
public sealed record AlarmBoardRow(
    long EventId,
    int ChannelId,
    string EventType,
    DateTime StartUtc,
    string Status,
    string Priority,
    DateTime? DueUtc,
    string? Owner,
    string? AssignedTo,
    bool IsOverdue);

/// <summary>分診面板彙總（各狀態計數＋逾期數）。</summary>
public sealed record AlarmBoardSummary(
    int Pending, int Acknowledged, int Actioned, int FalseAlarm, int Overdue);

/// <summary>
/// 警報管理器儲存（M47 §14.7 #3）：優先序／處理時限／負責人的 UPSERT 與分診面板查詢。
/// 處置狀態仍由 <see cref="AlarmEventRepository"/> 管理。
/// </summary>
public sealed class AlarmTriageRepository
{
    private readonly SqliteStore _store;

    public AlarmTriageRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>寫入/更新事件分診（優先序＋處理時限＋負責人）。</summary>
    public void SetTriage(long eventId, string priority, DateTime? dueUtc, string? owner, DateTime updatedAtUtc)
    {
        if (!AlarmPriority.IsValid(priority))
        {
            throw new ArgumentException($"無效的優先序：{priority}", nameof(priority));
        }

        _store.Execute(
            """
            INSERT INTO alarm_triage (event_id, priority, due_utc, owner, updated_at)
            VALUES ($id, $p, $d, $o, $t)
            ON CONFLICT(event_id) DO UPDATE SET
                priority = $p, due_utc = $d, owner = $o, updated_at = $t;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", eventId);
                cmd.Parameters.AddWithValue("$p", priority);
                cmd.Parameters.AddWithValue("$d", dueUtc is { } d ? SqliteStore.Iso(d) : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$o", (object?)owner ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(updatedAtUtc));
            });
    }

    /// <summary>單一事件的分診設定；無則 null。</summary>
    public AlarmTriageRecord? Get(long eventId)
    {
        return _store.Query(
            """
            SELECT event_id, priority, due_utc, owner, updated_at
            FROM alarm_triage
            WHERE event_id = $id;
            """,
            static r =>
            {
                if (!r.Read())
                {
                    return null;
                }

                return new AlarmTriageRecord(
                    r.GetInt64(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? null : SqliteStore.FromIso(r.GetString(2)),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    SqliteStore.FromIso(r.GetString(4)));
            },
            cmd => cmd.Parameters.AddWithValue("$id", eventId));
    }

    /// <summary>
    /// 分診面板列（M47）：僅活躍事件（狀態非 false_alarm）。
    /// 排序＝逾期最前→優先序 critical→high→normal→low→事件時間新→舊。
    /// </summary>
    public IReadOnlyList<AlarmBoardRow> ListBoard(DateTime nowUtc, int take = 100)
    {
        return _store.Query(
            """
            SELECT a.id, a.channel_id, a.event_type, a.start_time,
                   COALESCE(d.status, 'pending'),
                   COALESCE(t.priority, 'normal'),
                   t.due_utc, t.owner, COALESCE(d.assigned_to, t.owner)
            FROM alarm_events a
            LEFT JOIN event_dispositions d ON d.event_id = a.id
            LEFT JOIN alarm_triage t ON t.event_id = a.id
            WHERE COALESCE(d.status, 'pending') != 'false_alarm'
            ORDER BY
              CASE WHEN t.due_utc IS NOT NULL AND t.due_utc < $now
                        AND COALESCE(d.status, 'pending') IN ('pending', 'acknowledged')
                   THEN 0 ELSE 1 END,
              CASE COALESCE(t.priority, 'normal')
                   WHEN 'critical' THEN 4 WHEN 'high' THEN 3 WHEN 'normal' THEN 2 ELSE 1 END DESC,
              a.start_time DESC
            LIMIT $take;
            """,
            (SqliteDataReader r) =>
            {
                var list = new List<AlarmBoardRow>();
                while (r.Read())
                {
                    var status = r.GetString(4);
                    DateTime? due = r.IsDBNull(6) ? null : SqliteStore.FromIso(r.GetString(6));
                    var overdue = due is { } du && du < nowUtc &&
                        status is AlarmEventStatus.Pending or AlarmEventStatus.Acknowledged;
                    list.Add(new AlarmBoardRow(
                        r.GetInt64(0),
                        r.GetInt32(1),
                        r.GetString(2),
                        SqliteStore.FromIso(r.GetString(3)),
                        status,
                        r.GetString(5),
                        due,
                        r.IsDBNull(7) ? null : r.GetString(7),
                        r.IsDBNull(8) ? null : r.GetString(8),
                        overdue));
                }

                return list;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$now", SqliteStore.Iso(nowUtc));
                cmd.Parameters.AddWithValue("$take", Math.Max(1, take));
            });
    }

    /// <summary>分診面板彙總：各狀態計數＋逾期數（逾期＝有 due 且已過期且狀態 pending/acknowledged）。</summary>
    public AlarmBoardSummary Summarize(DateTime nowUtc)
    {
        return _store.Query(
            """
            SELECT
              COALESCE(SUM(CASE WHEN COALESCE(d.status, 'pending') = 'pending' THEN 1 ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN COALESCE(d.status, 'pending') = 'acknowledged' THEN 1 ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN COALESCE(d.status, 'pending') = 'actioned' THEN 1 ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN COALESCE(d.status, 'pending') = 'false_alarm' THEN 1 ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN t.due_utc IS NOT NULL AND t.due_utc < $now
                        AND COALESCE(d.status, 'pending') IN ('pending', 'acknowledged')
                   THEN 1 ELSE 0 END), 0)
            FROM alarm_events a
            LEFT JOIN event_dispositions d ON d.event_id = a.id
            LEFT JOIN alarm_triage t ON t.event_id = a.id;
            """,
            static r =>
            {
                r.Read();
                return new AlarmBoardSummary(
                    r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
            },
            cmd => cmd.Parameters.AddWithValue("$now", SqliteStore.Iso(nowUtc)));
    }
}