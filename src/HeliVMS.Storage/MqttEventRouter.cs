using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>
/// MQTT 事件路由（M103，§14.7 #15）：把 VMS 事件（頻道＋事件型別＋時間）對映為
/// MQTT topic 與 UTF-8 JSON payload，供 HA／自動化平台取用。topic 格式＝
/// <c>&lt;prefix&gt;/ch&lt;channelId&gt;/&lt;eventType&gt;</c>。
/// </summary>
public static class MqttEventRouter
{
    public const string DefaultPrefix = "helivms/events";

    public static string Topic(string? prefix, int channelId, string eventType)
    {
        var p = string.IsNullOrWhiteSpace(prefix) ? DefaultPrefix : prefix.TrimEnd('/');
        var type = (eventType ?? string.Empty).Replace(' ', '_').ToLowerInvariant();
        return $"{p}/ch{channelId}/{type}";
    }

    public static string PayloadJson(int channelId, string eventType, DateTime occurredAtUtc)
    {
        return JsonSerializer.Serialize(new
        {
            channel_id = channelId,
            event_type = eventType,
            ts = SqliteStore.Iso(occurredAtUtc),
        });
    }

    /// <summary>狀態（presence）topic（M111）：<c>&lt;prefix&gt;/status</c>。</summary>
    public static string StatusTopic(string? prefix)
        => $"{((string.IsNullOrWhiteSpace(prefix) ? DefaultPrefix : prefix)).TrimEnd('/')}/status";
}