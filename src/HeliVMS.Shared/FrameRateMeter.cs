namespace HeliVMS.Shared;

/// <summary>
/// 以滑動時間窗估算單一監看格的實際畫面更新率，以及「目前畫面上這張有多舊」。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要它：監看介面原本完全沒有 fps／延遲資訊，操作員只能靠肉眼判斷「卡不卡」。
/// 而「畫面愈拖愈舊」這個問題（UI 執行緒消化不掉的影格在佇列裡堆積）在沒有數字的情況下
/// 完全看不出來——症狀是「CPU 沒滿載但畫面LAG」，很容易被誤判成機器效能不足而去買
/// 更快的 CPU，實際瓶頸在 UI 執行緒的單執行緒 memcpy。</para>
/// <para>
/// 「畫面年齡」用 <c>now − 最後一張影格的時間戳</c> 表示，而不是算解碼耗時：它直接反映
/// 使用者此刻看到的畫面是多久以前的來源畫面，也就是真正該被監控的數字。</para>
/// </remarks>
public sealed class FrameRateMeter
{
    private readonly Queue<DateTime> _window = new();
    private readonly TimeSpan _windowSize;

    /// <summary>建立計量器。</summary>
    /// <param name="windowSize">滑動窗長度；太短會讓 fps 數字跳動，太長則反應慢。</param>
    public FrameRateMeter(TimeSpan? windowSize = null)
    {
        _windowSize = windowSize ?? TimeSpan.FromSeconds(3);
        if (_windowSize <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize), "滑動窗必須為正");
        }
    }

    /// <summary>目前估算的畫面更新率（幀／秒）。不足兩幀時為 0。</summary>
    public double Fps
    {
        get
        {
            if (_window.Count < 2)
            {
                return 0;
            }

            // 用第一張到最後一張的實際間隔當分母，而不是窗長：
            // 剛連線不到一秒時就能給出有意義的數字，不必等窗填滿。
            var span = (_window.Last() - _window.First()).TotalSeconds;
            if (span <= 0)
            {
                return 0;
            }

            // 上限：攝影機與網路都會「一陣一陣」送（現場實測一批兩三張貼在同幾毫秒內），
            // 那一刻的分母趨近 0，算出來是幾百 fps。這個數字要回答的是「畫面有沒有在更新、
            // 有沒有愈拖愈舊」——爆表的瞬時值只會蓋掉真正的訊號（低 fps、高年齡）。
            return Math.Min((_window.Count - 1) / span, MaxFps);
        }
    }

    /// <summary>回報的更新率上限（幀／秒）：抖動的瞬時值不該蓋掉真正的訊號。</summary>
    public const double MaxFps = 240;

    /// <summary>窗內影格數（供測試與診斷）。</summary>
    public int SampleCount => _window.Count;

    /// <summary>記錄一張已畫上螢幕的影格時間（UTC）。</summary>
    public void Record(DateTime frameUtc)
    {
        if (_window.Count > 0 && frameUtc < _window.Last())
        {
            // 時間倒退＝這是上一段的舊影格遲到（或新的串流段）：舊樣本不再代表現在。
            _window.Clear();
        }
        else if (_window.Count > 0 && frameUtc == _window.Last())
        {
            // 同一毫秒的兩張（現場的批次抖動就是這樣）不計入：分母趨近 0 會讓 fps 爆表，
            // 而那個數字只會被誤讀成「這台機器很快」。
            return;
        }

        _window.Enqueue(frameUtc);
        while (_window.Count > 2 && _window.Last() - _window.First() > _windowSize)
        {
            _window.Dequeue();
        }
    }

    /// <summary>
    /// 最後一張影格距 <paramref name="nowUtc"/> 多久。
    /// 沒有任何影格時回傳 <see cref="TimeSpan.Zero"/>。
    /// </summary>
    public TimeSpan Age(DateTime nowUtc)
    {
        if (_window.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var age = nowUtc - _window.Last();
        return age > TimeSpan.Zero ? age : TimeSpan.Zero;
    }

    /// <summary>清空（監看中斷或格位改派時呼叫，避免顯示上一段串流的舊數字）。</summary>
    public void Clear() => _window.Clear();
}