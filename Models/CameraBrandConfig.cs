// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Text.Json.Serialization;

namespace HeliVMS.Models;

/// <summary>ONVIF camera brand RTSP path config</summary>
public class CameraBrandConfig {
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("brands")]
    public List<BrandEntry> Brands { get; set; } = [];
}

public class BrandEntry {
    /// <summary>Normalized brand key (lowercase, no spaces)</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>Alias list for matching camera Manufacturer strings (lowercase)</summary>
    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = [];

    /// <summary>Main stream RTSP path</summary>
    [JsonPropertyName("mainPath")]
    public string MainPath { get; set; } = "";

    /// <summary>Sub stream RTSP path (optional)</summary>
    [JsonPropertyName("subPath")]
    public string SubPath { get; set; } = "";

    /// <summary>Number of known camera models for this brand in StrixCamDB</summary>
    [JsonPropertyName("modelCount")]
    public int ModelCount { get; set; }
}
