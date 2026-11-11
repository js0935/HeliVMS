namespace HeliVMS.Shared;

/// <summary>
/// 一段錄影「有多長」的唯一換算口徑：檔案自己的長度優先，探測不到才退回牆鐘時間。
/// </summary>
/// <remarks>
/// <para>
/// 兩個數字量的不是同一件事：牆鐘時間是 ffmpeg 行程活了多久（含 RTSP 暖機、攝影機卡住、
/// 以及 Windows 睡眠／校時造成的時間跳動），檔案長度是影片真的有幾秒。索引要的是後者——
/// 時間軸、方向鍵跳轉、多路同步都拿它換算「這一刻有沒有畫面」。
/// </para>
/// <para>
/// 這裡刻意<b>不</b>用牆鐘時間當「合理性上界」來否決探測值：牆鐘時間正是那個已知會膨脹
/// 幾千秒的數字，拿它當上界等於把要修的缺陷重新引進判斷裡。探測值只要落在
/// (0, <see cref="MaxPlausibleSeconds"/>] 之內就比牆鐘時間可信。
/// </para>
/// </remarks>
public static class RecordedSegmentLength
{
    /// <summary>
    /// 探測值的合理上限（24 小時）。超過就是讀到了錯的檔案或壞掉的容器索引。
    /// </summary>
    public const double MaxPlausibleSeconds = 24 * 60 * 60;

    /// <summary>
    /// 決定要記進索引的段長（秒）。
    /// </summary>
    /// <param name="probedSeconds">檔案自己的長度；<c>null</c> 表示探測不到。</param>
    /// <param name="wallSeconds">行程牆鐘時間差（秒）。</param>
    public static double Resolve(double? probedSeconds, double wallSeconds)
    {
        var wall = double.IsFinite(wallSeconds) && wallSeconds > 0 ? wallSeconds : 0;

        if (probedSeconds is { } probed
            && double.IsFinite(probed)
            && probed > 0
            && probed <= MaxPlausibleSeconds)
        {
            return probed;
        }

        return wall;
    }
}
