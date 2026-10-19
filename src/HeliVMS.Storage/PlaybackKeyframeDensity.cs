namespace HeliVMS.Storage;

/// <summary>
/// 時間軸關鍵幀刻度的密度判斷（§3.4「播放頭為關鍵幀吸附」）。
/// </summary>
/// <remarks>
/// <para>
/// 抽成純計算（無 WPF、無 ffprobe）是為了能真的驗證：時間軸帶畫在 WPF Canvas 上，
/// 桌面端沒有執行期測試環境，判斷式留在視窗裡就只能靠掃原始碼，密度算錯也沒人發現。
/// </para>
/// <para>
/// 動機是視覺而非效能：全日窗下單段可能有上千個關鍵幀，全畫會把錄影色塊糊成一條深色帶，
/// 操作員看不出哪裡有錄影——刻度是輔助，不該搶過錄影本身的辨識度。
/// </para>
/// </remarks>
public static class PlaybackKeyframeDensity
{
    /// <summary>
    /// 這一段區段的關鍵幀是否值得畫成刻度：以「平均關鍵幀間隔」換算成畫面上的像素，
    /// 達到 <paramref name="minPixels"/> 才畫。
    /// </summary>
    /// <param name="keys">該區段的關鍵幀（依 <c>TimeSec</c> 排序）。</param>
    /// <param name="spanSeconds">目前檢視窗的秒數。</param>
    /// <param name="widthPixels">時間軸帶的像素寬。</param>
    /// <param name="minPixels">相鄰刻度至少要有的像素間隔。</param>
    /// <remarks>
    /// 用平均間隔而不是任兩個相鄰關鍵幀：GOP 長度會變（場景切換時更短），
    /// 看單一間隙會被一個短 GOP 誤判成整段太密而整段不畫。
    /// </remarks>
    public static bool ShouldDraw(
        IReadOnlyList<SegmentKeyframe> keys,
        double spanSeconds,
        double widthPixels,
        double minPixels)
    {
        if (keys is null || keys.Count < 2 || spanSeconds <= 0 || widthPixels <= 0 || minPixels <= 0)
        {
            return false;
        }

        var averageGapSeconds = (keys[^1].TimeSec - keys[0].TimeSec) / (keys.Count - 1);
        if (averageGapSeconds <= 0)
        {
            return false;
        }

        return averageGapSeconds * (widthPixels / spanSeconds) >= minPixels;
    }
}
