namespace HeliVMS.Shared;

/// <summary>
/// ffmpeg 子行程連續失敗後的重新啟動退避（§13-2 行程治理）。
///
/// <para>
/// 固定 5 秒重試看起來很勤勞，實際後果是「永遠連不上的來源」會以每秒 0.2 次的頻率
/// 反覆開行程：一台拔掉網路線的攝影機一天重啟 17,280 次，每次都牽動一次 RTSP 交握、
/// 一次行程建立/銷毀，以及（錄影路徑）一筆寫進 <c>segments</c> 表的 corrupt 記錄。
/// 指數退避把重試頻率壓到每天每頻道 288 次以內，而重連的反應時間最多只犧牲到 5 分鐘——
/// 對「離線的攝影機」而言，5 分鐘後再試與 5 秒後再試沒有實質差別。
/// </para>
///
/// <para>
/// 抽成共用的純函式，是為了讓解碼（<c>RtspClient</c>）、錄影（<c>SegmentRecorder</c>）
/// 與發佈（<c>LivePublisher</c>）三條路徑用同一組數字。先前錄影端已有一份私有實作，
/// 解碼端卻仍是固定 5 秒；兩份各自演化就會漂移，而漂移只有在生產現場才看得出。
/// </para>
/// </summary>
public static class FfmpegBackoff
{
    /// <summary>首次失敗後的退避基準。</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);

    /// <summary>退避上限：離線的來源不可能靠更頻繁重試就連上。</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 連續失敗第 <paramref name="consecutiveFailures"/> 次後應等待的時間，指數成長到
    /// <see cref="MaxDelay"/> 為止。失敗次數 ≤ 1 時回 <see cref="BaseDelay"/>。
    /// </summary>
    public static TimeSpan DelayFor(int consecutiveFailures)
        => DelayFor(consecutiveFailures, BaseDelay, MaxDelay);

    /// <summary>以自訂基準／上限計算退避（供測試與不同路徑覆寫）。</summary>
    public static TimeSpan DelayFor(int consecutiveFailures, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        if (consecutiveFailures <= 1)
        {
            return baseDelay;
        }

        // 失敗次數很大時 2^n 會變成無限大；先夾住指數，語意（結果封頂在 maxDelay）不變，
        // 也讓這裡的意圖一眼可見。
        var shift = Math.Min(consecutiveFailures - 1, 20);
        var scaled = baseDelay.TotalMilliseconds * Math.Pow(2, shift);
        return TimeSpan.FromMilliseconds(Math.Min(scaled, maxDelay.TotalMilliseconds));
    }
}
