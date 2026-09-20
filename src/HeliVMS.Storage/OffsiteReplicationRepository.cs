using System.Globalization;

namespace HeliVMS.Storage;

/// <summary>異地備援複製工作存取（offsite_jobs 表；M55）。</summary>
public sealed class OffsiteReplicationRepository
{
    private readonly SqliteStore _store;

    public OffsiteReplicationRepository(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>新增一筆複製工作，回傳新 ID。</summary>
    public long Add(string sourcePath, string destinationPath, int intervalMinutes, bool enabled)
    {
        _store.Execute(
            """
            INSERT INTO offsite_jobs (source_path, destination_path, interval_minutes, enabled, consecutive_failures, created_at)
            VALUES ($s, $d, $i, $e, 0, $c);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$s", sourcePath);
                cmd.Parameters.AddWithValue("$d", destinationPath);
                cmd.Parameters.AddWithValue("$i", intervalMinutes);
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$c",
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            });

        return _store.Query(
            "SELECT last_insert_rowid();",
            static r =>
            {
                r.Read();
                return r.GetInt64(0);
            });
    }

    /// <summary>新增或更新一筆複製工作（依來源＋目標判重）。</summary>
    public long Upsert(string sourcePath, string destinationPath, int intervalMinutes, bool enabled)
    {
        var existing = List().FirstOrDefault(j =>
            string.Equals(j.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(j.DestinationPath, destinationPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Update(existing.Id, destinationPath: destinationPath, intervalMinutes: intervalMinutes, enabled: enabled);
            return existing.Id;
        }

        return Add(sourcePath, destinationPath, intervalMinutes, enabled);
    }

    /// <summary>更新一筆複製工作。</summary>
    public void Update(
        long id,
        string? sourcePath = null,
        string? destinationPath = null,
        int? intervalMinutes = null,
        bool? enabled = null)
    {
        _store.Execute(
            """
            UPDATE offsite_jobs
            SET source_path = COALESCE($s, source_path),
                destination_path = COALESCE($d, destination_path),
                interval_minutes = COALESCE($i, interval_minutes),
                enabled = COALESCE($e, enabled)
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$s", (object?)sourcePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$d", (object?)destinationPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$i", (object?)intervalMinutes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$e", (object?)(enabled.HasValue ? (enabled.Value ? 1 : 0) : (int?)null) ?? DBNull.Value);
            });
    }

    /// <summary>記錄一次執行結果。</summary>
    public void SetRunResult(long id, bool succeeded, string? detail)
    {
        _store.Execute(
            """
            UPDATE offsite_jobs
            SET last_run_utc = $t, last_result = $r, last_error = $e,
                consecutive_failures = CASE WHEN $s = 1 THEN 0 ELSE consecutive_failures + 1 END
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$r", succeeded ? "OK" : "FAILED");
                cmd.Parameters.AddWithValue("$e", (object?)(succeeded ? null : detail) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$s", succeeded ? 1 : 0);
            });
    }

    /// <summary>全部複製工作。</summary>
    public IReadOnlyList<OffsiteJob> List()
    {
        return _store.Query(
            """
            SELECT id, source_path, destination_path, interval_minutes, enabled,
                   last_run_utc, last_result, last_error, consecutive_failures, created_at
            FROM offsite_jobs
            ORDER BY id;
            """,
            static r =>
            {
                var list = new List<OffsiteJob>();
                while (r.Read())
                {
                    list.Add(new OffsiteJob(
                        r.GetInt64(0),
                        r.GetString(1),
                        r.GetString(2),
                        r.GetInt32(3),
                        r.GetInt32(4) != 0,
                        r.IsDBNull(5) ? null : DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        r.IsDBNull(6) ? null : r.GetString(6),
                        r.IsDBNull(7) ? null : r.GetString(7),
                        r.GetInt32(8),
                        r.IsDBNull(9) ? null : DateTime.Parse(r.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
                }

                return list;
            });
    }

    /// <summary>啟用中的複製工作。</summary>
    public IReadOnlyList<OffsiteJob> ListEnabled()
    {
        return _store.Query(
            """
            SELECT id, source_path, destination_path, interval_minutes, enabled,
                   last_run_utc, last_result, last_error, consecutive_failures, created_at
            FROM offsite_jobs
            WHERE enabled = 1
            ORDER BY id;
            """,
            static r =>
            {
                var list = new List<OffsiteJob>();
                while (r.Read())
                {
                    list.Add(new OffsiteJob(
                        r.GetInt64(0),
                        r.GetString(1),
                        r.GetString(2),
                        r.GetInt32(3),
                        r.GetInt32(4) != 0,
                        r.IsDBNull(5) ? null : DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        r.IsDBNull(6) ? null : r.GetString(6),
                        r.IsDBNull(7) ? null : r.GetString(7),
                        r.GetInt32(8),
                        r.IsDBNull(9) ? null : DateTime.Parse(r.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
                }

                return list;
            });
    }

    /// <summary>刪除一筆複製工作。</summary>
    public void Delete(long id)
    {
        _store.Execute(
            "DELETE FROM offsite_jobs WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }
}