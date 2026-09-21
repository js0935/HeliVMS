using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>最近窗內某頻道之運動事件信頼統計（§14.7 #17 Auto-VMD）。</summary>
public sealed record AutoTuneWindow(
    int TruePositives,
    int FalsePositives,
    double FalsePositiveRate)
{
    /// <summary>已處置（有回饋價值）之樣本數＝TP＋FP。</summary>
    public int DispositionedCount => TruePositives + FalsePositives;
}

/// <summary>感度調校建議（由呼叫端套用回 <c>channels.motion_sensitivity</c>）。</summary>
public sealed record SensitivitySuggestion(
    int ChannelId,
    double CurrentSensitivity,
    double SuggestedSensitivity,
    bool ChangeRecommended,
    double FalsePositiveRate,
    int TruePositives,
    int FalsePositives,
    string Reason);

/// <summary>
/// 運動感度自動調校（§14.7 #17；承 §5.11 信頼回饋方向）：以最近 windowDays 內該頻道 motion 事件的
/// 處置回饋（誤報/確認）計算誤報率，建議感度增減。語意注意：<see cref="FrameDifferenceMotionDetector"/>
/// 之 sensitivity 是比值門檻（值越大=門檻越嚴=越少觸發）——誤報多應「調高」門檻。
/// </summary>
public sealed class SensitivityAutoTuner
{
    public const int DefaultWindowDays = 14;
    public const double MaxFalsePositiveRate = 0.20;
    public const double Step = 0.05;
    public const int MinDecisionSamples = 5;
    public const int DownshiftSamples = 20;
    public const double SensitivityMin = 0.10;
    public const double SensitivityMax = 0.90;

    private readonly AlarmEventRepository _repo;

    public SensitivityAutoTuner(AlarmEventRepository repo)
    {
        _repo = repo;
    }

    /// <summary>依窗內事件統計 TP／FP／誤報率（pending／無處置之事件不計入）。</summary>
    public static AutoTuneWindow Summarize(IEnumerable<AlarmEventRecord> events)
    {
        var tp = 0;
        var fp = 0;
        foreach (var e in events)
        {
            switch (e.Status)
            {
                case AlarmEventStatus.FalseAlarm:
                    fp++;
                    break;
                case AlarmEventStatus.Acknowledged:
                case AlarmEventStatus.Actioned:
                    tp++;
                    break;
            }
        }

        var rate = tp + fp > 0 ? (double)fp / (tp + fp) : 0.0;
        return new AutoTuneWindow(tp, fp, rate);
    }

    /// <summary>依最近窗內處置回饋產出感度調校建議。</summary>
    public SensitivitySuggestion Suggest(
        int channelId,
        double currentSensitivity,
        DateTime nowUtc,
        int windowDays = DefaultWindowDays,
        string eventType = "motion")
    {
        var from = nowUtc.AddDays(-windowDays);
        var window = Summarize(
            _repo.ListByRange(channelId, from, nowUtc).Where(e => e.EventType == eventType));

        if (window.DispositionedCount < MinDecisionSamples)
        {
            return NoChange(channelId, currentSensitivity, window,
                $"樣本不足：窗內僅 {window.DispositionedCount} 筆已處置事件，需至少 {MinDecisionSamples} 筆。");
        }

        if (window.FalsePositiveRate > MaxFalsePositiveRate)
        {
            return Adjust(channelId, currentSensitivity, window, +Step,
                $"誤報率 {window.FalsePositiveRate:0%} 高於上限 {MaxFalsePositiveRate:0%}，調高感度門檻（減少誤報）。");
        }

        if (window.FalsePositives == 0 && window.TruePositives >= DownshiftSamples)
        {
            return Adjust(channelId, currentSensitivity, window, -Step,
                $"近窗 {window.TruePositives} 筆確認無誤報，調低感度門檻（避免過度遲鈍）。");
        }

        return NoChange(channelId, currentSensitivity, window,
            $"誤報率 {window.FalsePositiveRate:0%} 已達標，維持現值。");
    }

    private static SensitivitySuggestion Adjust(
        int channelId, double current, AutoTuneWindow window, double delta, string reason)
    {
        var suggested = Math.Clamp(current + delta, SensitivityMin, SensitivityMax);
        if (Math.Abs(suggested - current) < 1e-9)
        {
            return NoChange(channelId, current, window, reason);
        }

        return new SensitivitySuggestion(
            channelId, current, suggested, true, window.FalsePositiveRate,
            window.TruePositives, window.FalsePositives, reason);
    }

    private static SensitivitySuggestion NoChange(
        int channelId, double current, AutoTuneWindow window, string reason)
        => new(channelId, current, current, false, window.FalsePositiveRate,
            window.TruePositives, window.FalsePositives, reason);
}