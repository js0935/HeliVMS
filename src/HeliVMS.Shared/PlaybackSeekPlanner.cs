using HeliVMS.Shared.Models;

namespace HeliVMS.Shared;

/// <summary>
/// 回放「方向鍵跳轉」（←／→ 及其加按 Ctrl、Shift 的倍數）的落點計算。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要抽成純函式：這段邏輯原本寫在 WPF 的鍵盤處理常式裡，而桌面端沒有執行期測試環境，
/// 只能靠人眼驗證；偏偏它最容易錯的地方是「目標時刻落在兩段錄影之間的空檔」——白天錄影中斷
/// 很常見，出錯時畫面看起來完全正常，只是跳到錯的時間。
/// </para>
/// <para>
/// 空隙的語意：依行進方向落到**最近的段邊界**，不是跳到當日最後一段。舊行為是「比第一段早
/// 就跳到第一段開頭，其餘一律跳到最後一段尾端」，於是按一下右方向鍵就從早上跳到晚上，
/// 真正想看的下一段被靜默跳過，操作員只會覺得時間軸壞了。只有已經越過最後一段時才停在尾端，
/// 再按也還在原地（不會繞回開頭）。
/// </para>
/// <para>
/// 段長／結束時刻一律走 <see cref="PlaybackSegmentTiming"/>：索引沒回填長度的那一段若用 0 秒
/// 比對，等於把它當成不存在，跳轉會直接掠過它。
/// </para>
/// </remarks>
public static class PlaybackSeekPlanner
{
    /// <summary>
    /// 落在段尾時回退的一小段：<c>ffmpeg -ss &lt;段長&gt;</c> 不會產生任何影格，
    /// 回報「播放完成」而畫面全黑，所以停在最後一格之前。
    /// </summary>
    public const double LastFrameEpsilonSeconds = 0.001;

    /// <summary>跳轉落點：要播哪一段、從段內第幾秒起。</summary>
    public readonly record struct Landing(SegmentRecord Segment, double OffsetSeconds)
    {
        /// <summary>此落點對應的絕對時刻。</summary>
        public DateTime Utc => Segment.StartUtc.AddSeconds(OffsetSeconds);
    }

    /// <summary>
    /// 算出從目前位置往 <paramref name="targetUtc"/> 跳的落點；沒有任何錄影段時回 <c>null</c>。
    /// </summary>
    /// <param name="segments">當日可播的段（允許未排序）。</param>
    /// <param name="targetUtc">想跳到的絕對時刻。</param>
    /// <param name="forward">true＝往後（右方向鍵）；false＝往前（左方向鍵）。</param>
    public static Landing? Resolve(
        IEnumerable<SegmentRecord> segments,
        DateTime targetUtc,
        bool forward)
    {
        var ordered = segments.OrderBy(s => s.StartUtc).ToList();
        if (ordered.Count == 0)
        {
            return null;
        }

        // 1. 目標落在某一段之內：從那個點起播。區間是 [起點, 結束)，結束那一刻不算「在段內」。
        //    結束時刻本身沒有影格（ffmpeg -ss <段長> 什麼都不產生），落在那裡等於回報「播放完成」
        //    而畫面不動：真實症狀是按右鍵從 08:00:50 跳到 08:01:00（某段剛好結束）時，明明後面
        //    還有錄影，狀態列卻跳成「已播放結束」，再按一次才前進——看起來像跳轉少了一次。
        //    半開區間同時讓接縫（下一段起點＝前一段結束）自然歸屬後一段，不必靠「起點較晚者優先」
        //    這個保險規則才正確。
        Landing? covering = null;
        foreach (var segment in ordered)
        {
            if (segment.StartUtc <= targetUtc && targetUtc < PlaybackSegmentTiming.EndOf(segment))
            {
                // 段有重疊時取起點較晚的那一段，與多路同步的落點規則一致。
                if (covering is null || segment.StartUtc > covering.Value.Segment.StartUtc)
                {
                    covering = new Landing(
                        segment,
                        Math.Max(0, (targetUtc - segment.StartUtc).TotalSeconds));
                }
            }
        }

        if (covering is { } hit)
        {
            return hit;
        }

        if (forward)
        {
            // 2. 落在空隙：往後就進下一段（起點不早於目標的最近一段）。
            var next = ordered.FirstOrDefault(s => s.StartUtc >= targetUtc);
            if (next is not null)
            {
                return new Landing(next, 0);
            }

            // 3. 已越過最後一段：停在尾端。
            var last = ordered[^1];
            return new Landing(last, LastFrameOf(last));
        }

        // 2'. 落在空隙：往前就回到前一段的尾端（結束時刻不晚於目標的最近一段）。
        var previous = ordered.LastOrDefault(
            s => PlaybackSegmentTiming.EndOf(s) <= targetUtc);
        if (previous is not null)
        {
            return new Landing(previous, LastFrameOf(previous));
        }

        // 3'. 已退到第一段之前：停在開頭。
        var first = ordered[0];
        return new Landing(first, 0);
    }

    /// <summary>段內最後一格的偏移秒數（段長未知時仍至少為 0）。</summary>
    public static double LastFrameOf(SegmentRecord segment) =>
        Math.Max(0, PlaybackSegmentTiming.LengthOf(segment) - LastFrameEpsilonSeconds);
}
