using System.Text;
using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>
/// 智慧牆看板 MQTT 派送（M156，§14.7 #14 告警資訊 MQTT 派送掛載）：把
/// <see cref="SmartwallBoardEvent"/> 序列化成既有 {channel_id,event_type,priority,
/// occurred_at_utc,rule_order} 契約並 PUBLISH 到每頻道 topic；離線以 fake
/// <see cref="IMqttPublisher"/> 可單測；正式＝<see cref="MqttClient"/>。
/// </summary>
public static class SmartwallBoardDispatcher
{
    public const string DefaultTopicPrefix = "helivms/smartwall";

    /// <summary>每頻道 topic：<paramref name="prefix"/>/alerts/{channelId}。</summary>
    public static string TopicFor(string prefix, long channelId)
    {
        var root = string.IsNullOrWhiteSpace(prefix) ? DefaultTopicPrefix : prefix.Trim().TrimEnd('/');
        return $"{root}/alerts/{channelId}";
    }

    /// <summary>Package JSON payload（契約欄位與 M108 通知一致並加 priority/rule_order）。</summary>
    public static byte[] Payload(SmartwallBoardEvent e)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            channel_id = e.ChannelId,
            event_type = e.EventType,
            priority = e.Priority,
            occurred_at_utc = e.OccurredAtUtc.ToString("o"),
            rule_order = e.RuleOrder,
        }));

    /// <summary>看板中處於高亮（發生於 <see cref="SmartwallTimings.AlarmHighlight"/> 內）的頻道集合。</summary>
    public static HashSet<long> HighlightedChannels(IEnumerable<BoardCell> cells)
        => cells.Where(c => c.Highlight).Select(c => c.ChannelId).ToHashSet();

    /// <summary>
    /// 相對前一次快照「新進入高亮」的頻道事件（前次未高亮、這次高亮），依看板 Rank 排序；
    /// 每頻道取保留窗內最新事件以保留 RuleOrder。高亮持續期間不重複，故智慧牆只在告警首次
    /// 發生時派送一次，不會每次輪詢都重送。
    /// </summary>
    public static IReadOnlyList<SmartwallBoardEvent> EnteringHighlight(
        IReadOnlySet<long> previouslyHighlighted,
        IReadOnlyList<BoardCell> current,
        IReadOnlyList<SmartwallBoardEvent> events,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(previouslyHighlighted);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(events);

        var entering = new List<SmartwallBoardEvent>();
        foreach (var cell in current.OrderBy(c => c.Rank))
        {
            if (!cell.Highlight || previouslyHighlighted.Contains(cell.ChannelId))
            {
                continue;
            }

            if (SmartwallAlertBoard.LatestForChannel(events, cell.ChannelId, nowUtc) is { } e)
            {
                entering.Add(e);
            }
        }

        return entering;
    }

    /// <summary>
    /// 以單一別名埠連線（未連時）並 PUBLISH；回 <see cref="MqttPublishResult"/>。
    /// 失敗（host 空白／連線失敗／發布失敗）回 Fail 並附原因（不拋例外）。
    /// </summary>
    public static MqttPublishResult Dispatch(
        IMqttPublisher publisher,
        SmartwallBoardEvent e,
        string host,
        int port,
        string topicPrefix,
        string? user,
        string? password,
        DateTime occurredAtUtc)
    {
        if (publisher is null)
        {
            return MqttPublishResult.Fail("無 MQTT publisher");
        }

        if (string.IsNullOrWhiteSpace(host) || topicPrefix is null)
        {
            return MqttPublishResult.Fail("MQTT 未設定（host/topic 缺少）");
        }

        var topic = TopicFor(topicPrefix, e.ChannelId);
        var payload = Payload(e with { OccurredAtUtc = occurredAtUtc });

        var connected = publisher.Connect(host, port > 0 ? port : 1883, $"helivms-board-{Guid.NewGuid():N}".Substring(0, 20), 30, user, password);
        if (!connected.Ok)
        {
            return MqttPublishResult.Fail(connected.Error ?? "MQTT 連線失敗");
        }

        try
        {
            var published = publisher.Publish(topic, payload);
            return published.Ok ? MqttPublishResult.Success() : MqttPublishResult.Fail(published.Error ?? "MQTT 發布失敗");
        }
        finally
        {
            publisher.Disconnect();
        }
    }
}