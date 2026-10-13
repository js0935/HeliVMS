using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>
/// 決定性後備摘要供應商（§14.1 #8）：把 M63 的靜態拼貼 <see cref="SynopsisBuilder.Build"/>
/// 包成 <see cref="IVideoSummaryProvider"/>，讓「外部模型尚未接上」時影片摘要仍可運作並可離線測試。
/// </summary>
/// <remarks>
/// 這是「外部模型縫」的刻意後備：真機可換成以模型／ffmpeg 合成短片的實作而不動 UI；
/// 此實作保證與 M63 既有輸出逐一致，換實作前後既有測試都不需改。
/// </remarks>
public sealed class CollageVideoSummaryProvider : IVideoSummaryProvider
{
    /// <summary>無狀態之共用實例，供 UI 與測試使用。</summary>
    public static CollageVideoSummaryProvider Default { get; } = new();

    /// <inheritdoc />
    public string ModelId => "collage-bmp-v1";

    /// <inheritdoc />
    public SynopsisResult? Generate(SynopsisRequest request, IReadOnlyList<AlarmEventRecord> events, string outputDir)
        => SynopsisBuilder.Build(request, events, outputDir);
}
