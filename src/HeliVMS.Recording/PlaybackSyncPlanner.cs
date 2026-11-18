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
    /// 大格數「只解關鍵影格」總覽牆的漂移容差（秒）。
    /// </summary>
    /// <remarks>
    /// 關鍵影格之間通常隔一個 GOP（實測現場錄影 2–20 秒），所以那一格顯示的時刻本來就會
    /// 落後母鐘最多一個 GOP：用 0.4 秒的容差判定「落後」會變成每一輪都判定要重新對時，
    /// 於是 64 條解碼行程每 150ms 被重開一次——畫面永遠起不來，而且 CPU 全花在啟動 ffmpeg。
    /// 容差必須比 GOP 大，讓那一格安靜地播它的關鍵影格；要精確對時就點那一格放大成單格。
    /// </remarks>
    public const double OverviewDriftToleranceSeconds = 30;

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
    /// 多路回放的<b>群組時刻</b>：所有「畫面上已經有影格」的格之中<b>最慢</b>的那一個。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 這是參考機（QCV8001 web 播放器的 <c>TVTPlayer</c>）的做法，也是這條路上唯一會收斂的做法：
    /// <c>this._showTimestamp = Math.min.apply(null, 所有格的 timestamp)</c>——最慢的那一格定調，
    /// 快的幾格停下來等它。
    /// </para>
    /// <para>
    /// 為什麼不反過來「讓落後的去追」：追趕唯一的手段是重新開一個 ffmpeg 並 seek，而每一次重開都要
    /// 再付一次啟動延遲（實測檔案回放 0.3–1.5 秒，慢的相機來源 2 秒以上）。於是「落後 → 重開 →
    /// 開好又落後」變成一個永遠追不上的迴圈，操作員看到的就是「除了第一格，其他格都慢一拍」，
    /// 中間還夾著黑畫面。停下來等不需要付任何啟動成本，而且慢的那一格本來就是事實：它的解碼
    /// 就是比別人慢，畫面顯示它的時刻才是誠實的。
    /// </para>
    /// </remarks>
    /// <param name="paneInstants">各格畫面上的時刻（還沒有影格的格給 <c>null</c>，不參與）。</param>
    /// <returns>群組時刻；全部皆無影格時回 <c>null</c>。</returns>
    public static DateTime? GroupInstant(IEnumerable<DateTime?> paneInstants)
    {
        DateTime? slowest = null;
        foreach (var instant in paneInstants)
        {
            if (instant is not { } value)
            {
                continue;
            }

            if (slowest is null || value < slowest)
            {
                slowest = value;
            }
        }

        return slowest;
    }

    /// <summary>
    /// 這一格比群組時刻超前多少才要停下來等（秒）；不超前（含它就是最慢的那一格）時回 <see cref="TimeSpan.Zero"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只停「超出容差的那一部分」，並且停在容差的一半處：停在剛好等於容差會讓下一次判定立刻又超標，
    /// 於是每一輪都各停一次（畫面一格一格地頓）；停在半個容差留了餘裕，對時迴圈下一輪就安靜了。
    /// </para>
    /// <para>
    /// 上限 <see cref="MaxAlignmentHoldSeconds"/> 是為了區分兩種完全不同的落後：<b>啟動時間差</b>
    /// （半秒到一兩秒，停一下就會對齊）與<b>壞掉／被搶光的解碼</b>（落後持續變大）。後者若也照停，
    /// 整面牆會跟著它一起凍住——那不是對時，那是當機。超過上限就不再等它，交由停滯判定處理。
    /// </para>
    /// </remarks>
    /// <param name="paneInstant">這一格畫面上的時刻。</param>
    /// <param name="groupInstant">群組時刻（<see cref="GroupInstant"/>）。</param>
    /// <param name="toleranceSeconds">容差（秒）。</param>
    public static TimeSpan AlignmentHold(
        DateTime paneInstant,
        DateTime groupInstant,
        double toleranceSeconds = DefaultDriftToleranceSeconds)
    {
        var ahead = (paneInstant - groupInstant).TotalSeconds;
        if (ahead <= toleranceSeconds)
        {
            return TimeSpan.Zero;
        }

        var hold = Math.Min(ahead - (toleranceSeconds / 2), MaxAlignmentHoldSeconds);
        return hold <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(hold);
    }

    /// <summary>
    /// 單一格的等待時間上限（秒）：超過這個量代表這不是啟動時間差，而是這一格的解碼壞了或被搶光。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="MaxStartupLatencySeconds"/> 同一個數量級，但用途不同：那個是「多久沒有第一張影格
    /// 就當它卡住」，這裡是「願意為它把整面牆停多久」。整面牆停住的代價比單一格落後高得多，所以寧可
    /// 讓壞掉的那一格自己落後（操作員看得到它不動），也不要讓其他格陪它凍住。
    /// </remarks>
    public const double MaxAlignmentHoldSeconds = 5;

    /// <summary>
    /// 這一格該不該<b>重新起播</b>——只涵蓋「它根本沒有在動」的兩種情況。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>落後不是重新起播的理由。</b>落後由「最慢的那一格定調、超前的停下來等」處理（見
    /// <see cref="GroupInstant"/>／<see cref="AlignmentHold"/>），那條路不需要任何啟動成本；
    /// 而重新起播每一次都要再付一次啟動延遲（實測檔案回放 0.3–1.5 秒，慢的來源 2 秒以上），
    /// 正是「除了第一格，其他格都慢一拍」的成因。
    /// </para>
    /// <para>
    /// 留下來的兩種是真的壞掉：<b>等第一張影格等到超過 <see cref="MaxStartupLatencySeconds"/></b>
    /// （ffmpeg 起不來、seek 卡住），以及<b>已經有畫面卻不再更新</b>（解碼行程死了、檔案被鎖住）。
    /// 這兩種不救，整面牆會跟著它一起停住——等待量每一輪都會重算，所以「一直等」等於凍住。
    /// </para>
    /// <para>
    /// 「不再更新」的門檻由 <see cref="StallSeconds"/> 給：只解關鍵影格的總覽牆每 2–20 秒才換一張，
    /// 用一般門檻會把它每一輪都判成卡住而重開（64 條解碼行程的啟動風暴）。
    /// </para>
    /// </remarks>
    /// <param name="waitingForFirstFrame">這一格是否還在等第一張影格。</param>
    /// <param name="secondsSinceStart">從按下播放起算的秒數（等第一張影格時用）。</param>
    /// <param name="secondsSinceLastFrame">距最後一張影格的秒數（已有畫面時用）。</param>
    /// <param name="stallSeconds">「不再更新」的門檻（秒，見 <see cref="StallSeconds"/>）。</param>
    public static bool ShouldRestart(
        bool waitingForFirstFrame,
        double secondsSinceStart,
        double secondsSinceLastFrame,
        double stallSeconds)
        => waitingForFirstFrame
            ? secondsSinceStart > MaxStartupLatencySeconds
            : secondsSinceLastFrame > stallSeconds;

    /// <summary>一般格「不再更新」的門檻（秒）。</summary>
    /// <remarks>
    /// 5 秒＝150ms 的同步週期連續三十幾輪都沒有新影格。比一個正常的 GOP（0.5–2 秒）大得多，
    /// 所以不會把「正在解一個長 GOP」誤判成卡住，又小到整面牆最多只凍住幾秒。
    /// </remarks>
    public const double DefaultStallSeconds = 5;

    /// <summary>只解關鍵影格的格「不再更新」的門檻（秒）：現場錄影的 GOP 實測 2–20 秒。</summary>
    public const double KeyframesOnlyStallSeconds = 30;

    /// <summary>依解碼預算決定「不再更新」的門檻（秒）。</summary>
    public static double StallSeconds(bool keyframesOnly)
        => keyframesOnly ? KeyframesOnlyStallSeconds : DefaultStallSeconds;

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
    /// 依格數與焦點頻道，選出壁面要顯示的頻道（焦點排第一格，其餘照清單順序遞補，不繞回開頭）。
    /// 純函式，讓「2 路／4 路各顯示哪幾台」有測試可釘。
    /// </summary>
    /// <remarks>
    /// 舊版用 <c>(focusIndex + i) % count</c> 從焦點往後繞，焦點不是第一台時各格會變成
    /// <c>5,6,7,8,9,1,2,3,4</c> 這種跳號——操作員回報「排序錯亂」。焦點仍舊必須排在主格
    /// （回放視窗的焦點語意），但其他格照清單由上到下補，因此不論上次看的是哪一台，
    /// 多路配置的相對順序都一致，不會每次開回放都換一個排法。
    /// </remarks>
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

        var wanted = Math.Min(cells, orderedChannelIds.Count);
        var result = new List<int>(wanted);

        // 焦點台已被刪掉時就單純照清單順序，不要硬塞一個不存在的 Id 進主格。
        if (orderedChannelIds.Contains(focusChannelId))
        {
            result.Add(focusChannelId);
        }

        foreach (var id in orderedChannelIds)
        {
            if (result.Count >= wanted)
            {
                break;
            }

            if (id != focusChannelId)
            {
                result.Add(id);
            }
        }

        return result;
    }
}
