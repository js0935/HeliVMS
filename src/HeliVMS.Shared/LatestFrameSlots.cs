namespace HeliVMS.Shared;

/// <summary>
/// 每格一格的「最新影格」暫存：解碼執行緒丟進來時，只有第一張會要求排程重畫，
/// 排程／繪製期間抵達的後續影格直接覆蓋舊的那張。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要它：監看路徑原本對<em>每一張</em>解碼影格都呼叫
/// <c>Dispatcher.InvokeAsync</c>。WPF Dispatcher 的工作佇列沒有容量上限，一旦
/// UI 執行緒的產能低於來源幀率（4×4 = 16 路 × 15fps = 240 幀/秒，每幀還要
/// memcpy 一次全幀 BGR24），佇列就會無界膨脹：記憶體持續上升，而且排在後面的
/// 都是「已經過期」的影格。症狀是操作員看到畫面愈拖愈舊、愈點愈卡，
/// 但 CPU 沒滿載（因為瓶頸在 UI 執行緒的單執行緒 memcpy），看起來像「LAG 但不忙」。
/// </para>
/// <para>
/// 改成 latest-wins 後，每格最多一個在途重畫，延遲上限被釘死在「一幀」，
/// 丟幀變成刻意且可計數的行為（<see cref="Dropped"/>），而不是默默堆積在佇列裡。
/// </para>
/// <para>
/// 型別 <typeparamref name="T"/> 刻意保持無限制、此檔不引用 WPF：這樣這條規則
/// 能在 net10.0 測試專案裡直接用行為斷言驗證，而不必退化成掃
/// <c>MainWindow.xaml.cs</c> 原始碼的字串比對（<c>HeliVMS.App</c> 是
/// net10.0-windows，測試專案根本無法參考它）。
/// </para>
/// </remarks>
/// <typeparam name="T">待重畫的影格型別；須為參考型別，這樣「尚未有影格」可用 null 表示。</typeparam>
public sealed class LatestFrameSlots<T> where T : class
{
    private readonly Lock _gate = new();
    private readonly T?[] _pending;
    private readonly bool[] _scheduled;
    private long _dropped;
    private long _rendered;

    /// <summary>建立 <paramref name="slotCount"/> 個獨立的影格槽。</summary>
    public LatestFrameSlots(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        _pending = new T?[slotCount];
        _scheduled = new bool[slotCount];
    }

    /// <summary>槽數（等同監看格數）。</summary>
    public int SlotCount => _pending.Length;

    /// <summary>因「已有在途重畫」而被丟棄的影格總數。</summary>
    /// <remarks>這是刻意丟棄而非意外遺失；數字長期偏高代表 UI 執行緒吃不下該格的幀率。</remarks>
    public long Dropped
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    /// <summary>實際交給繪製的影格總數。</summary>
    public long Rendered
    {
        get
        {
            lock (_gate)
            {
                return _rendered;
            }
        }
    }

    /// <summary>目前有待繪製影格的槽數；正常情況下不超過 <see cref="SlotCount"/>。</summary>
    /// <remarks>供診斷顯示用：持續大於 0 代表 UI 執行緒消化不掉的來源幀率正被丟棄。</remarks>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                foreach (var v in _pending)
                {
                    if (v is not null)
                    {
                        n++;
                    }
                }

                return n;
            }
        }
    }

    /// <summary>該槽目前是否有待繪製的影格。</summary>
    public bool IsPending(int slot)
    {
        Validate(slot);
        lock (_gate)
        {
            return _pending[slot] is not null;
        }
    }

    /// <summary>
    /// 存入影格。<paramref name="value"/> 為 null 時視為清除該槽（不排程）。
    /// </summary>
    /// <returns>
    /// <c>true</c>＝該槽先前沒有在途重畫，呼叫者應當派發一次重畫；
    /// <c>false</c>＝已經有在途重畫，本次影格已覆蓋為最新，不需再派發。
    /// </returns>
    public bool TrySchedule(int slot, T? value)
    {
        Validate(slot);

        lock (_gate)
        {
            if (value is null)
            {
                _pending[slot] = null;
                return false;
            }

            if (_scheduled[slot])
            {
                // 已經有一張在排程／繪製中：直接換成最新的，舊的那張永遠不會被看到。
                // 這就是「丟幀」——比排隊等它被畫出來（顯示過期畫面）好。
                _pending[slot] = value;
                _dropped++;
                return false;
            }

            _scheduled[slot] = true;
            _pending[slot] = value;
            return true;
        }
    }

    /// <summary>
    /// 取出並清空該槽待繪製的影格。回傳 null 表示沒有待繪製項目
    /// （例如中斷連線時已 <see cref="Clear"/>）。
    /// </summary>
    public T? Take(int slot)
    {
        Validate(slot);

        lock (_gate)
        {
            var value = _pending[slot];
            _pending[slot] = null;
            _scheduled[slot] = false;
            if (value is not null)
            {
                _rendered++;
            }

            return value;
        }
    }

    /// <summary>清空所有槽（監看中斷時呼叫，避免舊影格在新連線建立後被畫出來）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_pending);
            Array.Clear(_scheduled);
        }
    }

    private void Validate(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, _pending.Length);
    }
}