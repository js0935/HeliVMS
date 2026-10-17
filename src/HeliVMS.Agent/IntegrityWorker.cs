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
