namespace HeliVMS.Recording;

/// <summary>
/// 錄影停滯的描述：連續幾段沒錄成、原因、以及 ffmpeg 最近的輸出。
///
/// 這是為了對付「攝影機離線但系統看起來正常」。ffmpeg 連不上時錄影迴圈仍在跑
/// （所以 <c>IsRecording</c> 為 true）、稽核也已寫過 recording.start，
/// 磁碟上卻沒有任何檔案。若沒有這條訊息，唯一能發現的方式是有人剛好去看那台
/// 攝影機在不在線——而無人值守的現場不會有人剛好看。
/// </summary>
/// <param name="ChannelId">停滯的頻道。</param>
/// <param name="ConsecutiveFailures">到目前為止連續失敗的段數。</param>
/// <param name="Failure">最近一次例外描述（沒有例外時為空字串）。</param>
/// <param name="FfError">ffmpeg 最近的 stderr，已遮蔽帳密。</param>
public sealed record RecordingStallInfo(
    int ChannelId,
    int ConsecutiveFailures,
    string Failure,
    string FfError);