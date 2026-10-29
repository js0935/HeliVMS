namespace HeliVMS.Shared;

/// <summary>
/// 關機收尾的順序執行器：每一步都丟到執行緒池，並有上限地等它完成。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼不能在呼叫端直接等：呼叫端是 WPF 的 UI 執行緒。收尾工作（停 ffmpeg、釋放解碼連線、
/// 關資料庫）內部都是 <c>await</c>；一旦在 UI 執行緒上同步等一件「續體要排回同一個
/// Dispatcher」的工作，就是自我死結——視窗已經關掉、畫面看起來正常，行程卻永遠停在
/// <c>Wait()</c> 裡。工作管理員留著一個沒有視窗的 HeliVMS.exe，而 <c>index.db</c> 也因此
/// 沒有乾淨關閉（WAL 沒 checkpoint），下一個行程讀到的索引可能停在舊狀態。
/// </para>
/// <para>
/// 逾時或失敗都要留下紀錄：關機沒收乾淨的現場症狀是資料夾裡躺著 <c>.tmp</c>、回放清單少了
/// 最後一段，沒有日誌就只剩操作員自己發現。
/// </para>
/// </remarks>
public static class ShutdownSequencer
{
    /// <summary>
    /// 執行單一收尾步驟。
    /// </summary>
    /// <param name="what">步驟名稱（寫進日誌；要讓操作員看得懂是哪一件事卡住）。</param>
    /// <param name="step">步驟內容。</param>
    /// <param name="timeout">最長等待。</param>
    /// <param name="log">日誌接收端；null 表示不記錄。</param>
    /// <returns>true 表示在期限內完成（含步驟本身拋例外的情形：例外已記錄）。</returns>
    public static bool Run(string what, Func<Task> step, TimeSpan timeout, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(step);

        try
        {
            var work = Task.Run(step);
            if (work.Wait(timeout))
            {
                return true;
            }

            log?.Invoke($"{what}逾時（{timeout.TotalSeconds:0.#} 秒未完成），改由行程結束時強制清除。");

            // 逾時的工作還在跑。明確觀察它的例外，否則 Task 被回收時會冒出
            // UnobservedTaskException，把真正的原因（這一步卡住）淹掉。
            _ = work.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke($"{what}失敗：{ex}");
            return true;
        }
    }
}
