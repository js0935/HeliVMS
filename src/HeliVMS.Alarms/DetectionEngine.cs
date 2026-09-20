using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>單一偵測結果（座標為 0..1 正規化，YOLO xywh）。<paramref name="TrackId"/> 由目標追蹤器（§5.7）指派。</summary>
public sealed record Detection(string Class, float Confidence, float X, float Y, float W, float H, int? TrackId = null);

/// <summary>一幀之完整偵測結果（供即時監看疊加）。</summary>
public sealed record DetectionsFrame(DateTime SnapshotUtc, IReadOnlyList<Detection> Items);

/// <summary>推理引擎抽象（§5.2）：輸入解碼幀 → 分類結果清單。</summary>
public interface IDetectionEngine : IDisposable
{
    IReadOnlyList<Detection> Run(VideoFrame frame);
}