namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// 連線處理用專用執行緒：QCTek 相容層的「回應時效」是協定行為的一部分。
/// </summary>
/// <remarks>
/// <para>
/// QCC 的即時影像狀態機以 33ms 為週期輪詢；實測（QCC ↔ 真機 vs QCC ↔ HeliVMS 的
/// 對照抓包）顯示，挑戰與 OK 的回應只要跨過一個週期（超過 33ms），QCC 就會在授權成功後
/// 立刻關線（OK 之後 0.6ms 發 FIN），即時影像永遠黑屏。真機是 C++ 行程、回應低於
/// 1ms、每次都成功；HeliVMS 走 <see cref="ThreadPool"/> 時回應 20–90ms、每次都失敗——
/// 這台機器同時在錄影與監看，執行緒池的工作佇列常年非空，新工作項的喚醒延遲被排到
/// 幾十毫秒之外。
/// </para>
/// <para>
/// NEMO 連線是「一條連線一個長壽任務」的形狀（accept → 逐命令應答 → 對端關閉），
/// 用專用執行緒不增加併發壓力：每條 CMS 連線一條 thread，數量 = 同時連線的 CMS 數
/// （個位數）。連線任務內部全部是 <c>ConfigureAwait(false)</c> 的 I/O await，
/// 由專用執行緒同步等待不會死鎖。
/// </para>
/// </remarks>
internal static class DedicatedThread
{
    /// <summary>在新建的背景執行緒上執行 <paramref name="action"/>（不排執行緒池佇列）。</summary>
    public static void Run(Action action, string name)
    {
        ArgumentNullException.ThrowIfNull(action);

        var thread = new Thread(() => action())
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
    }
}
