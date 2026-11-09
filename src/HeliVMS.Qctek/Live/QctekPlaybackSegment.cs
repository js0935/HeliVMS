namespace HeliVMS.Qctek.Live;

/// <summary>
/// 一段可供 QCC 遠端回放的錄影段（由裝置端實作從段索引查詢）。
/// </summary>
/// <param name="ChannelIndex">QCV8001 的 0-based 頻道號。</param>
/// <param name="StartUtc">段開始（UTC）——回放 FRAM 時戳的基準（錄影時刻）。</param>
/// <param name="FilePath">錄影檔路徑（MP4，H.264）。</param>
/// <param name="SizeBytes">檔案大小（OPENFILE 的檔案資訊用；未知回 0）。</param>
public sealed record QctekPlaybackSegment(
    int ChannelIndex,
    DateTime StartUtc,
    string FilePath,
    long SizeBytes = 0);
