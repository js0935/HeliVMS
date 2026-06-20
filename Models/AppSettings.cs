// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

namespace HeliVMS.Models;

public class AppSettings
{
    public string RecordingPath { get; set; } = @"D:\HeliVMS\Recordings";
    public bool AutoRecord { get; set; } = false;
    public int SegmentLength { get; set; } = 600;
    public int OnvifPort { get; set; } = 80;
    public int OnvifTimeout { get; set; } = 2;
    public int OverlayFontSize { get; set; } = 18;
    public bool EnableCameraNameOverlay { get; set; } = true;
    public bool EnableTimestampOverlay { get; set; } = true;
    public bool ShowDragDebugPanel { get; set; } = false;

    // Recording retention policy
    public int RetentionDays { get; set; } = 90;
    public int MaxStorageGB { get; set; } = 0;
    public bool EnableAutoPurge { get; set; } = true;

    // Live view layout
    public int LiveViewSplitLayout { get; set; }
    public int LiveViewCurrentPage { get; set; }
}
