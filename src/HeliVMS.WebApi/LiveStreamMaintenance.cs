using HeliVMS.Rtc;

namespace HeliVMS.WebApi;

/// <summary>
/// M244 的 publisher 啟動器與定期維護。
/// <para>
/// 抽成 <see cref="LiveStreamMaintenance"/> 而非塞進 <c>Program.cs</c>，是為了讓
/// 「週期性回收逾時會話／關掉閒置 publisher」這件事有明確的名字與可測的形狀。
/// </para>
/// </summary>
public sealed class LiveStreamMaintenance : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly LiveStreamService _live;
    private readonly ILogger<LiveStreamMaintenance> _logger;

    public LiveStreamMaintenance(LiveStreamService live, ILogger<LiveStreamMaintenance> logger)
    {
        _live = live;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var reaped = await _live.MaintainAsync(stoppingToken).ConfigureAwait(false);
                if (reaped > 0)
                {
                    _logger.LogInformation("回收 {Count} 個逾時的即時觀看會話。", reaped);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 維護迴圈絕不能因為一次例外就死掉：死了就沒有人再回收逾時會話，
                // 被遺忘的觀看會話會把配額佔死。
                _logger.LogError(ex, "即時串流維護迴圈發生例外，下一輪會再試。");
            }
        }
    }
}
