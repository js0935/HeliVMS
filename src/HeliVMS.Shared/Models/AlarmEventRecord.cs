/// <summary>警報事件記錄（§4 alarm_events 表；事件中心資料源）。</summary>
public sealed class AlarmEventRecord
{
    public long Id { get; init; }

    public int ChannelId { get; init; }

    /// <summary>事件類型：motion / offline / online / schedule_start / manual。</summary>
    public string EventType { get; init; } = "motion";

    public DateTime StartUtc { get; init; }

    public DateTime? EndUtc { get; init; }

    /// <summary>事件觸發快照路徑（擇一）。</summary>
    public string? SnapshotPath { get; init; }

    /// <summary>詳細資料（如影像變動比例）。</summary>
    public string? Detail { get; init; }

    public bool Acknowledged { get; init; }

    /// <summary>處置狀態（M38）：pending / acknowledged / actioned / false_alarm。</summary>
    public string Status { get; init; } = "pending";

    /// <summary>指派對象（M38；可空）。</summary>
    public string? AssignedTo { get; init; }

    /// <summary>處置備註（M38；可空）。</summary>
    public string? Note { get; init; }
}