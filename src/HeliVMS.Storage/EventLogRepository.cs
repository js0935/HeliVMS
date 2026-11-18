using HeliVMS.Shared;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>事件平面的一列（警報事件與稽核日誌的聯集）。</summary>
/// <remarks>
/// 兩個來源的欄位語意不同，這裡只保留「事件紀錄」畫面看得到的共同欄位：
/// <see cref="Origin"/> 區分來源（<see cref="EventLogOrigin.Alarm"/>／
/// <see cref="EventLogOrigin.Audit"/>），<see cref="Bucket"/> 是
/// <see cref="EventTaxonomy"/> 算出來的分類，<see cref="Code"/> 是原始代碼
/// （警報的 <c>event_type</c>／稽核的 <c>action</c>）。
/// 稽核列沒有處置狀態（<see cref="Status"/> 為 null、<see cref="Acknowledged"/> 為 false），
/// 也沒有結束時間（<see cref="DurationSeconds"/> 為 null）。
/// </remarks>
public sealed record EventLogEntry(
    long Id,
    string Origin,
    DateTime OccurredAtUtc,
    string Bucket,
    string Code,
    long? ChannelId,
    string? Detail,
    string? Actor,
    string? Status,
    bool Acknowledged,
    string? AssignedTo,
    string? Note,
    double? DurationSeconds,
    string? SnapshotPath)
{
    /// <summary>這一列是不是稽核日誌（唯讀：不能確認、處置、回放或遮蔽）。</summary>
    public bool IsAudit => Origin == EventLogOrigin.Audit;
}

/// <summary>事件平面的來源代碼。</summary>
public static class EventLogOrigin
{
    public const string Alarm = "alarm";
    public const string Audit = "audit";
}

/// <summary>
/// 事件平面查詢條件。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Bucket"/> 是 <see cref="EventTaxonomy.Buckets"/> 之一；分類衍生自既有資料，
/// 不在 SQL 重寫一份口徑（見 <see cref="EventLogRepository"/>）。
/// </para>
/// <para>
/// <see cref="Status"/>／<see cref="EventType"/>／<see cref="EventIds"/> 只對警報事件有意義，
/// 一旦指定就整批排除稽核列——稽核列沒有處置狀態，勉強讓它通過篩選等於回報一個不存在的狀態
/// （「僅未確認」會混進一堆永遠不會被確認的稽核紀錄，而清單看起來很正常）。
/// </para>
/// </remarks>
public sealed class EventLogQuery
{
    /// <summary>分類（<see cref="EventTaxonomy.Buckets"/>）；null／空＝不限。</summary>
    public string? Bucket { get; init; }

    /// <summary>頻道；稽核列以 <c>target_type='channel' AND target_id=</c> 比對。</summary>
    public long? ChannelId { get; init; }

    /// <summary>警報事件的 <c>event_type</c>（指定時只查警報）。</summary>
    public string? EventType { get; init; }

    /// <summary>處置狀態（指定時只查警報）。</summary>
    public string? Status { get; init; }

    /// <summary>關鍵字（警報詳情／稽核動作或詳情的 LIKE）；null／空＝不限。</summary>
    public string? Keyword { get; init; }

    /// <summary>警報事件 id 白名單（視覺搜索命中集）；null／空＝不過濾，指定時只查警報。</summary>
    public IReadOnlyList<long>? EventIds { get; init; }

    /// <summary>時間窗起點（含）。</summary>
    public DateTime FromUtc { get; init; }

    /// <summary>時間窗終點（含）。</summary>
    public DateTime ToUtc { get; init; }

    public int Limit { get; init; } = 200;

    public int Offset { get; init; }
}

