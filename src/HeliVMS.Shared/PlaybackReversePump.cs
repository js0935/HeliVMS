using HeliVMS.Shared.Models;

namespace HeliVMS.Shared;

/// <summary>倒轉的下一步：要顯示哪一段的第幾秒，以及是否已經退回當日開頭。</summary>
/// <param name="Landing">落點（與方向鍵跳轉共用同一份落點規則）。</param>
/// <param name="ReachedStart">已經在當日第一段的開頭：UI 該停在這一格並關掉倒轉，不要繞回最後一段。</param>
public readonly record struct ReverseStep(PlaybackSeekPlanner.Landing Landing, bool ReachedStart);

/// <summary>
/// 倒轉播放的節奏與落點（純函式）。QCV8001 的回放引擎把「快／慢／★倒★／逐格」並列為同一組
/// 播放能力（`QC_Player` 的 `CQC_MultiPlayer`／`CQC_UnitPlayer`，見 `docs/PLAYBACK-ALIGNMENT.md`），
/// 我們長年只有正向——這一支補上「倒」，而且刻意不自己發明落點規則。
/// </summary>
/// <remarks>
/// <para>
/// <b>倒轉不是把速度設成負數。</b> 解碼器只能從關鍵影格往後解，`-ss` 一次就是一次新的解碼
/// 起點；「負速度連續播放」在檔案層做不到（`-vf reverse` 要把整段緩衝進記憶體，一段 60 秒的
/// 1080p 就是 GB 級）。NVR 的倒轉實務上是<b>往回跳著看</b>：每個節拍往後退一小段、顯示那一格，
/// 連續起來就是倒著走。所以這裡輸出的是「下一個落點」，由 UI 用既有的逐格／起播路徑顯示。
/// </para>
/// <para>
/// <b>落點一律走 <see cref="PlaybackSeekPlanner.Resolve"/>（<c>forward: false</c>）。</b>
/// 段內倒退、跨過空隙回到前一段尾端、「已退到第一段之前就停在開頭」，這三件事方向鍵早就
/// 定義好了；倒轉自己再寫一份遲早會在接縫或空隙上給出不同答案（時間軸上明明有那段錄影，
/// 倒轉卻跳過它）。
/// </para>
/// <para>
/// <b>步長與速度的關係</b>：UI 每 250ms 問一次下一步，所以「1×」＝每步倒退 0.25 秒影片
/// ＝每秒倒退 1 秒影片；4× ＝每步 1 秒。夾在 0.25–4 秒之間：比 0.25 秒小就小於一般影格
/// 間距（會停在原地）、比 4 秒大就一次跳掉整個 GOP 以上（倒轉會看起來像在亂跳）。
/// </para>
/// </remarks>
public static class PlaybackReversePump
{
    /// <summary>UI 的倒轉節拍（秒）：每 250ms 一步，也就是每秒 4 步。</summary>
    public const double TickSeconds = 0.25;

    /// <summary>單步最小倒退量（秒）：小於一般影格間距就會停在原地。</summary>
    public const double MinimumStepSeconds = 0.25;

    /// <summary>單步最大倒退量（秒）：再大就會一次跳過整個 GOP 以上。</summary>
    public const double MaximumStepSeconds = 4.0;

    /// <summary>速度下拉支援的範圍（倍率）；超出或非法值一律當 1×。</summary>
    private const double MinSpeed = 0.5;
    private const double MaxSpeed = 16;

    /// <summary>
    /// 連續跳過幾個節拍就放棄倒轉：8 × 250ms ＝ 2 秒。
    /// </summary>
    /// <remarks>
    /// seek 比節拍慢時（大檔、磁碟忙、遠端來源），連發 <c>PlayFrom</c> 只會互相打斷——
    /// 每一步都把上一步的 session 停掉再開一個，畫面反而什麼都看不到。所以上一步的首幀還沒到
    /// 就跳過本節拍；但也不能無限等下去（檔案損壞時首幀永遠不會到），連續跳過 2 秒就收工並說原因。
    /// </remarks>
    public const int MaxSkippedBeats = 8;

    /// <summary>本節拍該不該發步：上一步的首幀還沒到就跳過（<paramref name="previousStepArrived"/>＝沒有在等）。</summary>
    public static bool ShouldSkipBeat(bool previousStepArrived) => !previousStepArrived;

    /// <summary>連續跳過的節拍是否已達放棄門檻。</summary>
    public static bool ShouldGiveUp(int consecutiveSkippedBeats) => consecutiveSkippedBeats >= MaxSkippedBeats;

    /// <summary>單步倒退的影片秒數：速度倍率 × 節拍，夾在允許範圍內。</summary>
    public static double StepSeconds(double speed)
    {
        var multiplier = double.IsFinite(speed) && speed > 0 ? speed : 1;
        var clamped = Math.Clamp(multiplier, MinSpeed, MaxSpeed);
        return Math.Clamp(clamped * TickSeconds, MinimumStepSeconds, MaximumStepSeconds);
    }

    /// <summary>
    /// 算出倒轉的下一步；沒有可播的段時回 <see langword="null"/>。
    /// </summary>
    /// <param name="segments">當日可播的段（允許未排序）。</param>
    /// <param name="playheadUtc">目前播放頭的絕對時刻。</param>
    /// <param name="speed">速度倍率（正數；<c>1</c>＝每秒倒退 1 秒影片）。</param>
    public static ReverseStep? Next(
        IReadOnlyList<SegmentRecord> segments,
        DateTime playheadUtc,
        double speed)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            return null;
        }

        var target = playheadUtc.AddSeconds(-StepSeconds(speed));
        if (PlaybackSeekPlanner.Resolve(segments, target, forward: false) is not { } landing)
        {
            return null;
        }

        // 「已到開頭」＝落點就在當日第一段的第 0 秒。判斷放在這裡而不是 UI：UI 若自己比對，
        // 很容易寫成「播放頭 <= 第一段起點」而漏掉「目標已越過第一段、被吸附回開頭」那一種。
        var first = segments.OrderBy(s => s.StartUtc).First();
        var atStart = landing.Segment.Id == first.Id && landing.OffsetSeconds <= 0;
        return new ReverseStep(landing, atStart);
    }
}
