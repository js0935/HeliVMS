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
    /// 區間內、尚無關鍵幀索引的 final 區段編號（背景補建用）。
    ///
    /// <para>
    /// 口徑與 <see cref="SegmentRepository.ListByRange"/> 一樣是重疊式（<c>end_time &gt; from</c>
    /// 或 tmp 段的 <c>start_time &gt;= from</c>），否則跨午夜的段會漏建。
    /// </para>
    /// </summary>
    public IReadOnlyList<long> ListMissingIndexIds(
        int channelId, string stream, DateTime fromUtc, DateTime toUtc)
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
