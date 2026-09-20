using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>聚合窗統計結果（M54，§14.7 #8）。</summary>
public sealed record AlertTrigger(
    long RuleId,
    string RuleName,
    string? EventType,
    int? ChannelId,
    int WindowCount,
    DateTime FirstUtc,
    DateTime LastUtc,
    bool Reached);

/// <summary>
/// 智慧警報評估器（M54，§14.7 #8）：對窗內 <see cref="AlarmEventRecord"/>（按規則的
/// 頻道／事件類別聚合）計數，跨過 <see cref="AlertRule.MinEventsInWindow"/> 才視為已達
/// 觸發門檻。規則 <see cref="AlertRule.FrameMinutes"/> 為 0 時視為逐筆（不聚合）。
/// </summary>
public static class SmartAlertEvaluator
{
    /// <summary>解析規則的命中事件類型（逗號分隔；null／空白＝全部）。</summary>
    public static ISet<string> ParseMatchTypes(string? matchEventTypes)
    {
        if (string.IsNullOrWhiteSpace(matchEventTypes))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(
            matchEventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>單筆事件是否符合規則（Enabled＋EventType/ChannelId/MatchEventTypes 全 AND）。</summary>
    public static bool Matches(AlertRule rule, AlarmEventRecord record)
    {
        if (!rule.Enabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(rule.EventType) ||
            string.Equals(rule.EventType, record.EventType, StringComparison.OrdinalIgnoreCase))
        {
            if (rule.ChannelId is { } cid && cid != record.ChannelId)
            {
                return false;
            }

            var matchTypes = ParseMatchTypes(rule.MatchEventTypes);
            if (matchTypes.Count > 0 && !matchTypes.Contains(record.EventType))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// 評估窗內事件（nowUtc 往後回推 windowMinutes）對啟用規則的聚合結果；
    /// <paramref name="windowMinutes"/> 為 0 時以整包事件集合為窗（含逐筆規則）。
    /// </summary>
    public static IReadOnlyList<AlertTrigger> Evaluate(
        IReadOnlyList<AlertRule> rules,
        IReadOnlyList<AlarmEventRecord> events,
        DateTime nowUtc,
        int windowMinutes)
    {
        var triggers = new List<AlertTrigger>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled || rule.FrameMinutes <= 0)
            {
                continue;
            }

            var effectiveWindow = windowMinutes > 0 ? windowMinutes : rule.FrameMinutes;
            var fromUtc = nowUtc.AddMinutes(-effectiveWindow);
            var windowEvents = events
                .Where(e => e.StartUtc >= fromUtc && e.StartUtc <= nowUtc && Matches(rule, e))
                .ToList();

            triggers.Add(new AlertTrigger(
                rule.Id,
                rule.Name,
                rule.EventType,
                rule.ChannelId,
                windowEvents.Count,
                windowEvents.Count == 0 ? nowUtc : windowEvents.Min(e => e.StartUtc),
                windowEvents.Count == 0 ? nowUtc : windowEvents.Max(e => e.StartUtc),
                windowEvents.Count >= rule.MinEventsInWindow));
        }

        return triggers;
    }

    /// <summary>該筆現行事件是否應被聚合抑制（未達窗內門檻）。</summary>
    public static bool ShouldSuppress(AlertRule rule, int windowCountIncludingCurrent)
        => rule.FrameMinutes > 0 && windowCountIncludingCurrent < rule.MinEventsInWindow;
}