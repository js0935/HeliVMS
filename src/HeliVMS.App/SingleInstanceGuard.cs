using System.Threading;

namespace HeliVMS.App;

/// <summary>
/// 單一實例守衛：同一台機器同一登入工作階段只允許一個 HeliVMS 執行；重複啟動時
/// 不開第二個行程，而是發出喚醒訊號讓「已經在跑的那一個」把視窗還原並帶到前景。
///
/// <para>
/// 為什麼需要它：操作員把捷徑／自動啟動點第二次時，會開出第二個完整行程——第二份錄影
/// 主機、第二份保留清理迴圈、第二組 ffmpeg 解碼，同時搶同一個 <c>index.db</c> 與同一顆
/// 錄影磁碟。症狀是「某些台在第二個視窗才看得到」「清理刪到別的視窗還在播的檔」這類
/// 難以歸因的互搶行為。正確行為是：第二個啟動只負責把第一個帶到前景。
/// </para>
///
/// <para>
/// 以 <c>Local\</c>（而非 <c>Global\</c>）為前置詞：讓同一台機器上以不同帳號登入的作業
/// 人員仍能各自開一個（多人共用 NVR 主機時是合理情境），只擋「同一個使用者重複啟動」。
/// 名稱字串集中在常數，跨行程兩端必須用完全相同的名字才配得上。
/// </para>
///
/// <para>
/// 喚醒用 <see cref="EventWaitHandle"/>（自動重設）而不是輪詢檔案或具名管道：Set 的瞬間
/// 等待端就被喚醒，且不需要額外清理資源；行程結束時核心物件自動釋放。等待刻意放在背景
/// 執行緒——若在 UI 執行緒上等，第一個實例的介面會整個卡住，比開出第二個還糟。
/// </para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>互斥鎖名稱；後續實例以「能不能立刻取得」判斷自己是不是第一個。</summary>
    internal const string MutexName = @"Local\HeliVMS.SingleInstance.Mutex";

    /// <summary>喚醒事件名稱；後續實例 Set，第一個實例的等待執行緒據此把視窗帶到前景。</summary>
    internal const string ActivateEventName = @"Local\HeliVMS.SingleInstance.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _waiter;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activateEvent, Action onActivate)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
        _waiter = new Thread(() => WaitLoop(onActivate))
        {
            // 背景執行緒：關閉應用程式時不該被這條等待執行緒擋住而無法結束。
            IsBackground = true,
            Name = "HeliVMS.SingleInstanceActivate",
        };
        _waiter.Start();
    }

    /// <summary>
    /// 嘗試成為第一個實例。成功回傳 guard（呼叫端負責在關閉時 <see cref="Dispose"/>）；
    /// 若已有實例在跑則發送喚醒訊號、回傳 <c>null</c>，呼叫端應立即結束自己。
    /// </summary>
    /// <param name="onActivate">已有實例收到喚醒訊號時要執行的動作（由第一個實例提供）。</param>
    public static SingleInstanceGuard? TryAcquire(Action onActivate)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // 已經有人在跑：喚醒它（Set 後立刻結束本行程），互斥鎖不必保留。
            using (var wake = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName))
            {
                wake.Set();
            }

            mutex.Dispose();
            return null;
        }

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        return new SingleInstanceGuard(mutex, activate, onActivate);
    }

    private void WaitLoop(Action onActivate)
    {
        var token = _cts.Token;

        // WaitAny 而非 WaitOne：關閉時要能立刻被取消喚醒，不必等下一個跨行程 Set。
        var handles = new[] { (WaitHandle)_activateEvent, token.WaitHandle };
        while (!token.IsCancellationRequested)
        {
            var index = WaitHandle.WaitAny(handles);
            if (index != 0)
            {
                return;
            }

            try
            {
                onActivate();
            }
            catch (Exception)
            {
                // 喚醒動作（跨執行緒呼叫 UI）失敗不該讓等待執行緒死掉，下一個喚醒仍要能作用。
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _activateEvent.Dispose();

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 未持有就釋放（例如已被系統回收）：不影響關閉流程，吞掉即可。
        }

        _mutex.Dispose();
    }
}
