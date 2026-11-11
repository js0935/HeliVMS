using HeliVMS.Recording;
using HeliVMS.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeliVMS.WebApi;

/// <summary>
/// 獨立 WebApi 部署的每小時錄影索引對帳（＋段長修正＋失敗原因回填）。
///
/// <para>
/// 桌面程式（<c>MainWindow</c> 保留迴圈）與 Headless Agent（<c>IntegrityWorker</c>）各自都會跑
/// <see cref="SegmentReconcileCoordinator"/>，但兩者掛的都是 <c>IncludeHostedServices=false</c>
/// 或根本不在這台機器上：純 Web 部署若沒有這一輪，外部刪檔之後用量統計與汰除候選會一直把
/// 已經不存在的錄影算進去（清單顯示已由查詢時對帳救回，索引本身卻沒有被修正）。
/// </para>
/// <para>
/// 同一輪也走段長修正與失敗原因回填（各自的一次性游標掃描）：它們改的都是索引裡的歷史列，
/// 與對帳同一個理由——桌面版修了而純 Web 部署不修，同一份索引在不同部署下呈現不同的世界。
/// 兩支都有探測失敗／庫被鎖的跳過與退讓設計（探測不到 ffprobe 時游標照樣前進）。
/// </para>
/// <para>
/// 延後五分鐘起跑、之後每小時一輪，與保留清理錯開，避免同時搶 SQLite 寫鎖；單輪失敗只記
/// 警告，絕不讓服務停擺——對帳的正確性不值得拿錄影中斷去換。
/// </para>
/// </summary>
public sealed class SegmentReconcileService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>開機後延後啟動，避開與保留清理（同樣每小時）同一時刻搶庫。</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    private readonly SqliteStore _store;
    private readonly ILogger<SegmentReconcileService> _logger;

    public SegmentReconcileService(SqliteStore store, ILogger<SegmentReconcileService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Segment reconcile worker starting (interval {Interval})", Interval);

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
                var report = SegmentReconcileCoordinator.RunConfigured(_store, DateTime.UtcNow);
                if (report.AnyMarked)
                {
                    _logger.LogWarning(
                        "Segment index reconcile marked missing: {Checked} checked, {Missing} missing, {Removed} folders removed, {Unverified} unverified",
                        report.Checked, report.MarkedMissing, report.FoldersRemoved, report.Unverified);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Segment index reconcile failed; will retry next interval");
            }

            // 【突變】純 Web 部署不跑段長修正
            try
            {
                var repair = SegmentDurationRepairCoordinator.RunConfigured(_store, DateTime.UtcNow);
                if (repair.Repaired > 0)
                {
                    _logger.LogInformation(
                        "Segment duration repair: {Probed} probed, {Repaired} repaired, {Skipped} skipped, cursor {Cursor}",
                        repair.Probed, repair.Repaired, repair.Skipped, repair.Cursor);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Segment duration repair failed; will retry next interval");
            }

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
                _logger.LogWarning(ex, "Segment failure reason backfill failed; will retry next interval");
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

        _logger.LogInformation("Segment reconcile worker stopped");
    }
}
