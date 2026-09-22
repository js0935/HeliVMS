using System.Data.Common;

namespace HeliVMS.Storage;

/// <summary>法證語意搜尋來源（M97，§14.7 #7）。</summary>
[Flags]
public enum ForensicSource
{
    Alarm = 1,
    Door = 2,
    Pos = 4,
    EdgeSmart = 8,
    All = Alarm | Door | Pos | EdgeSmart,
}

/// <summary>跨來源語意搜尋命中（M97）：來源＋原文片段＋時間＋bm25 rank。</summary>
public sealed record ForensicSearchHit(ForensicSource Source, long SourceId, string Text, DateTime OccurredAtUtc, double Rank);

/// <summary>法證語意搜尋多源（M97）：alarm 以外把門禁/POS/Edge AI metadata 併入同一全文檢索介面。</summary>
public sealed class UnifiedEventSearch
{
    private readonly SqliteStore _store;

    public UnifiedEventSearch(SqliteStore store)
    {
        _store = store;
        RebuildAll();
    }

    public void RebuildAll()
    {
        foreach (var table in new[] { "alarm_events_fts", "door_events_fts", "pos_events_fts", "edge_smart_events_fts" })
        {
            _store.Execute($"INSERT INTO {table}({table}) VALUES ('rebuild');");
        }
    }

    public IReadOnlyList<ForensicSearchHit> Search(
        string query,
        DateTime? fromUtc,
        DateTime? toUtc,
        ForensicSource sourceMask = ForensicSource.All,
        int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("查詢不可為空", nameof(query));
        }

        query = FtsQuery.Normalize(query);

        var hits = new List<ForensicSearchHit>();
        if (sourceMask.HasFlag(ForensicSource.Alarm)) hits.AddRange(SearchAlarm(query, fromUtc, toUtc, limit));
        if (sourceMask.HasFlag(ForensicSource.Door)) hits.AddRange(SearchDoor(query, fromUtc, toUtc, limit));
        if (sourceMask.HasFlag(ForensicSource.Pos)) hits.AddRange(SearchPos(query, fromUtc, toUtc, limit));
        if (sourceMask.HasFlag(ForensicSource.EdgeSmart)) hits.AddRange(SearchEdge(query, fromUtc, toUtc, limit));

        return hits
            .OrderByDescending(h => h.OccurredAtUtc)
            .ThenBy(h => h.Rank)
            .Take(limit)
            .ToList();
    }

    private IReadOnlyList<ForensicSearchHit> Search(string sql, Action<Microsoft.Data.Sqlite.SqliteCommand> bind)
    {
        return _store.Query(sql, r =>
        {
            var rows = new List<ForensicSearchHit>();
            while (r.Read())
            {
                rows.Add(new ForensicSearchHit(
                    (ForensicSource)r.GetInt32(0),
                    r.GetInt64(1),
                    r.GetString(2),
                    SqliteStore.FromIso(r.GetString(3)),
                    r.IsDBNull(4) ? 0.0 : r.GetDouble(4)));
            }

            return rows;
        }, bind);
    }

    private IReadOnlyList<ForensicSearchHit> SearchAlarm(string q, DateTime? from, DateTime? to, int limit)
    {
        return Search(
            """
            SELECT 1, e.id, e.detail, e.start_time, bm25(alarm_events_fts)
            FROM alarm_events_fts JOIN alarm_events e ON e.id = alarm_events_fts.rowid
            WHERE alarm_events_fts MATCH $q AND ($from IS NULL OR e.start_time >= $from) AND ($to IS NULL OR e.start_time < $to)
            LIMIT $lim;
            """,
            cmd => Bind(cmd, q, from, to, limit));
    }

    private IReadOnlyList<ForensicSearchHit> SearchDoor(string q, DateTime? from, DateTime? to, int limit)
    {
        return Search(
            """
            SELECT 2, e.id, (e.card_id || ' ' || e.direction || ' ' || e.reason), e.occurred_at_utc, bm25(door_events_fts)
            FROM door_events_fts JOIN door_events e ON e.id = door_events_fts.rowid
            WHERE door_events_fts MATCH $q AND ($from IS NULL OR e.occurred_at_utc >= $from) AND ($to IS NULL OR e.occurred_at_utc < $to)
            LIMIT $lim;
            """,
            cmd => Bind(cmd, q, from, to, limit));
    }

    private IReadOnlyList<ForensicSearchHit> SearchPos(string q, DateTime? from, DateTime? to, int limit)
    {
        return Search(
            """
            SELECT 4, e.id, (e.transaction_no || ' ' || e.register_id || ' ' || CAST(e.amount_cents AS TEXT)), e.occurred_at_utc, bm25(pos_events_fts)
            FROM pos_events_fts JOIN pos_events e ON e.id = pos_events_fts.rowid
            WHERE pos_events_fts MATCH $q AND ($from IS NULL OR e.occurred_at_utc >= $from) AND ($to IS NULL OR e.occurred_at_utc < $to)
            LIMIT $lim;
            """,
            cmd => Bind(cmd, q, from, to, limit));
    }

    private IReadOnlyList<ForensicSearchHit> SearchEdge(string q, DateTime? from, DateTime? to, int limit)
    {
        return Search(
            """
            SELECT 8, e.id, (e.class_name || ' ' || e.track_id || ' ' || e.direction), e.occurred_at_utc, bm25(edge_smart_events_fts)
            FROM edge_smart_events_fts JOIN edge_smart_events e ON e.id = edge_smart_events_fts.rowid
            WHERE edge_smart_events_fts MATCH $q AND ($from IS NULL OR e.occurred_at_utc >= $from) AND ($to IS NULL OR e.occurred_at_utc < $to)
            LIMIT $lim;
            """,
            cmd => Bind(cmd, q, from, to, limit));
    }

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand cmd, string q, DateTime? from, DateTime? to, int limit)
    {
        cmd.Parameters.AddWithValue("$q", q);
        cmd.Parameters.AddWithValue("$from", from is { } f ? SqliteStore.Iso(f) : DBNull.Value);
        cmd.Parameters.AddWithValue("$to", to is { } t ? SqliteStore.Iso(t) : DBNull.Value);
        cmd.Parameters.AddWithValue("$lim", limit);
    }
}