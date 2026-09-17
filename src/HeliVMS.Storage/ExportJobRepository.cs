using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>匯出工作記錄（§14.3(2) 匯出中心；export_jobs 表）。</summary>
public sealed record ExportJobRecord(
    long Id,
    int ChannelId,
    string Stream,
    DateTime StartUtc,
    DateTime EndUtc,
    string Status,
    string? OutputPath,
    long? FileSizeBytes,
    string? Sha256,
    string? Error,
    DateTime CreatedUtc,
    DateTime? StartedUtc,
    DateTime? FinishedUtc);

/// <summary>匯出工作佇列（queued→running→done／failed）。</summary>
public sealed class ExportJobRepository
{
    private readonly SqliteStore _store;

    public ExportJobRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>加入匯出工作，回傳工作 ID。</summary>
    public long Enqueue(int channelId, string stream, DateTime fromUtc, DateTime toUtc)
    {
        return _store.Query(
            """
            INSERT INTO export_jobs (channel_id, stream, start_time, end_time, status)
            VALUES ($c, $s, $f, $t, 'queued');
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
                cmd.Parameters.AddWithValue("$f", SqliteStore.Iso(fromUtc));
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(toUtc));
            });
    }

    /// <summary>全部工作（新→舊）。</summary>
    public IReadOnlyList<ExportJobRecord> List()
    {
        return _store.Query(
            """
            SELECT id, channel_id, stream, start_time, end_time, status, output_path,
                   file_size_bytes, sha256, error, created_at, started_at, finished_at
            FROM export_jobs
            ORDER BY id DESC;
            """,
            ReadRecords);
    }

    /// <summary>待處理工作（舊→新）。</summary>
    public IReadOnlyList<ExportJobRecord> ListQueued()
    {
        return _store.Query(
            """
            SELECT id, channel_id, stream, start_time, end_time, status, output_path,
                   file_size_bytes, sha256, error, created_at, started_at, finished_at
            FROM export_jobs
            WHERE status = 'queued'
            ORDER BY id ASC;
            """,
            ReadRecords);
    }

    /// <summary>單筆工作。</summary>
    public ExportJobRecord? Get(long id)
    {
        return _store.Query(
            """
            SELECT id, channel_id, stream, start_time, end_time, status, output_path,
                   file_size_bytes, sha256, error, created_at, started_at, finished_at
            FROM export_jobs
            WHERE id = $id;
            """,
            ReadRecords,
            cmd => cmd.Parameters.AddWithValue("$id", id))
            .FirstOrDefault();
    }

    /// <summary>標記開始處理。</summary>
    public void MarkRunning(long id, DateTime startedUtc)
    {
        _store.Execute(
            "UPDATE export_jobs SET status = 'running', started_at = $s WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$s", SqliteStore.Iso(startedUtc));
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>完成：status=done＋輸出資訊。</summary>
    public void SetResult(long id, string outputPath, string sha256, long sizeBytes, DateTime finishedUtc)
    {
        _store.Execute(
            """
            UPDATE export_jobs
            SET status = 'done', output_path = $o, sha256 = $h, file_size_bytes = $s, error = NULL, finished_at = $f
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$o", outputPath);
                cmd.Parameters.AddWithValue("$h", sha256);
                cmd.Parameters.AddWithValue("$s", sizeBytes);
                cmd.Parameters.AddWithValue("$f", SqliteStore.Iso(finishedUtc));
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>失敗：status=failed＋錯誤訊息。</summary>
    public void SetError(long id, string error, DateTime finishedUtc)
    {
        _store.Execute(
            """
            UPDATE export_jobs
            SET status = 'failed', error = $e, finished_at = $f
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", error);
                cmd.Parameters.AddWithValue("$f", SqliteStore.Iso(finishedUtc));
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    /// <summary>刪除單一工作（含完成檔可另行移除）。</summary>
    public void Delete(long id)
    {
        _store.Execute(
            "DELETE FROM export_jobs WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>清空已完成／失敗且早於指定時間的舊紀錄，回傳刪除數。</summary>
    public int PurgeFinished(DateTime beforeUtc)
    {
        return _store.Query(
            """
            DELETE FROM export_jobs
            WHERE status IN ('done', 'failed') AND finished_at < $b;
            SELECT changes();
            """,
            static r =>
            {
                r.Read();
                return r.GetInt32(0);
            },
            cmd => cmd.Parameters.AddWithValue("$b", SqliteStore.Iso(beforeUtc)));
    }

    private static IReadOnlyList<ExportJobRecord> ReadRecords(SqliteDataReader reader)
    {
        var list = new List<ExportJobRecord>();
        while (reader.Read())
        {
            list.Add(new ExportJobRecord(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                SqliteStore.FromIso(reader.GetString(3)),
                SqliteStore.FromIso(reader.GetString(4)),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                SqliteStore.FromIso(reader.GetString(10)),
                reader.IsDBNull(11) ? null : SqliteStore.FromIso(reader.GetString(11)),
                reader.IsDBNull(12) ? null : SqliteStore.FromIso(reader.GetString(12))));
        }

        return list;
    }
}