/// <summary>
/// 事件平面倉儲：把 <c>alarm_events</c> 與 <c>audit_log</c> 併成一份由新到舊的清單。
/// </summary>
/// <remarks>
/// <para>
/// 參考機的事件紀錄是單一清單（eventlog.type 1–5：System／Operation／Network／Event／Alarm），
/// 而 HeliVMS 的資料本來就分成兩張表：警報事件與「誰做了什麼」的稽核。畫面上要對齊，
/// 但<b>不</b>把兩張表複製成第三張——複本一定會有一天和來源不一致，而畫面看不出來。
/// </para>
/// <para>
/// 分類的 SQL 條件不是把 <see cref="EventTaxonomy"/> 的規則再寫一遍，而是先取出兩張表
/// <b>實際存在</b>的代碼（<c>event_type</c>／<c>category</c>），在 C# 用同一份 taxonomy 分類，
/// 再把屬於該分類的代碼以 <c>IN (…)</c> 回推成 SQL。這樣新增別名或前綴規則時，分類欄與
/// 篩選結果仍然一致；分成兩份實作的話，症狀是「清單上有這一列，選那個分類卻查不到」。
/// </para>
/// <para>
/// 分頁的總數用<b>同一組 WHERE</b> 的 <c>UNION ALL</c> 子查詢算，不是把兩個來源各自的
/// <c>COUNT</c> 相加（那會在時間窗或頻道篩選不一致時給出對不上的頁數）。
/// </para>
/// </remarks>
public sealed class EventLogRepository
{
    private readonly SqliteStore _store;

    public EventLogRepository(SqliteStore store) => _store = store;

    /// <summary>依條件列出事件平面的一頁（由新到舊）。</summary>
    public IReadOnlyList<EventLogEntry> List(EventLogQuery q)
    {
        ArgumentNullException.ThrowIfNull(q);
        var parts = BuildParts(q);
        if (parts.IsEmpty)
        {
            return [];
        }

        var union = string.Join(
            "\n    UNION ALL\n",
            new[] { parts.AlarmSelect, parts.AuditSelect }.Where(static s => s.Length > 0));

        var sql =
            $"""
            WITH plane AS (
            {union}
            )
            SELECT origin, id, ts, code, bucket_code, channel_id, detail, actor, status, acknowledged,
                   assigned_to, note, end_ts, snapshot_path
            FROM plane
            ORDER BY ts DESC, origin, id DESC
            LIMIT {Math.Max(1, q.Limit)} OFFSET {Math.Max(0, q.Offset)};
            """;

        return _store.Query(
            sql,
            static r =>
            {
                var list = new List<EventLogEntry>();
                while (r.Read())
                {
                    list.Add(ReadEntry(r));
                }

                return list;
            },
            cmd => Bind(cmd, q, parts));
    }

