using HeliVMS.Shared.Models;

namespace HeliVMS.Shared;

/// <summary>
/// 錄影段的「結束時刻／長度」換算，全應用程式唯一一份。
/// </summary>
/// <remarks>
/// <para>
/// 索引沒寫 <see cref="SegmentRecord.EndUtc"/>、也還沒回填 <see cref="SegmentRecord.DurationSec"/>
/// （剛切出來的那一段、從磁碟補登的舊資料）時，只能給一個估算值。估算值必須一致：時間軸色塊、
/// 事件與書籤比對、方向鍵跳轉、多路同步各用一份，畫面就會互相矛盾——色帶上明明有一塊，
/// 點下去卻回報「這裡沒有錄影」。
/// </para>
/// <para>
/// 10 秒不是任意數字：分段錄影的最短段長就在這個量級，當估算值剛好等於「還看不出長度的那一段」
/// 的實際大小時，畫面與行為才不會出現肉眼可見的落差。估短了會讓最後一段在時間軸上被裁掉，
/// 估長了會讓游標走到沒有影像的地方才停。
/// </para>
/// </remarks>
public static class PlaybackSegmentTiming
{
    /// <summary>索引沒有段長時的估算秒數。</summary>
    public const double FallbackDurationSeconds = 10;

    /// <summary>
    /// 段的結束時刻：優先用索引的 <see cref="SegmentRecord.EndUtc"/>，其次起點加段長，
    /// 兩者皆無時以 <see cref="FallbackDurationSeconds"/> 估算。
    /// </summary>
    public static DateTime EndOf(SegmentRecord segment) =>
        segment.EndUtc ?? segment.StartUtc.AddSeconds(segment.DurationSec ?? FallbackDurationSeconds);

    /// <summary>段長（秒），與 <see cref="EndOf"/> 同一口徑，至少 0。</summary>
    public static double LengthOf(SegmentRecord segment) =>
        Math.Max(0, (EndOf(segment) - segment.StartUtc).TotalSeconds);
}
