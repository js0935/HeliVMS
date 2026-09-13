/// <summary>警報事件記錄（§4 alarm_events 表；事件中心資料源）。</summary>
public sealed class AlarmEventRecord
{
    public long Id { get; init; }

    public int ChannelId { get; init; }

    /// <summary>事件類型：motion / offline / schedule_start / manual。</summary>
    public string EventType { get; init; } = "motion";

    public DateTime StartUtc { get; init; }

    public DateTime? EndUtc { get; init; }

    /// <summary>事件觸發快照路徑（擇一）。</summary>
    public string? SnapshotPath { get; init; }

    /// <summary>詳細資料（如影像變動比例）。</summary>
    public string? Detail { get; init; }

    public bool Acknowledged { get; init; }
}