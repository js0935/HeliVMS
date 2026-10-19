using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// 錄影區段關鍵幀索引（§3.4「播放頭為關鍵幀吸附」；segment_keyframes 表）。
/// </summary>
/// <remarks>
/// <para>
/// 這張表原本只建了 schema 卻無人讀寫；接上它之後，時間軸上的關鍵幀刻度與「T 前最近的
/// 關鍵幀」查表都走這裡，回放端不必每次跳轉都重掃整段檔案。
/// </para>
/// <para>
/// 舊段補建索引是<strong>整批取代</strong>（<see cref="Replace"/> 先清後寫）而非只補缺：GOP
/// 設定改變後同一段可能有不同的關鍵幀密度，混雜的新舊列會讓刻度密度不一致，操作員會以為
/// 刻度畫錯。整批取代後每段的索引永遠來自同一次探測。
/// </para>
/// </remarks>
public sealed class SegmentKeyframeRepository
{
    /// <summary>單次 IN 查詢最多帶幾個段編號（SQLite 參數上限 999 的保守值）。</summary>
    private const int IdBatch = 900;

    private readonly SqliteStore _store;

    public SegmentKeyframeRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>取代某段的全部關鍵幀列（同一交易內先清後寫）。</summary>
    public void Replace(long segmentId, IReadOnlyList<SegmentKeyframe> rows)
    {
        _store.InTransaction(() =>
        {
            _store.Execute(
                "DELETE FROM segment_keyframes WHERE segment_id = $id;",
                cmd => cmd.Parameters.AddWithValue("$id", segmentId));

            foreach (var row in rows)
            {
                _store.Execute(
                    """
                    INSERT INTO segment_keyframes (segment_id, time_sec, moof_offset)
                    VALUES ($s, $t, $o);
                    """,
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("$s", segmentId);
                        cmd.Parameters.AddWithValue("$t", row.TimeSec);
                        cmd.Parameters.AddWithValue("$o", row.MoofOffset);
                    });
            }
        });
    }

    /// <summary>某段的關鍵幀（time_sec 由小到大）。</summary>
    public IReadOnlyList<SegmentKeyframe> List(long segmentId)
    {
        return _store.Query(
            """
            SELECT time_sec, moof_offset
            FROM segment_keyframes
            WHERE segment_id = $id
            ORDER BY time_sec;
            """,
            static r =>
            {
                var list = new List<SegmentKeyframe>();
                while (r.Read())
                {
                    list.Add(new SegmentKeyframe(r.GetDouble(0), r.GetInt64(1)));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$id", segmentId));
    }

    /// <summary>
    /// 一次列出多段的關鍵幀（每段 time_sec 由小到大）；沒有索引的段不會出現在結果裡。
    /// </summary>
    /// <remarks>
    /// 舊寫法是呼叫端對每段各查一次 <see cref="List"/>：一天數百段就是數百次來回，而回放視窗
    /// 換日／換頻道時全落在 UI 執行緒上。這裡一趟查完，行為與逐段查完全一樣（含「無索引即缺席」）。
    /// 分批是為了避開 SQLite 的參數數量上限——10 秒一段的一天有 8640 段，超過舊版的 999。
    /// </remarks>
    public IReadOnlyDictionary<long, IReadOnlyList<SegmentKeyframe>> ListForSegments(
        IReadOnlyList<long> segmentIds)
    {
        var grouped = new Dictionary<long, List<SegmentKeyframe>>();

        for (var start = 0; start < segmentIds.Count; start += IdBatch)
        {
            var batch = Math.Min(IdBatch, segmentIds.Count - start);
            var placeholders = new string[batch];
            for (var i = 0; i < batch; i++)
            {
                placeholders[i] = "$id" + i;
            }

            _ = _store.Query(
                $"""
                SELECT segment_id, time_sec, moof_offset
                FROM segment_keyframes
                WHERE segment_id IN ({string.Join(", ", placeholders)})
                ORDER BY segment_id, time_sec;
                """,
                r =>
                {
                    while (r.Read())
                    {
                        var id = r.GetInt64(0);
                        if (!grouped.TryGetValue(id, out var rows))
                        {
                            rows = [];
                            grouped[id] = rows;
                        }

                        rows.Add(new SegmentKeyframe(r.GetDouble(1), r.GetInt64(2)));
                    }

                    return 0;
                },
                cmd =>
                {
                    for (var i = 0; i < batch; i++)
                    {
                        cmd.Parameters.AddWithValue(placeholders[i], segmentIds[start + i]);
                    }
                });
        }

        var result = new Dictionary<long, IReadOnlyList<SegmentKeyframe>>(grouped.Count);
        foreach (var (id, rows) in grouped)
        {
            result[id] = rows;
        }

        return result;
    }

    /// <summary>
    /// 區間內、尚無關鍵幀索引的 final 區段編號（背景補建用）。
    ///
    /// <para>
    /// 口徑與 <see cref="SegmentRepository.ListByRange"/> 一樣是重疊式（<c>end_time &gt; from</c>
    /// 或 tmp 段的 <c>start_time &gt;= from</c>），否則跨午夜的段會漏建。
    /// </para>
    /// </summary>
    public IReadOnlyList<long> ListMissingIndexIds(
        long channelId, string stream, DateTime fromUtc, DateTime toUtc)
    {
        return _store.Query(
            """
            SELECT s.id
            FROM segments s
            WHERE s.channel_id = $c AND s.stream = $s
              AND s.status = 'final'
              AND s.start_time < $to
              AND ((s.end_time IS NOT NULL AND s.end_time > $from)
                OR (s.end_time IS NULL AND s.start_time >= $from))
              AND NOT EXISTS (SELECT 1 FROM segment_keyframes k WHERE k.segment_id = s.id)
            ORDER BY s.start_time;
            """,
            static r =>
            {
                var list = new List<long>();
                while (r.Read())
                {
                    list.Add(r.GetInt64(0));
                }

                return list;
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$s", stream);
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
            });
    }
}
