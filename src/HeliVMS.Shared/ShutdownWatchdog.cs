namespace HeliVMS.Shared;

/// <summary>
/// 關機看門狗：時間到就強制結束行程，確保「關掉程式」真的關掉。
/// </summary>
/// <remarks>
/// <para>
/// 收尾的每一步都各自有上限（見 <see cref="ShutdownSequencer"/>），但上限只保證「那一步會
/// 回來」；只要有任何一段沒被包到（新加的收尾工作、被第三方元件卡住的建構子、UI 執行緒上
/// 的同步等待），視窗關掉之後行程仍會一直留在工作管理員裡——使用者的感受就是「程式關不掉」，
/// 而且它還佔著 index.db 與攝影機連線。
/// </para>
/// <para>
/// 這是兜底而非正規路徑：真的被觸發代表有一步沒有被正確包住，所以一定要留下日誌，
/// 否則下次只會再次重演而查不到原因。
/// </para>
/// </remarks>
public sealed class ShutdownWatchdog : IDisposable
{
    private readonly Action<string>? _log;
    private readonly Action _onFire;
    private readonly Timer _timer;
    private int _fired;
    private int _disposed;

    /// <summary>
    /// 啟動看門狗。
    /// </summary>
    /// <param name="budget">總預算；超過就執行 <paramref name="onFire"/>。</param>
    /// <param name="onFire">時間到時要做的事（正式流程是清掉孤兒子行程後結束行程）。</param>
    /// <param name="log">日誌接收端。</param>
    public ShutdownWatchdog(TimeSpan budget, Action onFire, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(onFire);
        _onFire = onFire;
        _log = log;
        _timer = new Timer(_ => Fire(), null, budget, Timeout.InfiniteTimeSpan);
    }

    /// <summary>時間到是否真的觸發過（測試與診斷用）。</summary>
    public bool HasFired => Volatile.Read(ref _fired) != 0;

    private void Fire()
    {
        // 只准觸發一次：重複呼叫會讓日誌與工作各寫兩次。
        if (Interlocked.Exchange(ref _fired, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _log?.Invoke("關機收尾超過預算仍未完成，強制結束行程（未收完的 ffmpeg 與資料庫由下次啟動的孤兒清掃、.tmp 救援接手）。");

        try
        {
            _onFire();
        }
        catch (Exception ex)
        {
            // 看門狗自己失敗也不能讓行程繼續掛著——但至少要知道它失敗了。
            _log?.Invoke($"強制結束失敗：{ex}");
        }
    }

    /// <summary>正常收尾完成後解除；來不及觸發就不會再觸發。</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _timer.Dispose();
    }
}
