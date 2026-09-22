using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>VMD 敏感度套用結果（M114，§14.7 #17）。</summary>
public sealed record AppliedSensitivityResult(
    bool Applied,
    int ChannelId,
    double Before,
    double After,
    string Reason)
{
    public static AppliedSensitivityResult Skipped(int channelId, string reason) => new(false, channelId, 0, 0, reason);

    public static AppliedSensitivityResult AppliedNow(int channelId, double before, double after, string reason)
        => new(true, channelId, before, after, reason);
}

/// <summary>
/// VMD 敏感度自動套用（M114，§14.7 #17）：將 <see cref="SensitivityAutoTuner.Suggest"/> 之建議
/// 寫回 <c>channels.motion_sensitivity</c>，逐場景＝每頻道各自套用。套用前檢查頻道存在、VMD 啟用、
/// 且有變動建議；套用即記錄稽核（config．vmd.sensitivity.apply，記 before-&gt;after）。
/// </summary>
public sealed class SensitivityAutoApplier
{
    private readonly ChannelRepository _channels;
    private readonly AuditLogRepository _audit;

    public SensitivityAutoApplier(SqliteStore store)
    {
        _channels = new ChannelRepository(store);
        _audit = new AuditLogRepository(store);
    }

    public AppliedSensitivityResult Apply(SensitivitySuggestion suggestion)
    {
        var channel = _channels.Get(suggestion.ChannelId);
        if (channel is null)
        {
            return AppliedSensitivityResult.Skipped(suggestion.ChannelId, $"頻道 {suggestion.ChannelId} 不存在，跳過套用");
        }

        if (!channel.MotionEnabled)
        {
            return AppliedSensitivityResult.Skipped(suggestion.ChannelId, $"頻道 {suggestion.ChannelId} 未啟用 VMD，跳過");
        }

        if (!suggestion.ChangeRecommended)
        {
            return AppliedSensitivityResult.Skipped(suggestion.ChannelId, suggestion.Reason);
        }

        var before = channel.MotionSensitivity;
        var after = Math.Clamp(suggestion.SuggestedSensitivity, SensitivityAutoTuner.SensitivityMin, SensitivityAutoTuner.SensitivityMax);
        _channels.SetMotionSensitivity(suggestion.ChannelId, after);
        _audit.Record(
            "system",
            "vmd.sensitivity.apply",
            AuditCategories.Config,
            targetType: "channel",
            targetId: suggestion.ChannelId,
            detail: $"{before:F2}->{after:F2}");
        return AppliedSensitivityResult.AppliedNow(suggestion.ChannelId, before, after, suggestion.Reason);
    }
}