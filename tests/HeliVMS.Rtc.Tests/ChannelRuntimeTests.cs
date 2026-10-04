using HeliVMS.Rtc;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// <see cref="ChannelRuntime"/> 啟動路徑的行為測試。
/// <para>
/// 這裡只驗證「時序保證」，完全不碰 ffmpeg：publisher 的啟動是注入的假實作，拆除則直接
/// 呼叫產品自己也會走的那條路。
/// </para>
/// </summary>
public class ChannelRuntimeTests
{
    [Fact]
    public async Task 等待首包期間被拆除不會回報成功()
    {
        // 生產症狀：OpenAsync 在沒有任何媒體的情況下回 201，瀏覽器顯示「已連線」卻永遠
        // 沒有畫面，而且 log 乾淨——就是 AGENTS.md 記的那個 landmine。等首包的那一段
        // 刻意放在 _startGate 之外（否則 30 秒的等待會卡住同通道的其他觀看者），所以必須
        // 證明「別人把 publisher 收掉」不會順便把這次等待取消成成功。
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource<PublisherStartException?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var runtime = new ChannelRuntime(
            7,
            new WhepOptions { PublisherStartTimeout = TimeSpan.FromMilliseconds(300) },
            (_, _, _) => { });

        var opening = runtime.EnsurePublisherAsync(
            new ChannelSource(7, "rtsp://camera/stream"),
            (_, _, _, _) =>
            {
                // 卡住 ffmpeg 啟動，讓測試可以精準地決定拆除發生在什麼時刻。
                entered.SetResult();
                return proceed.Task;
            },
            CancellationToken.None);

        // 等到 starter 真的被叫到：首包訊號已經就位，只差還沒有封包。
        await entered.Task;

        // 前置條件：這條通道確實一個封包都還沒收到。RtpIngest 綁的是 OS 指定的 loopback
        // 暫用埠，同一個 assembly 裡有測試會真的起 ffmpeg 送 RTP；那些 socket 釋放後埠號會
        // 被回收，若下一個測試剛好拿到同一個埠，还在串流的封包就會打進來，那時後面的斷言
        // 就不再代表「拆除被誤讀成成功」。把這件事寫成斷言，是為了讓它下次發生時是明確的
        // 失敗，而不是一個看不懂的間歇紅燈。
        Assert.Equal(0, runtime.PacketsReceived);

        // 服務關閉（DisposeAsync）走的就是這條路，而且刻意不經過 _startGate。
        await runtime.TeardownAsync();

        proceed.SetResult(null);

        // 一個 RTP 封包都沒有，開啟就不能報成功；逾時錯誤才是正確答案。
        await Assert.ThrowsAsync<PublisherStartException>(() => opening);
    }
}
