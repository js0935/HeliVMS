namespace HeliVMS.Recording;

/// <summary>
/// 錄影工作單元。<see cref="SegmentRecorder"/> 的最小契約。
///
/// 存在的唯一理由是讓 <see cref="RecordingScheduler"/> 的「該不該開錄」決策可以被測。
/// 在這個介面出現之前，排程的六條規則沒有任何一支測試能碰到：它們全在
/// <c>ReconcileAsync</c> 裡，而 <c>ReconcileAsync</c> 會直接 <c>new SegmentRecorder</c>()
/// 並啟動 ffmpeg。於是「排程不要對已在監看錄影的頻道再開一台錄影機」這種規則，
/// 只能靠人記得——而它壞掉的症狀是同一頻道兩個 ffmpeg 行程搶同一批檔案，
/// 磁碟用量翻倍、區段檔互相覆蓋，而且沒有任何錯誤。
///
/// 這個介面是 public 而非 internal：<c>HeliVMS.Recording</c> 沒有
/// <c>InternalsVisibleTo</c> 測試 friend assembly，而為了可測性去開一個組譯層級的洞
/// 比讓介面公開更不值得。
/// </summary>
public interface ISegmentRecorder : IAsyncDisposable
{
    /// <summary>開始錄影。<paramref name="rtspUrl"/> 來自頻道設定，<paramref name="recordingsRoot"/> 為錄影根目錄。</summary>
    Task StartAsync(
        int channelId,
        string rtspUrl,
        string recordingsRoot,
        string stream = "main",
        int? segmentSeconds = null);

    /// <summary>停止錄影並收尾；可重複呼叫。</summary>
    Task StopAsync();
}