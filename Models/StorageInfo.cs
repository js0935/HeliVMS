// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Text.Json.Serialization;

namespace HeliVMS.Models;

/// <summary>Storage space usage info</summary>
public partial class StorageInfo {
    /// <summary>Storage path (root directory)</summary>
    public string StoragePath { get; set; } = "";

    /// <summary>Total disk capacity (bytes)</summary>
    public long TotalBytes { get; set; }

    /// <summary>Free disk space (bytes)</summary>
    public long FreeBytes { get; set; }

    /// <summary>Used disk space (bytes)</summary>
    public long UsedBytes => TotalBytes - FreeBytes;

    /// <summary>Free space percentage</summary>
    public double FreePercent => TotalBytes > 0 ? (double)FreeBytes / TotalBytes * 100 : 0;

    /// <summary>Recording usage per channel</summary>
    public List<CameraStorageUsage> PerCamera { get; set; } = [];

    /// <summary>Total recording data (bytes, from DB statistics)</summary>
    public long TotalRecordingBytes { get; set; }

    /// <summary>Whether disk space is low (&lt; 10%)</summary>
    [JsonIgnore]
    public bool IsLowSpace => FreePercent < 10;

    /// <summary>Formatted free space text</summary>
    public string FreeText => FormatBytes(FreeBytes);

    /// <summary>Formatted total capacity text</summary>
    public string TotalText => FormatBytes(TotalBytes);

    /// <summary>Formatted recording usage text</summary>
    public string RecordingText => FormatBytes(TotalRecordingBytes);

    /// <summary>Free ratio text</summary>
    public string FreePercentText => $"{FreePercent:F1}%";

    private static string FormatBytes(long bytes) {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes < 1024L * 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        return $"{bytes / (1024.0 * 1024 * 1024 * 1024):F2} TB";
    }
}

/// <summary>Recording usage for a single channel</summary>
public class CameraStorageUsage {
    public int ChannelNumber { get; set; }
    public string CameraName { get; set; } = "";
    public string CameraId { get; set; } = "";
    public long TotalBytes { get; set; }
    public int SegmentCount { get; set; }
    public string SizeText => FormatBytes(TotalBytes);

    private static string FormatBytes(long bytes) => StorageInfo.FormatBytesStatic(bytes);
}

// Extension to expose static formatter
public partial class StorageInfo {
    public static string FormatBytesStatic(long bytes) => FormatBytes(bytes);
}
