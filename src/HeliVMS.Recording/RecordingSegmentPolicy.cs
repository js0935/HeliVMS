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
/// 預設 300 秒把檔數降為 15 秒的 1/20，同時保留操作員依站台需求調整的空間。
/// </para>
///
/// <para>
/// 7x24 一年級儲存（16 路約 250 TB）真正的瓶頸是檔數與索引列數，不是容量：16 路 ×
/// 60 秒一年約 840 萬列，<c>SegmentRepository</c> 自己就註明「一年可達千萬列」。
/// 預設拉到 300 秒把年列數砍到約 168 萬，且仍是上限 600 秒的一半，HLS 粒度可接受；
/// 這是一次性的成本最低槓桿（只改一個常數），不需要動到保留或分層。
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
///
/// <para>
/// 設定值是<b>上限</b>而不是固定長度：<see cref="PlanSeconds"/> 會把逼近整點的那一段縮短，
/// 讓每一段都結束在下一個整點之前。這是每小時合併能產出「01:00–02:00」而不是
/// 「01:57–02:03」的前提（段檔資料夾以起點所屬小時命名，跨點的段會把上一小時的內容
/// 帶進下一小時的資料夾）。
/// </para>
/// </summary>
public static class RecordingSegmentPolicy
{
    /// <summary>
    /// 整點對齊後允許的最短分段（秒）。
    ///
    /// <para>
    /// 只有「整點前不到一秒才開錄」會用到它：那時往下取整會得到 0，而 <c>-t 0</c>
    /// 產不出任何影格，ffmpeg 直接結束，索引就多一筆沒有內容的 corrupt。1 秒至少是一張
    /// 可回放的影格，跨過整點的量也只有零點幾秒。
    /// </para>
    /// </summary>
    public const int MinimumAlignedSeconds = 1;

    /// <summary>分段秒數的設定鍵；未設定或無法解析時採 <see cref="DefaultSeconds"/>。</summary>
    public const string SettingKey = "recording.segment_seconds";

    /// <summary>預設每段秒數。300 秒把 15 秒的檔數降為 1/20，又保留合理的搜尋粒度。</summary>
    public const int DefaultSeconds = 300;

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

    /// <summary>
    /// 這一段實際要錄幾秒：不跨過下一個整點，且不超過設定值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 為什麼要在整點收檔：段檔所在的資料夾是「起點所屬的那個小時」
    /// （<c>&lt;root&gt;\&lt;頻道&gt;\yyyyMMdd\HH</c>），而每小時合併是以資料夾為單位
    /// 重新封裝。固定長度會讓跨過整點的那一段把上一小時的內容帶進這個資料夾，合併出來的
    /// 檔因此是 01:57–02:03 而不是 01:00–02:00；反過來說，這也是「合併檔的起點永遠不是
    /// 整點」的唯一原因。
    /// </para>
    /// <para>
    /// 只有逼近整點的那一段會縮短，其餘仍是設定值，所以檔數不會因此增加。
    /// <c>-t</c> 量的是輸出內容長度，而 ffmpeg 從第一個影格起算，因此攝影機的連線暖機
    /// （實測健康攝影機 0.6–0.9 秒）會讓實際收檔時刻略晚於整點；索引的起點本來就記
    /// 「ffmpeg 啟動時刻」（見 AGENTS.md 的段起點口徑），不是這裡能修正的。
    /// </para>
    /// </remarks>
    /// <param name="configuredSeconds">操作員設定的分段秒數（視為上限）。</param>
    /// <param name="startUtc">這一段的起點（ffmpeg 啟動時刻；資料夾也用它分小時）。</param>
    public static int PlanSeconds(int configuredSeconds, DateTime startUtc)
    {
        var secondsIntoHour = startUtc.Minute * 60 + startUtc.Second + (startUtc.Millisecond / 1000.0);
        var toNextHour = 3600 - secondsIntoHour;
        var planned = Math.Min(configuredSeconds, toNextHour);

        // 向下取整：多錄的半秒會把「下一秒就是整點」的段推進下一個小時，正好是這個政策
        // 要避免的事。整點前不到一秒仍然給一秒——`-t 0` 產不出任何影格。
        return Math.Max(MinimumAlignedSeconds, (int)Math.Floor(planned));
    }
}
