namespace HeliVMS.Storage;

/// <summary>SLA 回應時限（M102，§14.7 #3）：依優先序提供目標回應秒數。</summary>
public static class AlarmSla
{
    public const int CriticalSeconds = 300;
    public const int HighSeconds = 900;
    public const int NormalSeconds = 3600;
    public const int LowSeconds = 7200;

    public static int ResponseSeconds(string priority) => priority switch
    {
        AlarmPriority.Critical => CriticalSeconds,
        AlarmPriority.High => HighSeconds,
        AlarmPriority.Normal => NormalSeconds,
        _ => LowSeconds,
    };
}

/// <summary>升階決策結果（M102）：ShouldEscalate=需升階；From/ToPriority 為優先序變化。</summary>
public sealed record AlarmSlaDecision(
    bool ShouldEscalate,
    int Level,
    string FromPriority,
    string ToPriority,
    DateTime NewDueUtc)
{
    public static AlarmSlaDecision None() => new(false, 0, string.Empty, string.Empty, default);
}

/// <summary>
/// 警報升階策略（M102，§14.7 #3，純 BCL）：優先序高一階（low→high、normal→high、high→critical、
/// critical→critical 保持頂階）；升階後新 SLA 截止＝now＋新優先序回應時限。僅「未關案且已逾時」才升階。
/// </summary>
public static class AlarmEscalationPolicy
{
    public static string NextPriority(string priority) => priority switch
    {
        AlarmPriority.Critical => AlarmPriority.Critical,
        AlarmPriority.High => AlarmPriority.Critical,
        AlarmPriority.Normal => AlarmPriority.High,
        _ => AlarmPriority.High,
    };

    /// <summary><paramref name="open"/>＝事件仍 pending/acknowledged（未 actioned/false_alarm）。</summary>
    public static AlarmSlaDecision Decide(string priority, DateTime? dueUtc, DateTime nowUtc, bool open)
    {
        if (!open || dueUtc is not { } due || due >= nowUtc)
        {
            return AlarmSlaDecision.None();
        }

        var next = NextPriority(priority);
        return new AlarmSlaDecision(true, 1, priority, next, nowUtc.AddSeconds(AlarmSla.ResponseSeconds(next)));
    }
}