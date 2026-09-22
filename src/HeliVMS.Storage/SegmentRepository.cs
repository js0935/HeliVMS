using HeliVMS.Shared.Models;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// 錄影區段索引（§4 segments 表；§15.6 區段記錄與存取）。
/// 時間戳一律 ISO8601 UTC（TEXT），狀態以文字暫存 tmp／final／corrupt。
/// </summary>
public sealed class SegmentRepository
{
    private readonly SqliteStore _store;

    public SegmentRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>開啟一個錄影區段（暫存檔，status=tmp），回傳區段 ID。</summary>
    public long BeginSegment(int channelId, string stream, string filePath, DateTime startUtc)
    {
        return _store.Query(
            """
            INSERT INTO segments (channel_id, stream, start_time, file_path, status)
            VALUES ($c, $s, $st, $p, 'tmp');
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$s", stream);
                cmd.Parameters.AddWithValue("$st", SqliteStore.Iso(startUtc));
                cmd.Parameters.AddWithValue("$p", filePath);
            });
    }

    /// <summary>完成錄影區段（status=final，含 SHA-256 與時長）。</summary>
    public void CompleteSegment(long id, DateTime endUtc, long sizeBytes, double durationSec, string sha256)
    {
        _store.Execute(
            """
            UPDATE segments
            SET end_time = $e, size_bytes = $s, duration_sec = $d, sha256 = $h, status = 'final'
            WHERE id = $id AND status = 'tmp';
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", SqliteStore.Iso(endUtc));
                cmd.Parameters.AddWithValue("$s", sizeBytes);
                cmd.Parameters.AddWithValue("$d", durationSec);
                cmd.Parameters.AddWithValue("$h", sha256);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>標記區段異常（status=corrupt）。</summary>
    public void MarkCorrupt(long id)
    {
        _store.Execute(
            "UPDATE segments SET status = 'corrupt' WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>依時間範圍列出區段（§3.4 時間軸回放）。</summary>
    public IReadOnlyList<SegmentRecord> ListByRange(int channelId, string stream, DateTime fromUtc, DateTime toUtc)
    {
        return _store.Query(
            """
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256
            FROM segments
            WHERE channel_id = $c AND stream = $s
              AND start_time >= $from AND start_time <= $to
            ORDER BY start_time;
            """,
            ReadRecords,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$s", stream);
                cmd.Parameters.AddWithValue("$from", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.Iso(toUtc));
            });
    }

    /// <summary>統計某頻道錄影總量（位元組）。</summary>
    public long GetChannelTotalSize(int channelId)
    {
        return _store.Query(
            """
            SELECT COALESCE(SUM(size_bytes), 0)
            FROM segments WHERE channel_id = $c AND status = 'final';
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd => cmd.Parameters.AddWithValue("$c", channelId));
    }

    /// <summary>統計全部頻道錄影總量（位元組，final 限定）。</summary>
    public long GetTotalUsage()
    {
        return _store.Query(
            """
            SELECT COALESCE(SUM(size_bytes), 0)
            FROM segments WHERE status = 'final';
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            });
    }

    /// <summary>依開始時間取出最舊之 final 區段（配額清理用）。</summary>
    public IReadOnlyList<SegmentRecord> ListOldestFinal(int take)
    {
        return _store.Query(
            $"""
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256
            FROM segments
            WHERE status = 'final'
            ORDER BY start_time
            LIMIT {Math.Max(1, take)};
            """,
            ReadRecords);
    }

    /// <summary>列出早於截止點之最舊 final 區段（保留期清理用；start_time 由舊至新）。</summary>
    public IReadOnlyList<SegmentRecord> ListRetired(DateTime cutoffUtc, int take)
    {
        return _store.Query(
            $"""
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256
            FROM segments
            WHERE status = 'final' AND start_time < $cut
            ORDER BY start_time
            LIMIT {Math.Max(1, take)};
            """,
            ReadRecords,
            cmd => cmd.Parameters.AddWithValue("$cut", SqliteStore.Iso(cutoffUtc)));
    }

    /// <summary>刪除區段記錄（檔案移除由配額策略負責）。</summary>
    public void Delete(long id)
    {
        _store.Execute(
            "DELETE FROM segments WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>依時長計算統計（供 §15 配額/RPO 報告）。</summary>
    public IReadOnlyList<SegmentRecord> ListFinal(int channelId)
    {
        return _store.Query(
            """
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256
            FROM segments
            WHERE channel_id = $c AND status = 'final'
            ORDER BY start_time;
            """,
            ReadRecords,
            cmd => cmd.Parameters.AddWithValue("$c", channelId));
    }

    /// <summary>全部 final 區段（開始時間由舊至新；§14.4 異地備援與統計用）。</summary>
    public IReadOnlyList<SegmentRecord> ListAllFinal()
    {
        return _store.Query(
            """
            SELECT id, channel_id, stream, start_time, end_time, file_path,
                   size_bytes, duration_sec, status, sha256
            FROM segments
            WHERE status = 'final'
            ORDER BY start_time;
            """,
            ReadRecords);
    }

    private static IReadOnlyList<SegmentRecord> ReadRecords(SqliteDataReader reader)
    {
        var list = new List<SegmentRecord>();
        while (reader.Read())
        {
            list.Add(new SegmentRecord
            {
                Id = reader.GetInt64(0),
                ChannelId = reader.GetInt32(1),
                Stream = reader.GetString(2),
                StartUtc = SqliteStore.FromIso(reader.GetString(3)),
                EndUtc = reader.IsDBNull(4) ? null : SqliteStore.FromIso(reader.GetString(4)),
                FilePath = reader.GetString(5),
                SizeBytes = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                DurationSec = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                Status = ParseStatus(reader.GetString(8)),
                Sha256 = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }

        return list;
    }

    private static SegmentStatus ParseStatus(string text) => text switch
    {
        "tmp" => SegmentStatus.Temporary,
        "final" => SegmentStatus.Final,
        "corrupt" => SegmentStatus.Corrupt,
        _ => SegmentStatus.Temporary,
    };
}