using HeliVMS.Recording;
using HeliVMS.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeliVMS.Agent;

/// <summary>
/// 錄影完整性自我檢查迴圈（M318，§11.1 寫盤保護、§15.7）。
///
/// <para>
/// 既有的保留清理只保證「裝得下」，不保證「檔案還在、還能播」。磁碟掉線、外部誤刪、
/// 寫入中途斷電造成的中途損毀，都不會有任何錯誤訊息——直到真的要調閱時才發現錄影是壞的，
/// 而現場早已經過去了。此 worker 每小時以 ffprobe 抽查少數已封檔區段，把損壞／遺失寫進稽核。
/// </para>
/// <para>
/// 刻意與 <see cref="RetentionWorker"/> 錯開啟動：兩者都會掃 <c>segments</c> 並寫庫，
/// 同一瞬間起跑會在 SQLite 上互搶寫鎖。開機後延遲幾分鐘再開始，之後每小時一輪。
/// </para>
/// </summary>
internal sealed class IntegrityWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>開機後延後啟動，避開與保留清理（同樣每小時）同一時刻搶庫。</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    private readonly SqliteStore _store;
    private readonly ILogger<IntegrityWorker> _logger;

    public IntegrityWorker(SqliteStore store, ILogger<IntegrityWorker> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Integrity worker starting (interval {Interval})", Interval);

        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var report = MediaIntegrityCoordinator.RunConfigured(_store, DateTime.UtcNow);
                if (report.AnySuspicious)
                {
                    // 查到壞段是事件，不是雜訊：升級為警告，讓服務日誌能直接看出來。
                    _logger.LogWarning(
                        "Integrity check found problems: {Checked} checked, {Corrupt} corrupt, {Missing} missing",
                        report.Checked, report.Corrupt, report.Missing);
                }
            }
            catch (Exception ex)
            {
                // 單輪檢查失敗（ffprobe 暫時不可用、庫被鎖）不該讓服務停擺而中斷錄影。
                _logger.LogWarning(ex, "Integrity check failed; will retry next interval");
            }

            // 段長修正：把改動前寫進索引的「行程活多久」改回檔案自己的長度。與完整性檢查分開，
            // 因為那支的契約是「只觀察、不改狀態」，而這裡要寫庫；分頁游標也不同（主鍵 vs
            // checked_at_utc），混用會讓早就標記過的舊段永遠輪不到。
            try
            {
                var repair = SegmentDurationRepairCoordinator.RunConfigured(_store, DateTime.UtcNow);
                if (repair.Repaired > 0)
                {
                    // 改了既有索引的段長是資料變更，服務日誌要留下來：日後有人問「這段時間軸
                    // 為什麼變短了」，這裡是第一手線索。
                    _logger.LogInformation(
                        "Segment duration repair: {Probed} probed, {Repaired} repaired, {Skipped} skipped, cursor {Cursor}",
                        repair.Probed, repair.Repaired, repair.Skipped, repair.Cursor);
                }
            }
            catch (Exception ex)
            {
                // 單輪修正失敗（ffprobe 暫時不可用、庫被鎖）不該讓服務停擺而中斷錄影。
                _logger.LogWarning(ex, "Segment duration repair failed; will retry next interval");
            }

            // 失敗原因回填：舊版索引的 corrupt 段只有狀態沒有原因，與 recording.stalled 稽核
            // 接起來補上（窗距由稽核的 failures 推導）。一次性掃描，掃完游標寫 -1。
            try
            {
                var backfill = SegmentFailureReasonBackfillCoordinator.RunConfigured(_store, DateTime.UtcNow);
                if (backfill.Backfilled > 0)
                {
                    _logger.LogInformation(
                        "Segment failure reason backfill: {Examined} examined, {Backfilled} backfilled, cursor {Cursor}",
                        backfill.Examined, backfill.Backfilled, backfill.Cursor);
                }
            }
            catch (Exception ex)
            {
                // 單輪回填失敗（庫被鎖）不該讓服務停擺而中斷錄影。
                _logger.LogWarning(ex, "Segment failure reason backfill failed; will retry next interval");
            }

            // 索引對帳：把「索引說有、磁碟上沒有」的段標成 missing。與完整性檢查分開的理由是
            // 兩者的成本形狀完全不同（對帳只 stat、不做 ffprobe），而且對帳要涵蓋整個索引，
            // 完整性抽查是每輪只取樣幾段。服務模式下若不跑這一輪，外部刪檔會讓調閱清單
            // 永遠列出不存在的錄影，而桌面版卻看得到正確結果——兩個部署模式不一致。
            try
            {
                var reconcile = SegmentReconcileCoordinator.RunConfigured(_store, DateTime.UtcNow);
                if (reconcile.AnyMarked)
                {
                    _logger.LogWarning(
                        "Segment index reconcile marked missing: {Checked} checked, {Missing} missing, {Removed} folders removed, {Unverified} unverified",
                        reconcile.Checked, reconcile.MarkedMissing, reconcile.FoldersRemoved, reconcile.Unverified);
                }
            }
            catch (Exception ex)
            {
                // 單輪對帳失敗（磁碟忙碌、庫被鎖）不該讓服務停擺而中斷錄影。
                _logger.LogWarning(ex, "Segment index reconcile failed; will retry next interval");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Integrity worker stopped");
    }
}
