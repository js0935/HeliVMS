namespace HeliVMS.Recording;

/// <summary>
/// 回放時「這一輪解出的第 n 格」對到「檔案裡的哪一刻」。
/// </summary>
/// <remarks>
/// <para>
/// 抽成純函式是為了把時間基準講清楚並可測試：這是多路同步與 OSD 讀數的唯一來源。
/// 舊寫法用影格序號 ÷ 名目幀率推算，等於假設「每一格都恰好間隔 1/名目幀率 秒」；
/// 現場的相機送格速率會變（名目 15fps 實際 12.5fps、名目 30fps 實際 13fps），
/// 於是每一格都比名目時間早到，各台以不同速率前進——多路回放「各播各的」。
/// </para>
/// <para>
/// 正確答案在檔案自己的封包時間戳裡（<see cref="HeliVMS.Media.FrameTimestamps"/>）；
/// 取不到時才退回舊的固定間隔累加，回放照播不誤。
/// </para>
/// </remarks>
public static class PlaybackFrameTiming
{
    /// <summary>
    /// 起播點之後第一格的封包索引（第一個不早於 <paramref name="seekSeconds"/> 的封包）。
    /// </summary>
    /// <returns>
    /// 清單為空、或起播點已越過所有封包時回 <c>-1</c>（呼叫端退回固定間隔）。
    /// </returns>
    /// <remarks>
    /// ffmpeg 的輸入端 <c>-ss</c> 會丟掉時間戳早於起播點的影格，所以「第一個留下的影格」
    /// 就是第一個不早於起播點的封包；二分搜尋讓 5 分鐘檔案（數千個封包）不必逐筆掃。
    /// </remarks>
    public static int StartIndex(IReadOnlyList<double> times, double seekSeconds)
    {
        ArgumentNullException.ThrowIfNull(times);

        var low = 0;
        var high = times.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (times[mid] < seekSeconds)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low < times.Count ? low : -1;
    }

    /// <summary>
    /// 最後一個不晚於 <paramref name="seconds"/> 的封包索引（＝此刻畫面上那一格）。
    /// </summary>
    /// <returns>清單為空、或全部封包都晚於它時回 <c>-1</c>。</returns>
    /// <remarks>
    /// 逐幀要踩的是檔案裡真的相鄰的兩格，而不是「位置 ± 1/15 秒」：名目與實際幀率不同的
    /// 檔案上，固定步長會落在同一格（按了像沒反應）或跳過好幾格。
    /// </remarks>
    public static int IndexAtOrBefore(IReadOnlyList<double> times, double seconds)
    {
        ArgumentNullException.ThrowIfNull(times);

        var low = 0;
        var high = times.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (times[mid] <= seconds + 1e-6)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    /// <summary>
    /// 第 <paramref name="frameIndex"/> 格（0 起算，從起播點算）的內容時刻，毫秒。
    /// </summary>
    /// <param name="times">檔案的封包時間戳（秒，呈現順序）；空清單＝用固定間隔。</param>
    /// <param name="startIndex">起播點的第一格索引（<see cref="StartIndex"/>；-1＝沒有可用封包）。</param>
    /// <param name="frameIndex">這一輪已解出的第幾格（0 起算）。</param>
    /// <param name="previousMs">上一格的內容時刻（退回固定間隔時用來累加）。</param>
    /// <param name="fallbackStepMs">沒有時間戳時的每格間隔（毫秒，＝1000 ÷ 名目幀率）。</param>
    /// <remarks>
    /// 時間戳用完時（解析補完、封包數多於影格數等）繼續以固定間隔累加，而不是回頭跳到 0——
    /// 時刻一旦倒退，多路同步會把它當成嚴重落後而立刻重開解碼，畫面反而抖個不停。
    /// </remarks>
    public static long ContentMs(
        IReadOnlyList<double> times,
        int startIndex,
        long frameIndex,
        long previousMs,
        double fallbackStepMs)
    {
        ArgumentNullException.ThrowIfNull(times);

        if (startIndex >= 0 && frameIndex >= 0 && startIndex + frameIndex < times.Count)
        {
            return (long)Math.Round((times[startIndex + (int)frameIndex] - times[startIndex]) * 1000.0);
        }

        return frameIndex <= 0 ? 0 : (long)Math.Round(previousMs + fallbackStepMs);
    }
}
