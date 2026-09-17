namespace HeliVMS.Storage;

/// <summary>
/// 告警規則（alert_rules 表；M37）：一筆規則為「全條件 AND」之篩選器。
/// 命中規則的事件僅會依 Channels 白名單走指定通知通道；規則不存在或欄位
/// 皆為 null 時維持「全通道」現況。EventType/ChannelId/Keyword 為條件；
/// Channels 為逗號分隔之 route 名稱（null＝該事件不受本規則限制）。
/// </summary>
public sealed record AlertRule(
    long Id,
    string Name,
    string? EventType,
    int? ChannelId,
    string? Keyword,
    string? Channels,
    bool Enabled);