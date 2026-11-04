using HeliVMS.Shared;
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
    /// 判定「這一格該重新對時」的容許誤差（秒）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 現場回報「多路回放各播各的」：舊值 1.5 秒等於每一格都可以落後主畫面一個半秒以上才被拉回，
    /// 而每次拉回都是重開 ffmpeg（解碼啟動又要幾百毫秒），於是四格各自在自己的節奏上跑。
    /// 0.4 秒約是 10–30fps 下 4–12 張影格的差距，肉眼在同一畫面內看得出來；再縮小則會因為
    /// 解碼啟動抖動而不斷重開，反而更不穩。
    /// </para>
    /// <para>
    /// 光縮小容差會讓「啟動比別人慢」的那一格永遠追不上（每輪都落後同樣的啟動延遲），
    /// 所以必須搭配 <see cref="CompensatedOffset"/>：把該格量到的啟動延遲先加進跳轉位置。
    /// </para>
    /// </remarks>
    public const double DefaultDriftToleranceSeconds = 0.4;

    /// <summary>
    /// 對時跳轉的實際落點：要求位置＋<b>這一格自己的啟動延遲</b>。
    /// </summary>
    /// <remarks>
    /// 從按下播放到第一張影格出現之間要啟動 ffmpeg 並 seek，這段時間畫面是空的；
    /// 若直接跳到要求位置，等到第一張影格出現時已經落後別人那麼多，而且之後是等速播放，
    /// 落後量永遠存在。先把落後量加進跳轉位置，第一張影格就會出現在正確的時刻上。
    /// </remarks>
    /// <param name="offsetSeconds">要求的位置（段內秒數）。</param>
    /// <param name="startupLatencySeconds">該格量到的啟動延遲（未量到時給合理初值）。</param>
    public static double CompensatedOffset(double offsetSeconds, double startupLatencySeconds)
        => Math.Max(0, offsetSeconds + Math.Max(0, startupLatencySeconds));

    /// <summary>
    /// 更新某格的啟動延遲估計值（指數加權移動平均，取樣少的抖動不至於讓下一次跳轉過衝）。
    /// </summary>
    /// <param name="previous">前一次估計（秒）。</param>
    /// <param name="measured">這次量到的延遲（秒）；不合理值會先被夾住。</param>
    public static double NextLatencyEstimate(double previous, double measured)
    {
        var sample = Math.Clamp(measured, 0, MaxStartupLatencySeconds);
        var baseValue = Math.Clamp(previous, 0, MaxStartupLatencySeconds);
        return Math.Clamp((baseValue * 0.5) + (sample * 0.5), 0, MaxStartupLatencySeconds);
    }

    /// <summary>啟動延遲估計的上限（秒）：超過這個值代表機器有更大的問題，不該一路加進跳轉位置。</summary>
    public const double MaxStartupLatencySeconds = 5;

    /// <summary>
    /// 這一格該不該重新對時。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>等待第一張影格期間不做漂移判定</b>：那段時間的「目前時刻」是跳轉目標本身，而跳轉位置
    /// 刻意超前補償了啟動延遲（見 <see cref="CompensatedOffset"/>）——拿它與母鐘比會永遠超出容差，
    /// 於是每一輪都重開 ffmpeg，反而永遠播不起來。等待期間唯一要處理的是「卡住」：
    /// 超過 <see cref="MaxStartupLatencySeconds"/> 還沒有第一張影格就重開一次。
    /// </para>
    /// <para>
    /// 第一張影格到達之後，母鐘也已經往前走了同樣的啟動時間，於是「畫面上的時刻」與母鐘
    /// 只差「實際延遲與估計值的差」——那才是該用容差判定的量。
    /// </para>
    /// </remarks>
    /// <param name="driftSeconds">畫面上的時刻與母鐘的差（秒）。</param>
    /// <param name="waitingForFirstFrame">這一格是否還在等第一張影格。</param>
    /// <param name="secondsSinceStart">從按下播放起算的秒數。</param>
    /// <param name="toleranceSeconds">容差（秒）。</param>
    public static bool ShouldReseek(
        double driftSeconds,
        bool waitingForFirstFrame,
        double secondsSinceStart,
        double toleranceSeconds = DefaultDriftToleranceSeconds)
        => waitingForFirstFrame
            ? secondsSinceStart > MaxStartupLatencySeconds
            : Math.Abs(driftSeconds) > toleranceSeconds;

    /// <summary>
    /// 區段結束時刻。與時間軸色塊、事件比對、跳轉共用 <see cref="PlaybackSegmentTiming.EndOf"/>
    /// 的唯一一份換算：<see cref="SegmentRecord.EndUtc"/> 為 null（錄影中或舊資料）時以段長推算，
    /// 段長也沒有時以 10 秒估算。
    /// </summary>
    /// <remarks>
    /// 這裡原本對「沒有段長」的段回零長度，於是索引還沒回填長度的那一段在同步時被當成空隙：
    /// 色帶上明明有一塊，次畫面卻停住並標示無錄影，看起來像同步壞掉。兩套估算並存正是原因，
    /// 現在只剩一份。
    /// </remarks>
    public static DateTime EndOf(SegmentRecord segment) =>
        PlaybackSegmentTiming.EndOf(segment);

    /// <summary>區段長度（秒），至少 0。</summary>
    public static double LengthOf(SegmentRecord segment) =>
        PlaybackSegmentTiming.LengthOf(segment);

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
