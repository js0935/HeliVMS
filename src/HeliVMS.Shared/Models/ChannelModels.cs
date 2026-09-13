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

/// <summary>錄影區段狀態（§4：final／tmp／corrupt）。</summary>
public enum SegmentStatus
{
    /// <summary>錄製中（暫存檔）。</summary>
    Temporary,

    /// <summary>已完成（正式檔）。</summary>
    Final,

    /// <summary>異常（檔案損壞／未正常收尾）。</summary>
    Corrupt,

    /// <summary>已汰除（配額清理）。</summary>
    Retired,
}

/// <summary>頻道資訊（§4 channels 表）。</summary>
public sealed class ChannelInfo
{
    public int Id { get; init; }

    public int? DeviceId { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>主流位址（RTSP URL）。</summary>
    public string MainStreamUrl { get; init; } = string.Empty;

    /// <summary>次流位址。</summary>
    public string SubStreamUrl { get; init; } = string.Empty;

    /// <summary>視訊編碼（h264／h265）。</summary>
    public string Codec { get; init; } = "h264";

    public bool AudioEnabled { get; init; } = true;

    /// <summary>音訊錄製方式（aac 轉碼／copy／none）。</summary>
    public string AudioEncoder { get; init; } = "copy";

    public bool MotionEnabled { get; init; }

    public double MotionSensitivity { get; init; } = 0.5;

    public RecordingMode RecordingMode { get; init; } = RecordingMode.Always;

    public bool Enabled { get; init; } = true;
}

/// <summary>錄影區段索引記錄（§4 segments 表）。</summary>
public sealed class SegmentRecord
{
    public long Id { get; init; }

    public int ChannelId { get; init; }

    /// <summary>來源串流（main／sub）。</summary>
    public string Stream { get; init; } = "main";

    public DateTime StartUtc { get; init; }

    public DateTime? EndUtc { get; init; }

    public string FilePath { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public double? DurationSec { get; init; }

    /// <summary>容器格式。</summary>
    public string Format { get; init; } = "mp4";

    public SegmentStatus Status { get; init; } = SegmentStatus.Final;

    /// <summary>檔案 SHA-256（§11.5 防竄改）。</summary>
    public string? Sha256 { get; init; }
}