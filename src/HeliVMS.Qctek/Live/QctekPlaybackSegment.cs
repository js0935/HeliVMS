namespace HeliVMS.Qctek.Live;

/// <summary>
/// 一段可供 QCC 遠端回放的錄影段（由裝置端實作從段索引查詢）。
/// </summary>
/// <param name="ChannelIndex">QCV8001 的 0-based 頻道號。</param>
/// <param name="StartUtc">段開始（UTC）——回放 FRAM 時戳的基準（錄影時刻）。</param>
/// <param name="FilePath">錄影檔路徑（MP4，H.264）。</param>
/// <param name="SizeBytes">檔案大小（OPENFILE 的檔案資訊用；未知回 0）。</param>
/// <param name="DurationSeconds">
/// 段的<b>實際</b>長度（秒）。段播完後下一段從這裡接，而不是從固定的 5 分鐘；
/// 0＝未知（退回舊的 300 秒假設）。分段長度可設 5–600 秒，硬寫 5 分鐘會在設定為
/// 60 秒時跳過四分鐘的錄影。
/// </param>
/// <param name="FrameTimes">
/// 檔案自己的封包時間戳（秒，從檔案起點算），由裝置端以 ffprobe 讀出並快取。
/// 空清單或 null＝讀不到（ffprobe 不在、檔案壞掉），FRAM 時戳退回「影格序號 × 33ms」。
/// </param>
public sealed record QctekPlaybackSegment(
    int ChannelIndex,
    DateTime StartUtc,
    string FilePath,
    long SizeBytes = 0,
    double DurationSeconds = 0,
    IReadOnlyList<double>? FrameTimes = null);
