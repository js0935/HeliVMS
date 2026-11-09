namespace HeliVMS.Shared;

/// <summary>
/// 監看牆「輪巡」（QC_Live 的 AutoScan）落點規劃。純函式，行為測試在
/// <c>WallAutoScanPlannerTests</c>。
/// </summary>
/// <remarks>
/// <para>
/// 行為依 QCV8001 反編譯事實（<c>QC_Live\decompiled\functions.txt</c>，AutoScan tick）：
/// 每個週期把畫面往前推「一個分割量」的頻道——
/// <c>offset = (offset + cells) % count</c>，推進後若尾頁不足一個分割量
/// （<c>offset + cells > count</c>）則夾在 <c>count - cells</c>；
/// 分割量 ≥ 頻道數時不動（畫面已經是全部頻道，QC_Live 同樣跳過）。
/// </para>
/// <para>
/// 開關與週期由呼叫端持久化（<c>app_settings</c> 的 <c>wall.auto_scan</c>／
/// <c>wall.auto_scan_seconds</c>）；這裡只負責「下一頁從哪一台開始」與週期整備，
/// 不碰 UI、不碰工作階段——改派一律走既有的 <c>ApplyWallAsync(offset)</c>。
/// </para>
/// </remarks>
public static class WallAutoScanPlanner
{
    /// <summary>週期下限（秒）：再短操作員來不及看清任何一路，也貼著重連抖動。</summary>
    public const int MinPeriodSeconds = 3;

    /// <summary>週期上限（秒）：一小時，再長等於關掉，不如直接不開。</summary>
    public const int MaxPeriodSeconds = 3600;

    /// <summary>沒有設定或設定讀不懂時的週期（秒）。＝HeliVMS 跳台序列的既有節奏；
    /// QC_Live 的 <c>AutoScanPeriod</c> 預設值無法從二進位確認，取現狀值以保證升級後行為不變。</summary>
    public const int DefaultPeriodSeconds = 5;

    /// <summary>
    /// 把輪巡週期整備到可用範圍：沒設定、≤0 或讀不懂回 <see cref="DefaultPeriodSeconds"/>，
    /// 其餘夾在 <see cref="MinPeriodSeconds"/>～<see cref="MaxPeriodSeconds"/>。
    /// </summary>
    public static int NormalizePeriodSeconds(int? seconds)
    {
        var value = seconds is int s and > 0 ? s : DefaultPeriodSeconds;
        return Math.Clamp(value, MinPeriodSeconds, MaxPeriodSeconds);
    }

    /// <summary>
    /// 下一頁的起始頻道序。輪巡不該動時（頻道數 0、分割量 ≤0、分割量 ≥ 頻道數）
    /// 原地不動；呼叫端以此判斷要不要改派。
    /// </summary>
    public static int NextOffset(int channelCount, int splitCells, int offset)
    {
        if (channelCount <= 0 || splitCells <= 0 || splitCells >= channelCount)
        {
            return Math.Max(0, offset);
        }

        var current = Math.Max(0, offset);
        var next = (current + splitCells) % channelCount;
        if (next + splitCells > channelCount)
        {
            next = channelCount - splitCells;
        }

        return next;
    }
}
