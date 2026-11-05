namespace HeliVMS.Shared;

/// <summary>
/// 顯示幀率上限的閘門（令牌桶）：容許短期抖動整批通過，長期平均仍受上限約束。
/// </summary>
/// <remarks>
/// <para>
/// 舊寫法是「距上一張至少 1/上限 秒，否則丟掉」。攝影機送出來的影格從來不是等間距的
/// ——現場實測 640×360 子碼流名目 15fps，抵達卻是一陣一陣（診斷日誌同時顯示
/// <c>read=15/s</c>、<c>drop=4–9/s</c>、<c>shown=5–10/s</c>）——於是每一批的第二張都被丟掉，
/// 畫面比攝影機原本還頓，而這件事在畫面上只表現成「怎麼有點不流暢」。
/// </para>
/// <para>
/// 令牌桶以「每張影格消耗 1 個令牌、令牌隨時間以 上限/秒 補充、存量上限為 burst」運作：
/// <list type="bullet">
/// <item>長期平均不會超過上限（超出部分照樣丟棄，UI 才不會被灌爆）；</item>
/// <item>burst 以內的抖動整批通過（2 張＝攝影機常見的「一批兩張」）；</item>
/// <item>閒置再久也只累積到 burst，串流卡住後恢復不會一次補上十幾張。</item>
/// </list>
/// </para>
/// <para>
/// 與 <c>RtspClient</c> 的搭配是刻意的：**先讀進來才丟**（令牌不足時影格已經在手上），
/// 管線永遠被抽乾，延遲才不會隨時間累積。
/// </para>
/// </remarks>
public sealed class FrameRateGate
{
    private readonly double _burst;
    private double _tokens;

    /// <param name="burstFrames">
    /// 可一次通過的張數上限（也是令牌存量的上限）。2 是現場抖動的形狀；調大只會讓 UI 承受
    /// 更大的瞬時量，調到 1 就退回「嚴格等間距」的老問題。
    /// </param>
    public FrameRateGate(double burstFrames = 2)
    {
        _burst = Math.Max(1, burstFrames);

        // 起始就給滿：第一張影格永遠要出畫（來源只送一張的測試 stub 也才收得到）。
        _tokens = _burst;
    }

    /// <summary>這一張要不要送出去（會消耗令牌）。</summary>
    /// <param name="elapsed">距上一次呼叫的間隔（含被丟棄的那些影格）。</param>
    /// <param name="maxFramesPerSecond">上限；≤ 0 表示不限。</param>
    public bool Admit(TimeSpan elapsed, double maxFramesPerSecond)
    {
        if (maxFramesPerSecond <= 0)
        {
            return true;
        }

        if (elapsed > TimeSpan.Zero)
        {
            _tokens = Math.Min(_burst, _tokens + (elapsed.TotalSeconds * maxFramesPerSecond));
        }

        if (_tokens < 1)
        {
            return false;
        }

        _tokens -= 1;
        return true;
    }
}
