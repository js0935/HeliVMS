using System.Globalization;

namespace HeliVMS.Storage;

/// <summary>邊緣補抓 job 狀態（M94，§14.7 #10）：Pending→Downloading→Done／Failed↺重試。</summary>
public enum EdgeBackfillStatus
{
    Pending,
    Downloading,
    Done,
    Failed,
}

/// <summary>邊緣補抓回灌 job（M94）：依 (device, channel) 的時段，把設備 SD 側錄補抓回主庫。</summary>
public sealed record EdgeBackfillJob(
    long Id,
    int DeviceId,
    int ChannelId,
    DateTime StartUtc,
    DateTime EndUtc,
    EdgeBackfillStatus Status,
    int Attempts,
    DateTime? NextAttemptUtc,
    string? LastError,
    DateTime? CompletedUtc);

/// <summary>回灌執行結果（M94）。</summary>
public sealed record EdgeBackfillResult(bool Success, string? Error = null);

/// <summary>實際執行「補抓」的 runner 抽象（M94）：實作以 ffmpeg http 拉流 pull 回主庫。</summary>
public interface IEdgeBackfillRunner
{
    ValueTask<EdgeBackfillResult> RunAsync(EdgeBackfillJob job, CancellationToken ct);
}

/// <summary>純函式：產生 ffmpeg 補抓指令（M94）——`-ss` 前移快速 seek、`-t` 定長、`-c copy` 免轉碼。</summary>
public static class EdgeBackfillCommandFactory
{
    public static string[] BuildArguments(string sourceUri, string destinationPath, DateTime startUtc, DateTime endUtc)
    {
        var delta = endUtc - startUtc;
        return
        [
            "-y",
            "-nostdin",
            "-ss", startUtc.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
            "-t", delta.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", sourceUri,
            "-c", "copy",
            destinationPath,
        ];
    }
}

/// <summary>邊緣補抓 job 倉儲（M94，SQLite v32）：Create（重疊去重）／QueryDue／狀態推進。</summary>
public sealed class EdgeBackfillJobRepository
{
    private readonly SqliteStore _store;

    public EdgeBackfillJobRepository(SqliteStore store) => _store = store;

    private static EdgeBackfillStatus ParseStatus(string s) => s switch
    {
        "Pending" => EdgeBackfillStatus.Pending,
        "Downloading" => EdgeBackfillStatus.Downloading,
        "Done" => EdgeBackfillStatus.Done,
        _ => EdgeBackfillStatus.Failed,
    };

    public bool HasConflict(int deviceId, DateTime startUtc, DateTime endUtc)
    {
        return _store.Query("SELECT count(*) FROM edge_backfill_jobs WHERE device_id = $d AND status IN ('Pending', 'Downloading', 'Done') AND start_utc < $end AND end_utc > $start", r => r.Read() ? r.GetInt64(0) != 0 : false, cmd =>
        {
            cmd.Parameters.AddWithValue("$d", deviceId);
            cmd.Parameters.AddWithValue("$start", SqliteStore.Iso(startUtc));
            cmd.Parameters.AddWithValue("$end", SqliteStore.Iso(endUtc));
        });
    }

