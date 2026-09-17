using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// 告警規則比對（M37）：依「第一匹配優先」取第一筆符合條件的啟用規則；
/// 事件類型／頻道／關鍵字為全 AND 條件。命中規則帶 Channels 白名單時，
/// 通知分派僅走該白名單通道（<see cref="NotificationService"/> 使用）。
/// </summary>
public static class AlertRuleMatcher
{
    /// <summary>依規則優先序（id 遞增）回傳第一筆命中，無命中回 null。</summary>
    public static AlertRule? Match(IReadOnlyList<AlertRule> rules, AlarmEventRecord record)
    {
        foreach (var rule in rules)
        {
            if (Matches(rule, record))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>單筆規則是否命中事件（全條件 AND）。</summary>
    public static bool Matches(AlertRule rule, AlarmEventRecord record)
    {
        if (!rule.Enabled)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.EventType) &&
            !string.Equals(rule.EventType, record.EventType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.ChannelId is { } cid && cid != record.ChannelId)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.Keyword) &&
            !record.EventType.Contains(rule.Keyword, StringComparison.OrdinalIgnoreCase) &&
            (record.Detail is null ||
             !record.Detail.Contains(rule.Keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    /// <summary>解析逗號分隔 route 白名單（null/空白＝空集合，表示不限）。</summary>
    public static ISet<string> ParseChannels(string? channels)
    {
        if (string.IsNullOrWhiteSpace(channels))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(
            channels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }
}