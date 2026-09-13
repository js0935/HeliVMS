namespace HeliVMS.Shared.Models;

/// <summary>
/// 單筆 AI 物件偵測記錄（§4 detections 表；全量 metadata 可查）。
/// 座標為 0..1 正規化（YOLO xywh），與 <c>HeliVMS.Alarms.Detection</c> 對應。
/// </summary>
public sealed record DetectionRecord
{
    public long Id { get; init; }

    public int ChannelId { get; init; }

    public string Class { get; init; } = "";

    public float Confidence { get; init; }

    public float X { get; init; }

    public float Y { get; init; }

    public float W { get; init; }

    public float H { get; init; }

    /// <summary>偵測幀時間（UTC）。</summary>
    public DateTime DetectedUtc { get; init; }
}