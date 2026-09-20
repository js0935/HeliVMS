namespace HeliVMS.Storage;

/// <summary>錄影時數報表列（M60，§14.7 #9 統計報表）。</summary>
public sealed record RecordingSummaryRow(int ChannelId, string ChannelName, double Hours, long Bytes);

/// <summary>容量趨勢列（M60）：逐日位元組／錄影時數。</summary>
public sealed record CapacityTrendRow(string Day, long Bytes, double Hours);

/// <summary>AI／事件類別計數列（M60）：按 event_type 統計。</summary>
public sealed record AiEventCountRow(string EventType, long Count);

/// <summary>
/// 統圖報表查詢（M60，§14.7 #9、§11.7）：唯讀統計既有 segments／alarm_events／channels。
/// 時間以 ISO 文字比對（含終點不含終點界：`>= from` 且 `&lt; to`）。
/// </summary>
public sealed class ReportRepository
{
    private readonly SqliteStore _store;

    public ReportRepository(SqliteStore store) => _store = store;

    /// <summary>各頻道錄影時數與位元組（`status='final'`；LEFT JOIN 讓無錄影頻道也列出、時數為 0）。</summary>
    public IReadOnlyList<RecordingSummaryRow> ListRecordingSummary(DateTime fromUtc, DateTime toUtc)
    {
        return _store.Query(
            """
            SELECT c.id, c.name,
                   COALESCE(SUM(s.duration_sec), 0) / 3600.0,
                   COALESCE(SUM(s.size_bytes), 0)
            FROM channels c
            LEFT JOIN segments s
              ON s.channel_id = c.id AND s.status = 'final'
             AND s.start_time >= $from AND s.start_time < $to
            GROUP BY c.id, c.name
            ORDER BY c.id;
            """,
            static r =>
            {
                var rows = new List<RecordingSummaryRow>();
                while (r.Read())
                {
                    rows.Add(new RecordingSummaryRow(
                        r.GetInt32(0),
                        r.GetString(1),
                        r.IsDBNull(2) ? 0 : r.GetDouble(2),
                        r.GetInt64(3)));
                }

                return rows;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
            });
    }

    /// <summary>容量趨勢：範圍內逐日（UTC 日）位元組與錄影時數。</summary>
    public IReadOnlyList<CapacityTrendRow> ListCapacityTrend(DateTime fromUtc, DateTime toUtc)
    {
        return _store.Query(
            """
            SELECT substr(s.start_time, 1, 10) AS day,
                   COALESCE(SUM(s.size_bytes), 0),
                   COALESCE(SUM(s.duration_sec), 0) / 3600.0
            FROM segments s
            WHERE s.status = 'final' AND s.start_time >= $from AND s.start_time < $to
            GROUP BY day
            ORDER BY day;
            """,
            static r =>
            {
                var rows = new List<CapacityTrendRow>();
                while (r.Read())
                {
                    rows.Add(new CapacityTrendRow(r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? 0 : r.GetDouble(2)));
                }

                return rows;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
            });
    }

    /// <summary>斷線次數：範圍內 `event_type='offline'` 筆數（M29 斷線事件源既有）。</summary>
    public long GetDisconnectCount(DateTime fromUtc, DateTime toUtc)
    {
        return _store.Query(
            """
            SELECT COUNT(*)
            FROM alarm_events
            WHERE event_type = 'offline' AND start_time >= $from AND start_time < $to;
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
            });
    }

    /// <summary>AI／事件類別計數：範圍內按 `event_type` 統計（motion／`ai_*`／tamper 等，VIP）。</summary>
    public IReadOnlyList<AiEventCountRow> ListAiEventSummary(DateTime fromUtc, DateTime toUtc)
    {
        return _store.Query(
            """
            SELECT event_type, COUNT(*)
            FROM alarm_events
            WHERE start_time >= $from AND start_time < $to
            GROUP BY event_type
            ORDER BY COUNT(*) DESC, event_type;
            """,
            static r =>
            {
                var rows = new List<AiEventCountRow>();
                while (r.Read())
                {
                    rows.Add(new AiEventCountRow(r.GetString(0), r.GetInt64(1)));
                }

                return rows;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
            });
    }
}