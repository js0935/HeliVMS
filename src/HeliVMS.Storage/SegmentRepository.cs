using HeliVMS.Shared.Models;
using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>
/// 錄影區段索引（§15.6：區段記錄與存取）。
/// </summary>
public sealed class SegmentRepository
{
    private readonly SqliteStore _store;

    public SegmentRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>確保頻道存在（不存在則建立，冪等）。</summary>
    public void EnsureChannel(int channelId, string name, string mainUrl)
    {
        _store.Execute(
            """
            INSERT OR IGNORE INTO channels (id, name, main_url, enabled)
            VALUES ($id, $n, $u, 1);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", channelId);
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$u", mainUrl);
            });
    }

    /// <summary>開啟一個錄影區段，回傳區段 ID。</summary>
    public long BeginSegment(
        int channelId,
        string filePath,
        DateTime startUtc,
        string format = "mpegts")
    {
        return _store.Query(
            """
            INSERT INTO segments (channel_id, start_ms, file_path, format, status, created_ms)
            VALUES ($c, $s, $p, $f, $st, $cm);
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
                cmd.Parameters.AddWithValue("$s", SqliteStore.ToUnixMs(startUtc));
                cmd.Parameters.AddWithValue("$p", filePath);
                cmd.Parameters.AddWithValue("$f", format);
                cmd.Parameters.AddWithValue("$st", (int)SegmentStatus.Recording);
                cmd.Parameters.AddWithValue("$cm", SqliteStore.ToUnixMs(DateTime.UtcNow));
            });
    }

    /// <summary>完成錄影區段（寫入結束時間與檔案大小）。</summary>
    public bool CompleteSegment(long id, DateTime endUtc, long sizeBytes)
    {
        _store.Execute(
            """
            UPDATE segments
            SET end_ms = $e, size_bytes = $s, status = $st
            WHERE id = $id AND status = $r;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", SqliteStore.ToUnixMs(endUtc));
                cmd.Parameters.AddWithValue("$s", sizeBytes);
                cmd.Parameters.AddWithValue("$st", (int)SegmentStatus.Completed);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$r", (int)SegmentStatus.Recording);
            });
        return true;
    }

    /// <summary>標記區段異常。</summary>
    public bool MarkCorrupt(long id)
    {
        _store.Execute(
            "UPDATE segments SET status = $st WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$st", (int)SegmentStatus.Corrupt);
                cmd.Parameters.AddWithValue("$id", id);
            });
        return true;
    }

    /// <summary>依時間範圍列出區段（§15.6 回放/時間軸）。</summary>
    public IReadOnlyList<SegmentRecord> ListByRange(int channelId, DateTime fromUtc, DateTime toUtc)
    {
        return _store.Query(
            """
            SELECT id, channel_id, start_ms, end_ms, file_path, size_bytes, format, status
            FROM segments
            WHERE channel_id = $c AND start_ms >= $from AND start_ms <= $to
            ORDER BY start_ms;
            """,
            ReadRecords,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$from", SqliteStore.ToUnixMs(fromUtc));
                cmd.Parameters.AddWithValue("$to", SqliteStore.ToUnixMs(toUtc));
            });
    }

    /// <summary>統計某頻道錄影總量（位元組）。</summary>
    public long GetChannelTotalSize(int channelId)
    {
        return _store.Query(
            """
            SELECT COALESCE(SUM(size_bytes), 0)
            FROM segments
            WHERE channel_id = $c AND status = $st;
            """,
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$st", (int)SegmentStatus.Completed);
            });
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
                StartUtc = SqliteStore.FromUnixMs(reader.GetInt64(2)),
                EndUtc = reader.IsDBNull(3) ? null : SqliteStore.FromUnixMs(reader.GetInt64(3)),
                FilePath = reader.GetString(4),
                SizeBytes = reader.GetInt64(5),
                Format = reader.GetString(6),
                Status = (SegmentStatus)reader.GetInt32(7),
            });
        }

        return list;
    }
}