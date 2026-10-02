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

/// <summary>正式環境的 publisher 啟動器：真的呼叫 ffmpeg。</summary>
public static class LivePublishers
{
    /// <summary>
    /// 啟動 ffmpeg；失敗時回傳帶原因（已遮蔽憑證）的例外物件。
    /// </summary>
    public static Task<PublisherStartException?> StartFfmpeg(
        LivePublisher publisher,
        System.Net.IPEndPoint rtpTarget,
        ChannelSource source,
        CancellationToken token)
    {
        try
        {
            publisher.Start(source.RtspUrl, rtpTarget);
            return Task.FromResult<PublisherStartException?>(null);
        }
        catch (PublisherStartException ex)
        {
            return Task.FromResult<PublisherStartException?>(ex);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Win32Exception：找不到 ffmpeg 可執行檔（最常見的部署問題）。
            return Task.FromResult<PublisherStartException?>(
                new PublisherStartException($"無法啟動 ffmpeg（{HeliVMS.Rtc.WhepOptions.Prefix}FFMPEG）：{ex.Message}"));
        }
    }
}