using HeliVMS.Core.Ntp;
using HeliVMS.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeliVMS.Agent;

/// <summary>
/// NTP 校時工作（M310）：由服務代桌面程式校正系統時鐘。
///
/// <para>
/// 校正 Windows 系統時鐘需要 <c>SeSystemtimePrivilege</c>；未提高權限的桌面行程沒有，而服務以
/// LocalSystem／LocalService 執行時預設就持有。這裡週期性地、或應桌面在設定庫留下的請求
/// （<see cref="NtpSettings.RequestUtc"/>），向 NTP 伺服器查時並呼叫 <see cref="SystemClock"/> 校正，
/// 再把結果（上次嘗試／上次成功／錯誤）寫回設定庫。
/// </para>
/// <para>
/// 沒有這條路徑時，未提高權限的桌面只能查到合理偏移量卻改不動時鐘，操作員會誤以為校時成功，
/// 但錄影時間戳照舊——證據時序就對不上。
/// </para>
/// </summary>
internal sealed class NtpSyncWorker : BackgroundService
{
    // 每 30 秒看一次設定庫。成本很低（一次讀幾列），卻能讓桌面的「立即對時」在數秒內被服務接手，
    // 又不必為此拉一條跨行程通道。
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly SqliteStore _store;
    private readonly ILogger<NtpSyncWorker> _logger;

    public NtpSyncWorker(SqliteStore store, ILogger<NtpSyncWorker> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NTP sync worker starting");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 對時是盡力而為：一次失敗不該讓整個 worker 結束，下一輪或下次請求會再試。
                _logger.LogWarning(ex, "NTP sync tick failed");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("NTP sync worker stopped");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var settings = new SettingsRepository(_store);
        if (!NtpSettings.IsEnabled(settings))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var request = NtpSettings.ReadUtc(settings, NtpSettings.RequestUtc);
        var applied = NtpSettings.ReadUtc(settings, NtpSettings.AppliedRequestUtc);
        var attempt = NtpSettings.ReadUtc(settings, NtpSettings.ClockAttemptUtc);
        if (!NtpSettings.IsDue(attempt, request, applied, now, NtpSettings.DefaultInterval))
        {
            return;
        }

        var server = NtpSettings.ReadServer(settings);

        // 先記下「嘗試時間」，即使查詢或校正失敗，週期計時也不會每 30 秒重打一次；
        // 請求失敗則由桌面再次按「立即對時」觸發（請求記號在下方一律標記為已處理）。
        settings.Set(NtpSettings.ClockAttemptUtc, NtpSettings.FormatUtc(now));

        var query = await new NtpClient().QueryAsync(server, ct: ct).ConfigureAwait(false);
        if (query is null)
        {
            settings.Set(NtpSettings.ClockError, "NTP query failed");
            _logger.LogWarning("NTP query to {Server} failed", server);
            MarkRequestApplied(settings, request);
            return;
        }

        var serverUtc = DateTime.UtcNow + query.Offset;
        settings.Set(NtpSettings.LastSyncUtc, NtpSettings.FormatUtc(serverUtc));
        settings.Set(NtpSettings.LastOffsetMs, NtpSettings.FormatOffsetMs(query.Offset));

        if (SystemClock.TrySetUtcNow(serverUtc, out var error))
        {
            settings.Set(NtpSettings.ClockSetUtc, NtpSettings.FormatUtc(DateTime.UtcNow));
            settings.Set(NtpSettings.ClockError, string.Empty);
            _logger.LogInformation(
                "System clock corrected via {Server}; offset {Offset} ms, round-trip {RoundTrip} ms, stratum {Stratum}",
                server,
                (long)query.Offset.TotalMilliseconds,
                query.RoundTrip.TotalMilliseconds,
                query.Stratum);
        }
        else
        {
            // 服務通常有權限；走到這裡多半是政策把服務帳戶的「變更系統時間」也移除了，記下來供診斷。
            settings.Set(NtpSettings.ClockError, error ?? string.Empty);
            _logger.LogWarning("System clock set via {Server} failed: {Error}", server, error);
        }

        MarkRequestApplied(settings, request);
    }

    /// <summary>把目前的請求標記為已處理，讓同一筆請求不會每輪重打（下次週期或再次請求才會再校時）。</summary>
    private static void MarkRequestApplied(SettingsRepository settings, DateTime? request)
    {
        if (request is DateTime req)
        {
            settings.Set(NtpSettings.AppliedRequestUtc, NtpSettings.FormatUtc(req));
        }
    }
}
