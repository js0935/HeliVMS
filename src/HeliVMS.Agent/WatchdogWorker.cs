using System.Diagnostics;
using HeliVMS.Recording;
using HeliVMS.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeliVMS.Agent;

/// <summary>
/// 錄影看門狗（M320，§11.1）。
///
/// <para>
/// 以 Windows 服務（背景、無人看管、開機即起）監看錄影心跳：錄影行程每 30 秒寫一次心跳，
/// 這個 worker 每分鐘讀一次，超過 2 分鐘沒更新就代表錄影行程卡住或已經崩潰——以前這種情況
/// 不會有任何訊息，直到有人要調閱才發現好幾天沒錄。
/// </para>
///
/// <para>
/// 判定與轉折去重／稽核在 <see cref="RecordingWatchdogMonitor"/>（可單獨測試）；這裡只負責
/// 排程、分級 log，以及（若管理員設了 <c>recording.watchdog.restart_exe</c>）重新拉起錄影程式。
/// </para>
/// </summary>
internal sealed class WatchdogWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);

    private readonly RecordingWatchdogMonitor _monitor;
    private readonly ILogger<WatchdogWorker> _logger;

    public WatchdogWorker(SqliteStore store, ILogger<WatchdogWorker> logger)
    {
        _logger = logger;
        var dataRoot = DataRootLayout.ResolveRoot(v => Environment.GetEnvironmentVariable(v));
        _monitor = new RecordingWatchdogMonitor(
            dataRoot,
            new AuditLogRepository(store),
            restartExe: () => new SettingsRepository(store).Get(RecordingWatchdogMonitor.RestartExeKey),
            launch: Launch);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Watchdog worker starting (interval {Interval})", Interval);

        // 開機先等一段：服務常比桌面程式早啟動，立刻判會把「還沒開始錄」誤報成停擺。
        if (await DelayAsync(StartupGrace, stoppingToken).ConfigureAwait(false))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunOnce(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // 看門狗自己出錯不該讓服務停擺，也不該影響錄影；下一輪再試。
                _logger.LogWarning(ex, "Watchdog evaluation failed; will retry next interval");
            }

            if (await DelayAsync(Interval, stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }

        _logger.LogInformation("Watchdog worker stopped");
    }

    private void RunOnce(DateTime nowUtc)
    {
        var verdict = _monitor.Evaluate(nowUtc);
        switch (verdict.Status)
        {
            case RecordingHeartbeatStatus.Missing:
                _logger.LogError("錄影心跳檔不存在：錄影行程可能從未啟動或已停止寫入。");
                break;
            case RecordingHeartbeatStatus.Stale:
                _logger.LogError(
                    "錄影心跳已過期 {Age}s：錄影行程可能已卡住或崩潰（最後更新 {Last:u}）。",
                    verdict.Age.TotalSeconds, verdict.Last?.UpdatedUtc);
                break;
            default:
                break;
        }
    }

    /// <summary>重新拉起設定的錄影程式；路徑不存在或啟動失敗只吞掉（告警已由監看器寫入稽核）。</summary>
    private static void Launch(string exe)
    {
        try
        {
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
            // 拉不起來不是看門狗能補救的；下一次偵測會再次告警。
        }
    }

    /// <summary>延遲；被取消時回 true 表示該收工了。</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}
