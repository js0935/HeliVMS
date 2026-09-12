namespace HeliVMS.Shared.Models;

/// <summary>
/// 解碼後影格（BGR24，供監看顯示）。
/// </summary>
public sealed class VideoFrame
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>一列像素位元組數（BGR24：Width×3）。</summary>
    public int Stride => Width * 3;

    /// <summary>像素資料（BGR24）。</summary>
    public required byte[] Pixels { get; init; }

    /// <summary>顯示此影格的時間（UTC）。</summary>
    public required DateTime TimestampUtc { get; init; }

    /// <summary>串流內時間戳（毫秒）。</summary>
    public long PtsMs { get; init; }
}