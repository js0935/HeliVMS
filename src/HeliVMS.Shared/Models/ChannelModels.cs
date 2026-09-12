namespace HeliVMS.Shared.Models;

/// <summary>串流種類。</summary>
public enum StreamKind
{
    /// <summary>主流（高解析）。</summary>
    Main,

    /// <summary>次流（低解析、省頻寬）。</summary>
    Sub,
}

/// <summary>錄影模式（§15.1 依觸發模式）。</summary>
public enum RecordingMode
{
    /// <summary>全時錄影。</summary>
    Always,

    /// <summary>依排程錄影。</summary>
    Scheduled,

    /// <summary>事件觸發錄影。</summary>
    Event,

    /// <summary>手動錄影。</summary>
    Manual,
}

/// <summary>錄影區段狀態。</summary>
public enum SegmentStatus
{
    /// <summary>錄製中。</summary>
    Recording,

    /// <summary>已完成。</summary>
    Completed,

    /// <summary>異常（檔案損壞／未正常收尾）。</summary>
    Corrupt,

    /// <summary>已汰除（配額清理）。</summary>
    Retired,
}

/// <summary>頻道資訊（§2：頻道模型）。</summary>
public sealed class ChannelInfo
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>主流位址（RTSP URL）。</summary>
    public string MainStreamUrl { get; init; } = string.Empty;

    /// <summary>次流位址。</summary>
    public string SubStreamUrl { get; init; } = string.Empty;

    public RecordingMode RecordingMode { get; init; } = RecordingMode.Scheduled;

    public bool Enabled { get; init; } = true;
}

/// <summary>錄影區段索引記錄（§15.6 索引與生命週期）。</summary>
public sealed class SegmentRecord
{
    public long Id { get; init; }

    public int ChannelId { get; init; }

    public DateTime StartUtc { get; init; }

    public DateTime? EndUtc { get; init; }

    public string FilePath { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    /// <summary>容器格式（mpegts／mp4…）。</summary>
    public string Format { get; init; } = "mpegts";

    public SegmentStatus Status { get; init; } = SegmentStatus.Completed;
}