    public long Create(int deviceId, int channelId, DateTime startUtc, DateTime endUtc)
    {
        if (HasConflict(deviceId, startUtc, endUtc))
        {
            return -1;
        }

        _store.Execute(
            """
            INSERT INTO edge_backfill_jobs (device_id, channel_id, start_utc, end_utc, status, attempts)
            VALUES ($d, $ch, $start, $end, 'Pending', 0);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$d", deviceId);
                cmd.Parameters.AddWithValue("$ch", channelId);
                cmd.Parameters.AddWithValue("$start", SqliteStore.Iso(startUtc));
                cmd.Parameters.AddWithValue("$end", SqliteStore.Iso(endUtc));
            });
        return _store.Query<long>("SELECT last_insert_rowid();", r => r.Read() ? r.GetInt64(0) : 0);
    }

    public IReadOnlyList<EdgeBackfillJob> QueryDue(DateTime nowUtc)
    {
        return _store.Query(
            """
            SELECT id, device_id, channel_id, start_utc, end_utc, status, attempts, next_attempt_utc, last_error, completed_utc
            FROM edge_backfill_jobs
            WHERE status IN ('Pending', 'Failed') AND (next_attempt_utc IS NULL OR next_attempt_utc <= $now)
            ORDER BY start_utc, id;
            """,
            ReadJobs,
            cmd => cmd.Parameters.AddWithValue("$now", SqliteStore.Iso(nowUtc)));
    }

    public IReadOnlyList<EdgeBackfillJob> QueryAll(int? deviceId = null)
    {
        var sql = "SELECT id, device_id, channel_id, start_utc, end_utc, status, attempts, next_attempt_utc, last_error, completed_utc FROM edge_backfill_jobs";
        if (deviceId is not null)
        {
            sql += " WHERE device_id = $d";
        }

        sql += " ORDER BY id;";
        return _store.Query(sql, ReadJobs, cmd =>
        {
            if (deviceId is { } d) cmd.Parameters.AddWithValue("$d", d);
        });
    }

    private static IReadOnlyList<EdgeBackfillJob> ReadJobs(System.Data.Common.DbDataReader r)
    {
        var jobs = new List<EdgeBackfillJob>();
        while (r.Read())
        {
            jobs.Add(new EdgeBackfillJob(
                r.GetInt64(0),
                r.GetInt32(1),
                r.GetInt32(2),
                SqliteStore.FromIso(r.GetString(3)),
                SqliteStore.FromIso(r.GetString(4)),
                ParseStatus(r.GetString(5)),
                r.GetInt32(6),
                r.IsDBNull(7) ? null : SqliteStore.FromIso(r.GetString(7)),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : SqliteStore.FromIso(r.GetString(9))));
        }

        return jobs;
    }

    public void MarkDownloading(long id) =>
        _store.Execute("UPDATE edge_backfill_jobs SET status = 'Downloading' WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));

    public void MarkDone(long id, DateTime nowUtc) =>
        _store.Execute(
            """
            UPDATE edge_backfill_jobs SET status = 'Done', completed_utc = $t, last_error = NULL
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(nowUtc));
            });

    public void MarkFailed(long id, string? error, DateTime nextAttemptUtc) =>
        _store.Execute(
            """
            UPDATE edge_backfill_jobs
            SET status = 'Failed', attempts = attempts + 1, last_error = $e, next_attempt_utc = $t
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$e", error ?? string.Empty);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(nextAttemptUtc));
            });
}

/// <summary>邊緣補抓執行器（M94）：一次 ExecuteOnce 取到期 job、有限並行、成敗推進、失敗退避。</summary>
public sealed class EdgeBackfillExecutor
{
    private readonly EdgeBackfillJobRepository _jobs;
    private readonly IEdgeBackfillRunner _runner;
    private readonly int _maxConcurrent;
    private readonly TimeSpan _retryInterval;

    public EdgeBackfillExecutor(EdgeBackfillJobRepository jobs, IEdgeBackfillRunner runner, int maxConcurrent, TimeSpan retryInterval)
    {
        _jobs = jobs;
        _runner = runner;
        _maxConcurrent = maxConcurrent;
        _retryInterval = retryInterval;
    }

    public async ValueTask<EdgeBackfillRun> ExecuteOnceAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var due = _jobs.QueryDue(nowUtc);
        if (due.Count == 0)
        {
            return new EdgeBackfillRun(Array.Empty<EdgeBackfillRunItem>());
        }

        using var gate = new SemaphoreSlim(_maxConcurrent);
        var items = new List<EdgeBackfillRunItem>(due.Count);
        await Task.WhenAll(due.Select(async job =>
        {
            await gate.WaitAsync(ct);
            try
            {
                _jobs.MarkDownloading(job.Id);
                var result = await _runner.RunAsync(job, ct);
                if (result.Success)
                {
                    _jobs.MarkDone(job.Id, nowUtc);
                    items.Add(new EdgeBackfillRunItem(job.Id, true, null));
                }
                else
                {
                    _jobs.MarkFailed(job.Id, result.Error, nowUtc + _retryInterval);
                    items.Add(new EdgeBackfillRunItem(job.Id, false, result.Error));
                }
            }
            finally
            {
                gate.Release();
            }
        }));
        return new EdgeBackfillRun(items);
    }
}

/// <summary>單次執行摘要（M94）。</summary>
public sealed record EdgeBackfillRunItem(long JobId, bool Success, string? Error);

/// <summary>單次執行摘要（M94）。</summary>
public sealed record EdgeBackfillRun(IReadOnlyList<EdgeBackfillRunItem> Items);