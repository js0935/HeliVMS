namespace HeliVMS.Storage;

/// <summary>
/// 中央站彙整的單筆警報（§14.1 #10，M303）。
///
/// 以子節點 <c>/api/alarm-board</c> 的一列為底，補上來源節點身分，讓跨機看板能分辨
/// 「這筆是第幾台機器的第幾路事件」。<see cref="Overdue"/> 沿用子節點以自身時鐘算出的結果，
/// 不在中央端重算——子節點才知道自己事件的分診狀態與處理時限。
/// </summary>
/// <param name="NodeId">中央站 <c>central_nodes</c> 的節點識別。</param>
/// <param name="NodeName">節點顯示名稱。</param>
/// <param name="EventId">子節點上的事件識別（僅在該節點內唯一）。</param>
/// <param name="ChannelId">子節點上的頻道編號。</param>
/// <param name="EventType">事件類型（motion/line_cross…）。</param>
/// <param name="StartUtc">事件起始時間；子節點未給時為 <see cref="DateTime.MinValue"/>。</param>
/// <param name="Status">處置狀態（pending/acknowledged/actioned/false_alarm）。</param>
/// <param name="Priority">分診優先序（low/normal/high/critical）。</param>
/// <param name="DueUtc">處理時限；未設定為 <c>null</c>。</param>
/// <param name="Owner">負責人；未設定為 <c>null</c>。</param>
/// <param name="AssignedTo">指派對象；未設定為 <c>null</c>。</param>
/// <param name="Overdue">是否逾期（由子節點判定）。</param>
public sealed record CentralAlarm(
    long NodeId,
    string NodeName,
    long EventId,
    int ChannelId,
    string EventType,
    DateTime StartUtc,
    string Status,
    string Priority,
    DateTime? DueUtc,
    string? Owner,
    string? AssignedTo,
    bool Overdue);