    /// <summary>同 <see cref="List"/> 條件的總筆數（忽略 Limit／Offset）。</summary>
    public int Count(EventLogQuery q)
    {
        ArgumentNullException.ThrowIfNull(q);
        var parts = BuildParts(q);
        if (parts.IsEmpty)
        {
            return 0;
        }

        var union = string.Join(
            "\n    UNION ALL\n",
            new[] { parts.AlarmSelect, parts.AuditSelect }.Where(static s => s.Length > 0));

        var sql =
            $"""
            WITH plane AS (
            {union}
            )
            SELECT COUNT(1) FROM plane;
            """;

        return _store.Query(
            sql,
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            },
            cmd => Bind(cmd, q, parts));
    }

    /// <summary>實際存在的警報事件類型（去重、排序；事件中心「類型」下拉用）。</summary>
    public IReadOnlyList<string> ListAlarmTypes()
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

    /// <summary>事件平面 SQL 的兩半（各自可缺席）與其參數。</summary>
    private sealed class Parts
    {
        public bool HasAlarm { get; init; }

        public bool HasAudit { get; init; }

        public string AlarmSelect { get; init; } = "";

        public string AuditSelect { get; init; } = "";

        public string[] AlarmTypes { get; init; } = [];

        public string[] AuditCategories { get; init; } = [];

        public bool IsEmpty => !HasAlarm && !HasAudit;
    }

    private Parts BuildParts(EventLogQuery q)
    {
        // 狀態／事件類型／命中 id 都只屬於警報事件；指定了就不能讓稽核列出現。
        var alarmOnly = !string.IsNullOrWhiteSpace(q.Status)
            || !string.IsNullOrWhiteSpace(q.EventType)
            || q.EventIds is { Count: > 0 };

        var hasBucket = !string.IsNullOrWhiteSpace(q.Bucket);
        string[] alarmTypes = [];
        string[] auditCategories = [];
        if (hasBucket)
        {
            (alarmTypes, auditCategories) = BucketCodeSets(q.Bucket!);
        }

        var includeAlarm = !hasBucket || alarmTypes.Length > 0;
        var includeAudit = !alarmOnly && (!hasBucket || auditCategories.Length > 0);

        string alarmSelect = "";
        if (includeAlarm)
        {
            var where = new List<string> { "a.start_time >= $from", "a.start_time <= $to" };
            if (hasBucket)
            {
                where.Add(CodeIn("a.event_type", "et", alarmTypes.Length));
            }

            if (q.ChannelId is not null)
            {
                where.Add("a.channel_id = $c");
            }

            if (!string.IsNullOrWhiteSpace(q.EventType))
            {
                where.Add("a.event_type = $evt");
            }

            if (!string.IsNullOrWhiteSpace(q.Status))
            {
                where.Add("COALESCE(d.status, 'pending') = $st");
            }

            if (!string.IsNullOrWhiteSpace(q.Keyword))
            {
                where.Add("a.detail LIKE $kw");
            }

            if (q.EventIds is { Count: > 0 } ids)
            {
                where.Add(CodeIn("a.id", "vid", ids.Count));
            }

            alarmSelect =
                $"""
                 SELECT 'alarm' AS origin, a.id AS id, a.start_time AS ts, a.event_type AS code,
                        a.event_type AS bucket_code,
                        a.channel_id AS channel_id, a.detail AS detail, NULL AS actor,
                        COALESCE(d.status, 'pending') AS status, a.acknowledged AS acknowledged,
                        d.assigned_to AS assigned_to, d.note AS note,
                        a.end_time AS end_ts, a.snapshot_path AS snapshot_path
                 FROM alarm_events a
                 LEFT JOIN event_dispositions d ON d.event_id = a.id
                 WHERE {string.Join(" AND ", where)}
                """;
        }

        string auditSelect = "";
        if (includeAudit)
        {
            var where = new List<string> { "l.occurred_at >= $from", "l.occurred_at <= $to" };
            if (hasBucket)
            {
                where.Add(CodeIn("l.category", "cat", auditCategories.Length));
            }

            if (q.ChannelId is not null)
            {
                where.Add("l.target_type = 'channel' AND l.target_id = $c");
            }

            if (!string.IsNullOrWhiteSpace(q.Keyword))
            {
                where.Add("(l.detail LIKE $kw OR l.action LIKE $kw)");
            }

            auditSelect =
                $"""
                 SELECT 'audit' AS origin, l.id AS id, l.occurred_at AS ts, l.action AS code,
                        l.category AS bucket_code,
                        CASE WHEN l.target_type = 'channel' THEN l.target_id END AS channel_id,
                        l.detail AS detail, l.actor AS actor,
                        NULL AS status, 0 AS acknowledged,
                        NULL AS assigned_to, NULL AS note,
                        NULL AS end_ts, NULL AS snapshot_path
                 FROM audit_log l
                 WHERE {string.Join(" AND ", where)}
                """;
        }

        return new Parts
        {
            HasAlarm = includeAlarm,
            HasAudit = includeAudit,
            AlarmSelect = alarmSelect,
            AuditSelect = auditSelect,
            AlarmTypes = alarmTypes,
            AuditCategories = auditCategories,
        };
    }

    private static void Bind(SqliteCommand cmd, EventLogQuery q, Parts parts)
    {
        cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(q.FromUtc));
        cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(q.ToUtc));
        if (q.ChannelId is { } channel)
        {
            cmd.Parameters.AddWithValue("$c", channel);
        }

        if (!string.IsNullOrWhiteSpace(q.EventType))
        {
            cmd.Parameters.AddWithValue("$evt", q.EventType);
        }

        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            cmd.Parameters.AddWithValue("$st", q.Status);
        }

        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            cmd.Parameters.AddWithValue("$kw", $"%{q.Keyword}%");
        }

        if (q.EventIds is { Count: > 0 } ids)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                cmd.Parameters.AddWithValue($"$vid{i}", ids[i]);
            }
        }

        for (var i = 0; i < parts.AlarmTypes.Length; i++)
        {
            cmd.Parameters.AddWithValue($"$et{i}", parts.AlarmTypes[i]);
        }

        for (var i = 0; i < parts.AuditCategories.Length; i++)
        {
            cmd.Parameters.AddWithValue($"$cat{i}", parts.AuditCategories[i]);
        }
    }

    /// <summary>分類 → 兩張表裡實際存在、且屬於這個分類的代碼。</summary>
    private (string[] AlarmTypes, string[] AuditCategories) BucketCodeSets(string bucket)
    {
        var eventTypes = _store.Query(
            "SELECT DISTINCT event_type FROM alarm_events;",
            static r =>
            {
                var list = new List<string>();
                while (r.Read())
                {
                    list.Add(r.GetString(0));
                }

                return list;
            });

        var categories = _store.Query(
            "SELECT DISTINCT category FROM audit_log;",
            static r =>
            {
                var list = new List<string>();
                while (r.Read())
                {
                    list.Add(r.GetString(0));
                }

                return list;
            });

        return (
            eventTypes
                .Where(t => EventTaxonomy.BucketOf(t) == bucket)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static t => t, StringComparer.Ordinal)
                .ToArray(),
            categories
                .Where(c => EventTaxonomy.BucketOfAuditCategory(c) == bucket)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static c => c, StringComparer.Ordinal)
                .ToArray());
    }

    private static string CodeIn(string column, string prefix, int count)
    {
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = $"${prefix}{i}";
        }

        return $"{column} IN ({string.Join(", ", names)})";
    }

    private static EventLogEntry ReadEntry(SqliteDataReader r)
    {
        var origin = r.GetString(0);
        var id = r.GetInt64(1);
        var occurredAt = SqliteStore.FromIso(r.GetString(2));
        var code = r.GetString(3);
        var bucketCode = r.GetString(4);
        var channelId = r.IsDBNull(5) ? (long?)null : r.GetInt64(5);
        var detail = r.IsDBNull(6) ? null : r.GetString(6);
        var actor = r.IsDBNull(7) ? null : r.GetString(7);
        var status = r.IsDBNull(8) ? null : r.GetString(8);
        var acknowledged = !r.IsDBNull(9) && r.GetInt32(9) != 0;
        var assignedTo = r.IsDBNull(10) ? null : r.GetString(10);
        var note = r.IsDBNull(11) ? null : r.GetString(11);
        double? duration = r.IsDBNull(12)
            ? null
            : (SqliteStore.FromIso(r.GetString(12)) - occurredAt).TotalSeconds;
        var snapshot = r.IsDBNull(13) ? null : r.GetString(13);

        // 分類的來源欄位兩邊不同：警報看 event_type，稽核看 category（不是 action）。
        var bucket = origin == EventLogOrigin.Audit
            ? EventTaxonomy.BucketOfAuditCategory(bucketCode)
            : EventTaxonomy.BucketOf(bucketCode);

        return new EventLogEntry(
            id,
            origin,
            occurredAt,
            bucket,
            code,
            channelId,
            detail,
            actor,
            status,
            acknowledged,
            assignedTo,
            note,
            duration,
            snapshot);
    }
}
