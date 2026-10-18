using HeliVMS.Shared.Models;

namespace HeliVMS.Recording;

/// <summary>
/// 多路同步回放的排程核心（ARCHITECTURE §8.5／§11.4「多通道同步回放，最多 4 路同軸時間比對」）。
///
/// <para>
/// 桌面端沒有 WPF 執行期測試環境（見 <c>UiWiringContractTests</c> 的說明），
/// 4 路鎖同一播放頭時「每一路各自該播哪一段、從第幾秒起」的判斷若留在程式碼後置裡就只能靠人眼驗證；
/// 這裡把它抽成純函式，才能在測試裡把邊界（空隙、整天前後、段端點）逐一根釘死。
/// </para>
/// <para>
/// 關鍵語意：各路的錄影段切點不同，同一個絕對時刻對某一路可能是「有錄影的第 N 段」、
/// 對另一路卻是「兩段之間的空隙」。同步播放要求的是**同一條時間軸**，因此空隙必須回
/// <c>null</c>（畫面停在上一幀並標示無錄影），而不是硬跳到別的段——否則兩路會播放不同時刻卻看似同步。
/// </para>
/// </summary>
public static class PlaybackSyncPlanner
{
    /// <summary>某一路在某一絕對時刻的落點：涵蓋該時刻的區段與段內偏移秒數。</summary>
    public readonly record struct Cursor(SegmentRecord Segment, double OffsetSeconds)
    {
        /// <summary>此落點對應的絕對時刻。</summary>
        public DateTime Utc => Segment.StartUtc.AddSeconds(OffsetSeconds);
    }

    /// <summary>
    /// 區段結束時刻。<see cref="SegmentRecord.EndUtc"/> 為 null（錄影中或舊資料）時以
    /// <see cref="SegmentRecord.DurationSec"/> 推算；再無則視為零長度段（起點即終點）。
    /// </summary>
    public static DateTime EndOf(SegmentRecord segment) =>
        segment.EndUtc ?? segment.StartUtc.AddSeconds(segment.DurationSec ?? 0);

    /// <summary>區段長度（秒），至少 0。</summary>
    public static double LengthOf(SegmentRecord segment) =>
        Math.Max(0, (EndOf(segment) - segment.StartUtc).TotalSeconds);

    /// <summary>
    /// 在一串（允許未排序）區段中找出涵蓋 <paramref name="targetUtc"/> 的那一段，並算出段內偏移。
    /// 落在空隙、或整天區段之前／之後，一律回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 端點採閉區間：正好落在段起點時偏移 0，正好落在段終點時仍屬該段（與 <see cref="EndOf"/> 一致），
    /// 兩段相接時才不會出現「剛好卡在接縫」的空窗。
    /// </remarks>
    public static Cursor? Resolve(IEnumerable<SegmentRecord> segments, DateTime targetUtc)
    {
        Cursor? best = null;
        foreach (var segment in segments)
        {
            if (segment.StartUtc <= targetUtc && targetUtc <= EndOf(segment))
            {
                var offset = Math.Max(0, (targetUtc - segment.StartUtc).TotalSeconds);
                if (best is null || segment.StartUtc > best.Value.Segment.StartUtc)
                {
                    best = new Cursor(segment, offset);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// 起點不早於 <paramref name="targetUtc"/> 的最近一段；用於目前落在空隙時「等到下一段再進場」。
    /// 沒有下一段時回 <c>null</c>。
    /// </summary>
    public static SegmentRecord? NextAtOrAfter(IEnumerable<SegmentRecord> segments, DateTime targetUtc) =>
        segments
            .Where(s => s.StartUtc >= targetUtc)
            .OrderBy(s => s.StartUtc)
            .FirstOrDefault();

    /// <summary>
    /// 某一組區段的時間範圍（最早起點、最晚終點）；無區段時回 <c>null</c>。
    /// 供「同步播放起點／終點」與進度換算使用。
    /// </summary>
    public static (DateTime StartUtc, DateTime EndUtc)? Bounds(IEnumerable<SegmentRecord> segments)
    {
        DateTime? start = null;
        DateTime? end = null;
        foreach (var segment in segments)
        {
            if (start is null || segment.StartUtc < start)
            {
                start = segment.StartUtc;
            }

            var segEnd = EndOf(segment);
            if (end is null || segEnd > end)
            {
                end = segEnd;
            }
        }

        return start is { } s && end is { } e ? (s, e) : null;
    }

    /// <summary>
    /// 多路同步時整組的時間範圍：最早起點到最晚終點；全部皆空時回 <c>null</c>。
    /// 壁面可播的總長以此為準（某路中間有斷，總長仍由其他路的錄影撐著）。
    /// </summary>
    public static (DateTime StartUtc, DateTime EndUtc)? GroupBounds(
        IEnumerable<IEnumerable<SegmentRecord>> perChannel)
    {
        DateTime? start = null;
        DateTime? end = null;
        foreach (var channel in perChannel)
        {
            if (Bounds(channel) is not { } b)
            {
                continue;
            }

            if (start is null || b.StartUtc < start)
            {
                start = b.StartUtc;
            }

            if (end is null || b.EndUtc > end)
            {
                end = b.EndUtc;
            }
        }

        return start is { } s && end is { } e ? (s, e) : null;
    }

    /// <summary>
    /// 依格數與焦點頻道，選出壁面要顯示的頻道（焦點排第一格，其餘依清單順序遞補、繞回開頭）。
    /// 純函式，讓「2 路／4 路各顯示哪幾台」有測試可釘。
    /// </summary>
    public static IReadOnlyList<int> WallChannelIds(
        IReadOnlyList<int> orderedChannelIds,
        int focusChannelId,
        int cells)
    {
        if (cells < 1)
        {
            cells = 1;
        }

        if (orderedChannelIds.Count == 0)
        {
            return [];
        }

        var focusIndex = -1;
        for (var i = 0; i < orderedChannelIds.Count; i++)
        {
            if (orderedChannelIds[i] == focusChannelId)
            {
                focusIndex = i;
                break;
            }
        }

        if (focusIndex < 0)
        {
            focusIndex = 0;
        }

        var wanted = Math.Min(cells, orderedChannelIds.Count);
        var result = new List<int>(wanted);
        for (var i = 0; i < wanted; i++)
        {
            result.Add(orderedChannelIds[(focusIndex + i) % orderedChannelIds.Count]);
        }

        return result;
    }
}
