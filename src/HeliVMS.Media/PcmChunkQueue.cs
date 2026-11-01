namespace HeliVMS.Media;

/// <summary>
/// 有界 PCM 片段佇列（<see cref="AudioPullClient"/> 與播放端之間的緩衝）。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要上限：拉音訊與寫音效卡是兩條速度不同的工作。音效卡卡住（裝置被獨佔、
/// 藍牙耳機重連、USB 音效卡拔掉又插回）時，若佇列無上限，記憶體會一路長到當掉；
/// 而且補播幾分鐘前的聲音比「跳過那一段」更糟——現場收音要的是現在發生的事。
/// </para>
/// <para>
/// 滿了就丟**最舊**的一段（不是拒絕新的）：保全場景寧可漏掉開頭，也不能讓畫面
/// 與聲音的時間差愈拉愈大。<see cref="Dropped"/> 是唯一看得出來的線索——沒有這個
/// 計數，「聲音有時斷斷續續」只能靠猜。
/// </para>
/// </remarks>
public sealed class PcmChunkQueue
{
    /// <summary>預設容量：100 ms 一段時約 5 秒的緩衝。</summary>
    public const int DefaultCapacity = 50;

    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks;
    private readonly int _capacity;
    private long _dropped;
    private long _enqueued;
    private long _bufferedBytes;

    /// <param name="capacity">最多保存幾段；小於 1 視為 1。</param>
    public PcmChunkQueue(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
        _chunks = new Queue<byte[]>(_capacity);
    }

    /// <summary>上限（段數）。</summary>
    public int Capacity => _capacity;

    /// <summary>目前排隊中的段數。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _chunks.Count;
            }
        }
    }

    /// <summary>目前排隊中的位元組數（診斷用；可看出緩衝堆積到什麼程度）。</summary>
    public long BufferedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bufferedBytes;
            }
        }
    }

    /// <summary>累計寫入的段數（清除佇列不會歸零）。</summary>
    public long Enqueued
    {
        get
        {
            lock (_gate)
            {
                return _enqueued;
            }
        }
    }

    /// <summary>因為佇列已滿而被丟棄的最舊段數。</summary>
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

    /// <summary>放入一段；已滿時先丟掉最舊的一段。</summary>
    public void Enqueue(byte[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        lock (_gate)
        {
            if (_chunks.Count >= _capacity)
            {
                var oldest = _chunks.Dequeue();
                _bufferedBytes -= oldest.Length;
                _dropped++;
            }

            _chunks.Enqueue(chunk);
            _bufferedBytes += chunk.Length;
            _enqueued++;
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>取出最舊的一段；沒有資料時立即回 false。</summary>
    public bool TryDequeue(out byte[] chunk)
    {
        lock (_gate)
        {
            if (_chunks.Count == 0)
            {
                chunk = [];
                return false;
            }

            TakeLocked(out chunk);
            return true;
        }
    }

    /// <summary>
    /// 取出最舊的一段；沒有資料時最多等待 <paramref name="timeout"/>，
    /// 讓播放執行緒可以閒置而不必輪詢。
    /// </summary>
    public bool TryDequeue(out byte[] chunk, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return TryDequeue(out chunk);
        }

        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_gate)
        {
            while (_chunks.Count == 0)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    chunk = [];
                    return false;
                }

                // 被 Pulse 叫醒後若仍為空（例如另一端只是 Clear），就繼續等剩下的時間。
                Monitor.Wait(_gate, (int)Math.Min(remaining, int.MaxValue));
            }

            TakeLocked(out chunk);
            return true;
        }
    }

    /// <summary>清空緩衝（停止播放時呼叫；累計計數保留供診斷）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _chunks.Clear();
            _bufferedBytes = 0;
        }
    }

    private void TakeLocked(out byte[] chunk)
    {
        chunk = _chunks.Dequeue();
        _bufferedBytes -= chunk.Length;
    }
}
