using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>備份執行紀錄（§14.4 異地備援；backup_log 表）。</summary>
public sealed record BackupRunRecord(
    long Id,
    DateTime RunAt,
    string SourceRoot,
    string TargetRoot,
    DateTime? CheckpointUtc,
    int CopiedCount,
    long CopiedBytes,
    int FailedCount,
    string? Detail);

/// <summary>備份紀錄儲存：每次執行寫一列，成功 run 才推進檢查點。</summary>
public sealed class BackupRepository
{
    private readonly SqliteStore _store;

    public BackupRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>寫入一次備份執行紀錄。checkpointUtc 僅成功 run 才傳入（代表已涵蓋的時間點）。</summary>
    public void RecordRun(
        DateTime runAt,
        string sourceRoot,
        string targetRoot,
        DateTime? checkpointUtc,
        int copiedCount,
        long copiedBytes,
        int failedCount,
        string? detail)
    {
        _store.Execute(
            """
            INSERT INTO backup_log (run_at, source_root, target_root, checkpoint_utc, copied_count, copied_bytes, failed_count, detail)
            VALUES ($t, $s, $d, $c, $cc, $cb, $f, $dt);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(runAt));
                cmd.Parameters.AddWithValue("$s", sourceRoot);
                cmd.Parameters.AddWithValue("$d", targetRoot);
                cmd.Parameters.AddWithValue("$c", checkpointUtc is { } ck ? SqliteStore.Iso(ck) : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$cc", copiedCount);
                cmd.Parameters.AddWithValue("$cb", copiedBytes);
                cmd.Parameters.AddWithValue("$f", failedCount);
                cmd.Parameters.AddWithValue("$dt", detail is null ? (object)DBNull.Value : detail);
            });
    }

    /// <summary>最後成功檢查點（同源→同目標）；無則回傳 null。</summary>
    public DateTime? LastCheckpoint(string sourceRoot, string targetRoot)
    {
        return _store.Query(
            """
            SELECT MAX(checkpoint_utc) FROM backup_log
            WHERE source_root = $s AND target_root = $t AND checkpoint_utc IS NOT NULL;
            """,
            static r =>
            {
                if (!r.Read() || r.IsDBNull(0))
                {
                    return (DateTime?)null;
                }

                return SqliteStore.FromIso(r.GetString(0));
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$s", sourceRoot);
                cmd.Parameters.AddWithValue("$t", targetRoot);
            });
    }

    /// <summary>最近備份紀錄（新→舊；目標目錄可過濾）。</summary>
    public IReadOnlyList<BackupRunRecord> ListRuns(string? targetRoot = null, int take = 30)
    {
        var where = targetRoot is null ? "" : " WHERE target_root = $t";
        var sql = $"""
            SELECT id, run_at, source_root, target_root, checkpoint_utc, copied_count, copied_bytes, failed_count, detail
            FROM backup_log{where}
            ORDER BY run_at DESC
            LIMIT {Math.Max(1, take)};
            """;
        return _store.Query(
            sql,
            ReadRecords,
            targetRoot is null ? null : (Action<SqliteCommand>)(cmd => cmd.Parameters.AddWithValue("$t", targetRoot)));
    }

    private static IReadOnlyList<BackupRunRecord> ReadRecords(SqliteDataReader reader)
    {
        var list = new List<BackupRunRecord>();
        while (reader.Read())
        {
            list.Add(new BackupRunRecord(
                reader.GetInt64(0),
                SqliteStore.FromIso(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : SqliteStore.FromIso(reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt64(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return list;
    }
}