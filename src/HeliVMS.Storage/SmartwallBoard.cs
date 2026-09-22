namespace HeliVMS.Storage;

/// <summary>看板事件（M106，§14.7 #14 智慧牆警報牆派送）：RuleOrder 為規則序號（小者先進）。</summary>
public sealed record SmartwallBoardEvent(
    long ChannelId,
    string EventType,
    string Priority,
    DateTime OccurredAtUtc,
    int RuleOrder);

/// <summary>看板單格（M106，§14.7 #14）：Highlight＝發生於 <see cref="SmartwallTimings.AlarmHighlight"/> 內。</summary>
public sealed record BoardCell(
    long ChannelId,
    string EventType,
    string Priority,
    int Rank,
    bool Highlight,
    TimeSpan Age);

/// <summary>
/// 智慧牆看板快照引擎（M106，§14.7 #14 警報牆派送，純 BCL）：
/// 「收到告警 → 依 Rule 排序 → queue → 看板；每頻道取保留窗內最新一則；各 tile 顯示該頻道最新
/// 事件；發生 ≤5s 高亮；>300s 移出看板」。
/// 排序＝RuleOrder 升序 → 優先序（low&lt;normal&lt;high&lt;critical）升序 → 發生時間新→舊。
/// </summary>
public static class SmartwallAlertBoard
{
    private static readonly IReadOnlyDictionary<string, int> PriorityOrder = new Dictionary<string, int>
    {
        ["low"] = 0,
        ["normal"] = 1,
        ["high"] = 2,
        ["critical"] = 3,
    };

    public static int RankOf(string priority)
        => PriorityOrder.TryGetValue(priority, out var rank) ? rank : int.MaxValue;

    /// <summary>產生看板快照：每頻道最新一則、RuleOrder/優先序/發生時間排序、至多 <paramref name="maxCells"/> 格。</summary>
    public static IReadOnlyList<BoardCell> Snapshot(
        IEnumerable<SmartwallBoardEvent> events,
        DateTime nowUtc,
        int maxCells)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCells, 1);

        return events
            .Where(e => nowUtc - e.OccurredAtUtc <= SmartwallTimings.MosaicKeepLast)
            .GroupBy(e => e.ChannelId)
            .Select(g => g.OrderByDescending(e => e.OccurredAtUtc).First())
            .OrderBy(e => e.RuleOrder)
            .ThenByDescending(e => RankOf(e.Priority))
            .ThenByDescending(e => e.OccurredAtUtc)
            .Take(maxCells)
            .Select((e, i) => new BoardCell(
                e.ChannelId,
                e.EventType,
                e.Priority,
                i + 1,
                nowUtc - e.OccurredAtUtc <= SmartwallTimings.AlarmHighlight,
                nowUtc - e.OccurredAtUtc))
            .ToList();
    }

    /// <summary>單一頻道於保留窗內的最新事件；無則 null。</summary>
    public static SmartwallBoardEvent? LatestForChannel(
        IEnumerable<SmartwallBoardEvent> events,
        long channelId,
        DateTime nowUtc)
    {
        return events
            .Where(e => e.ChannelId == channelId && nowUtc - e.OccurredAtUtc <= SmartwallTimings.MosaicKeepLast)
            .OrderByDescending(e => e.OccurredAtUtc)
            .FirstOrDefault();
    }
}