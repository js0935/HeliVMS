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
    private readonly AuditLogRepository _audit;

    public ExportJobRepository(SqliteStore store)
    {
        _store = store;
        _audit = new AuditLogRepository(store);
    }

    /// <summary>加入匯出工作，回傳工作 ID。</summary>
    public long Enqueue(int channelId, string stream, DateTime fromUtc, DateTime toUtc)
    {
        var id = _store.Query(
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
        _audit.Record("system", "export.enqueue", AuditCategories.Export,
            targetType: "export_job", targetId: id,
            detail: $"ch={channelId} {SqliteStore.Iso(fromUtc)}..{SqliteStore.Iso(toUtc)}");
        return id;
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
        _audit.Record("system", "export.done", AuditCategories.Export,
            targetType: "export_job", targetId: id, detail: $"sha256={sha256}", occurredAtUtc: finishedUtc);
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

    /// <summary>失敗→重新排入佇列（清除錯誤與時戳），回傳原本的開始時間窗。</summary>
    public void Retry(long id)
    {
        _store.Execute(
            """
            UPDATE export_jobs
            SET status = 'queued', error = NULL, started_at = NULL, finished_at = NULL
            WHERE id = $id AND status = 'failed';
            """,
            cmd => cmd.Parameters.AddWithValue("$id", id));
        _audit.Record("system", "export.retry", AuditCategories.Export,
            targetType: "export_job", targetId: id, detail: "失敗後重新排入佇列");
    }

    /// <summary>啟動時把「執行中」的匯出工作結算成 failed（行程孤兒對帳）。</summary>
    /// <param name="nowUtc">結算時刻（finished_at 與稽核的時間）。</param>
    /// <param name="reason">寫進 error 欄的說明（會原樣顯示在匯出中心）。</param>
    /// <returns>結算的工作數。</returns>
    /// <remarks>
    /// <para>
    /// 匯出跑在本行程內：行程剛啟動時，索引裡任何 running 工作都是上個行程的孤兒
    /// （程式於匯出途中被砍時，ffmpeg 一起死掉，工作永遠不會有 finished_at——
    /// 實測 11-01 的 running 掛到 11-12，匯出中心列著一筆永遠不會完成的「執行中」）。
    /// 刻意<b>只在啟動時</b>呼叫：匯出中心開啟時行程可能正有匯出在跑，那時結算會殺掉
    /// 活著的工作。
    /// </para>
    /// <para>
    /// 結算成 failed 而不是重新排入佇列：匯出是長時間的 ffmpeg 工作，自動重跑可能與
    /// 操作員當下的操作衝突；狀態 failed 讓「重試」按鈕（<see cref="Retry"/>）接手，
    /// 重排的決定留給操作員。
    /// </para>
    /// </remarks>
    public int FailRunningAsOrphaned(DateTime nowUtc, string reason)
    {
        var orphanIds = _store.Query(
            "SELECT id FROM export_jobs WHERE status = 'running' ORDER BY id;",
            r =>
            {
                var ids = new List<long>();
                while (r.Read())
                {
                    ids.Add(r.GetInt64(0));
                }

                return ids;
            });

        foreach (var id in orphanIds)
        {
            SetError(id, reason, nowUtc);
            _audit.Record("system", "export.orphan", AuditCategories.Export,
                targetType: "export_job", targetId: id,
                detail: "程式於匯出途中結束，工作結算為失敗", occurredAtUtc: nowUtc);
        }

        return orphanIds.Count;
    }

    /// <summary>取消排隊中的工作（QC_Live 的 <c>ctrlRecBackupTask</c> 對齊）。</summary>
    /// <remarks>
    /// 只取消 <c>queued</c>：正在執行的工作由「停止」中斷整批（ffmpeg 的 ct 生效），
    /// 已完成／已失敗的工作沒有可取消的東西。取消的工作保留在清單上（狀態 <c>cancelled</c>）——
    /// 操作員看得到「這筆曾經排過、後來取消」，也才想得到要重新排（Delete 才會移除）。
    /// </remarks>
    public bool Cancel(long id, string? actor = null)
    {
        var affected = _store.Query<int>(
            """
            UPDATE export_jobs
            SET status = 'cancelled', error = 'cancelled', finished_at = $f
            WHERE id = $id AND status = 'queued';
            SELECT changes();
            """,
            static r => r.Read() ? r.GetInt32(0) : 0,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                // 與 MarkRunning/SetResult/SetError 同一口徑：一律走 Iso。
                // 裸綁 DateTime 會產生另一種文字格式，跟 PurgeFinished 的 Iso 截止值
                // 做字串比較時詞序隨時區翻轉（本機 UTC+8 綠、CI UTC 紅的那次就是它）。
                cmd.Parameters.AddWithValue("$f", SqliteStore.Iso(DateTime.UtcNow));
            });

        if (affected > 0)
        {
            _audit.Record(actor, "export.cancel", AuditCategories.Export,
                targetType: "export_job", targetId: id, detail: "排隊中的匯出工作被取消");
        }

        return affected > 0;
    }

    /// <summary>刪除單一工作（含完成檔可另行移除）。</summary>
    public void Delete(long id)
    {
        _store.Execute(
            "DELETE FROM export_jobs WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>清空已完成／失敗／取消且早於指定時間的舊紀錄，回傳刪除數。</summary>
    public int PurgeFinished(DateTime beforeUtc)
    {
        return _store.Query(
            """
            DELETE FROM export_jobs
            WHERE status IN ('done', 'failed', 'cancelled') AND finished_at < $b;
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