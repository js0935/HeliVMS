using HeliVMS.Recording;
using HeliVMS.Shared;
using HeliVMS.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeliVMS.Agent;

/// <summary>
/// 保留清理迴圈（M316）。
///
/// Headless Agent 過去只錄不刪——沒有配額汰除、沒有暫存清理、沒有快照汰除、沒有中繼資料
/// 保管期限。以 Windows 服務（無桌面、無 WebApi 主機）模式部署時，錄影檔與警報／事件錄音／
/// 稽核日誌會無上限成長，直到磁碟被寫爆才被發現，而日誌上不會有任何錯誤。
///
/// 此 worker 每小時與桌面（<c>MainWindow</c>）走同一條清理路徑（<see cref="RetentionCoordinator"/>），
/// 設定每圈重讀，切換錄影磁碟後下一圈即改認新根（舊根仍可汰除，見 <c>RetentionService</c>）。
/// 清理失敗只記錄並於下一圈重試，不讓服務因單圈例外而停止錄影。
/// </summary>
internal sealed class RetentionWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly SqliteStore _store;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(SqliteStore store, ILogger<RetentionWorker> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Retention worker starting (interval {Interval})", Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunOnce(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // 單圈清理失敗（磁碟暫時不可用、權限等）不該讓服務停擺而中斷錄影。
                _logger.LogWarning(ex, "Retention sweep failed; will retry next interval");
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

        _logger.LogInformation("Retention worker stopped");
    }

    private void RunOnce(DateTime nowUtc)
    {
        var dataRoot = DataRootLayout.ResolveRoot(v => Environment.GetEnvironmentVariable(v));

        // 錄影根目錄與桌面、WebApi 共用同一條規則：以設定中心寫進共用資料庫的 recording.root
        // 為準，未設定時才退回環境變數與「避開系統碟」的推導值。設定每圈重讀。
        var sweep = RetentionCoordinator.RunConfigured(_store, dataRoot, nowUtc);

        if (sweep.AnythingRemoved)
        {
            _logger.LogInformation(
                "Retention sweep: {Segments} segments, {Tmp} tmp, {Snapshots} snapshots, {Alarms} alarms, {Audio} audio, {Audit} audit",
                sweep.DeletedSegments, sweep.PurgedTmp, sweep.SnapshotDeletedFiles,
                sweep.AlarmPurged, sweep.AudioPurged, sweep.AuditPurged);
        }

        // 磁碟滿策略第一階段（§11.1）：>85% 預警、低於寫入門檻告警。等級改變才寫稽核
        //（與桌面共用入口），並在這裡留下分級的 log 讓維運不必查稽核就看得到。
        var recordingsRoot = RecordingStorage.ResolveConfiguredRoot(
            dataRoot, new SettingsRepository(_store).Get(RecordingStorage.RootSettingKey));
        var level = DiskPressureCoordinator.RunConfigured(_store, recordingsRoot, nowUtc);
        if (level == DiskPressureLevel.Critical)
        {
            _logger.LogError(
                "錄影磁碟可用空間低於寫入門檻（{Root}）：已停止開新錄影，請清理或擴充磁碟。",
                recordingsRoot);
        }
        else if (level == DiskPressureLevel.Warning)
        {
            _logger.LogWarning("錄影磁碟已用超過 85%（{Root}），請預先清理或擴充。", recordingsRoot);
        }
    }
}
