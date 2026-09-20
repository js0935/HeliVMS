namespace HeliVMS.Storage;

/// <summary>
/// 告警規則（alert_rules 表；M37＋M54）：一筆規則為「全條件 AND」之篩選器。
/// 命中規則的事件僅會依 Channels 白名單走指定通知通道；規則不存在或欄位
/// 皆為 null 時維持「全通道」現況。EventType/ChannelId/Keyword 為條件；
/// Channels 為逗號分隔之 route 名稱（null＝該事件不受本規則限制）。
/// M54 智慧警報（§14.7 #8）：MatchEventTypes 為逗號分隔已命中者（null＝全部）、
/// FrameMinutes&gt;0 時以「同頻道＋同事件類別」窗內聚合計數跨過
/// MinEventsInWindow 才觸發，避免警報洪泛。
/// </summary>
public sealed record AlertRule(
    long Id,
    string Name,
    string? EventType,
    int? ChannelId,
    string? Keyword,
    string? Channels,
    bool Enabled,
    string? MatchEventTypes = null,
    int FrameMinutes = 0,
    int MinEventsInWindow = 1);