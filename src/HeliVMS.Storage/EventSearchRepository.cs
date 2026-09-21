namespace HeliVMS.Storage;

/// <summary>法證語意搜尋之命中（M91，§14.7 #7）：Rank 愈小愈相關（bm25）。</summary>
public sealed record EventSearchHit(long Id, string EventType, double Rank, DateTime OccurredAtUtc);

/// <summary>
/// 事件全文檢索（M91，§14.7 #7，FTS5 外部內容表 `alarm_events_fts`）：
/// 建構時若索引筆數 ≠ 主表筆數即回填既有列；Insert/Delete/Update 由 v29 trigger 自動同步；
/// Search 以 bm25 排名（較小較相關）、支援 start_time 範圍過濾與 limit；
/// 空白查詢抛 ArgumentException（防 SQLite FTS 語法注入）。
/// </summary>
public sealed class EventSearchRepository
{
    public const int DefaultLimit = 200;

    private readonly SqliteStore _store;

    public EventSearchRepository(SqliteStore store)
    {
        _store = store;
        RebuildIndex();
    }

    /// <summary>依 SQLite 正規作法重建全文索引：'rebuild' 特例插入會依內容表全量重灌
    /// （外部內容表首選指令；不另做手動 SELECT 補插，以免與現列產生重複/不一致）。</summary>
    public void RebuildIndex()
    {
        _store.Execute(
            """
            INSERT INTO alarm_events_fts(alarm_events_fts) VALUES ('rebuild');
            """);
    }

    /// <summary>全文檢索：MATCH 由 SQLite 分詞（字首匹配）、bm25 排名；from/to 過濾 start_time（左閉右開）。</summary>
    public IReadOnlyList<EventSearchHit> Search(string query, DateTime? fromUtc = null, DateTime? toUtc = null, int limit = DefaultLimit)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("查詢不可為空白。", nameof(query));
        }

        if (limit <= 0)
        {
            limit = DefaultLimit;
        }

        var where = "alarm_events_fts MATCH $q";
        if (fromUtc is not null)
        {
            where += " AND a.start_time >= $from";
        }

        if (toUtc is not null)
        {
            where += " AND a.start_time < $to";
        }

        return _store.Query(
            $$"""
            SELECT a.id, a.event_type, a.start_time, bm25(alarm_events_fts) AS r
            FROM alarm_events_fts
            JOIN alarm_events a ON a.id = alarm_events_fts.rowid
            WHERE {{where}}
            ORDER BY r
            LIMIT $lim;
            """,
            reader =>
            {
                var hits = new List<EventSearchHit>();
                while (reader.Read())
                {
                    hits.Add(new EventSearchHit(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetDouble(3),
                        SqliteStore.FromIso(reader.GetString(2))));
                }

                return hits;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$q", query);
                cmd.Parameters.AddWithValue("$lim", limit);
                if (fromUtc is not null)
                {
                    cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc.Value));
                }

                if (toUtc is not null)
                {
                    cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc.Value));
                }
            });
    }
}