using System.Globalization;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 錄影分段長度政策（M312）：決定每段錄影寫成一個檔案的秒數。
///
/// <para>
/// 分段長度是整個錄影管線的槓桿：每段一個 MP4 檔、一筆 <c>segments</c> 索引、一個 HLS 片段。
/// 早期固定 15 秒，單一頻道一天就是 5,760 個檔（16 路 30 天約 277 萬個檔），
/// 讓磁碟目錄、<c>segments</c> 表與保留清理都被檔數而非容量壓垮。改成可設定後，
/// 預設 60 秒把檔數直接降為四分之一，同時保留操作員依站台需求調整的空間。
/// </para>
///
/// <para>
/// 為何不建議更長（例如 1 小時一段）：分段同時是 HLS 的媒體片段，一段一小時會讓
/// <c>EXT-X-TARGETDURATION</c> 變 3600、播放器搜尋粒度變一小時、單一片段抓取動輒上 GB；
/// 且程序當下最多會損失一整段的錄影。因此上限刻意收在 <see cref="MaxSeconds"/>＝10 分鐘。
/// </para>
///
/// <para>
/// 下限 <see cref="MinSeconds"/>＝5 秒與上限同樣重要：更短的分段會讓每段的 ffmpeg
/// 啟停與收尾（改名、SHA-256、索引回寫）成為主體成本，反而更容易掉段。
/// </para>
/// </summary>
public static class RecordingSegmentPolicy
{
    /// <summary>分段秒數的設定鍵；未設定或無法解析時採 <see cref="DefaultSeconds"/>。</summary>
    public const string SettingKey = "recording.segment_seconds";

    /// <summary>預設每段秒數。60 秒把 15 秒的檔數降為四分之一，又保留合理的搜尋粒度。</summary>
    public const int DefaultSeconds = 60;

    /// <summary>允許的最短分段（秒）。</summary>
    public const int MinSeconds = 5;

    /// <summary>允許的最長分段（秒）；上限見類別說明的 HLS 與崩潰損失理由。</summary>
    public const int MaxSeconds = 600;

    /// <summary>從共用設定讀取分段秒數；未設定或無法解析時回 <see cref="DefaultSeconds"/>。</summary>
    public static int Read(SqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Parse(new SettingsRepository(store).Get(SettingKey));
    }

    /// <summary>
    /// 解析設定字串。無法解析時回 <see cref="DefaultSeconds"/>；可解析但超出
    /// <see cref="MinSeconds"/>～<see cref="MaxSeconds"/> 時夾到邊界（而非退回預設），
    /// 這樣「設 1」會被當成最接近的合法值 5，而不是悄悄跳回 60。
    /// </summary>
    public static int Parse(string? raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return DefaultSeconds;
        }

        return Math.Clamp(value, MinSeconds, MaxSeconds);
    }

    /// <summary>
    /// 解析並驗證設定字串。只有在能解析成整數且落在合法範圍內時才回 true；
    /// 供設定中心區分「合法套用」與「退件提示」。
    /// </summary>
    public static bool TryParse(string? raw, out int seconds)
    {
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= MinSeconds
            && value <= MaxSeconds)
        {
            seconds = value;
            return true;
        }

        seconds = DefaultSeconds;
        return false;
    }
}
