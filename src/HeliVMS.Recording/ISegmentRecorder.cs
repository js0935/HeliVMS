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
    /// <summary>
    /// 連續多段都沒錄成時觸發一次（恢復成功後才會再觸發下一次）。
    ///
    /// 放在契約裡而不是只有 <see cref="SegmentRecorder"/>，是因為唯一該在意的
    /// 消費者是 <see cref="RecordingScheduler"/>：它要把這個事件寫成稽核，
    /// 而它只能看到介面。少了這裡，「錄影機啟動了卻沒在錄」就永遠沒有記錄。
    /// </summary>
    event EventHandler<RecordingStallInfo>? RecordingStalled;

    /// <summary>
    /// 曾停滯而如今成功錄回檔（參數為頻道 ID）。
    /// </summary>
    /// <remarks>
    /// 同樣必須在契約裡：排程與 UI 都要靠它把「錄影異常」清掉。若只有
    /// <see cref="SegmentRecorder"/> 有，訂閱介面的一方（排程）就收不到，
    /// 警示只能靠逾時猜測——而顯示串流與錄影串流是兩條獨立連線，
    /// 停滯時畫面照常有幀，任何「畫面有動就算好了」的判斷都會立刻誤清警示。
    /// </remarks>
    event EventHandler<int>? RecordingResumed;

    /// <summary>開始錄影。<paramref name="rtspUrl"/> 來自頻道設定，<paramref name="recordingsRoot"/> 為錄影根目錄。</summary>
    Task StartAsync(
        int channelId,
        string rtspUrl,
        string recordingsRoot,
        string stream = "main",
        int? segmentSeconds = null);

    /// <summary>停止錄影並收尾；可重複呼叫。</summary>
    Task StopAsync();

    /// <summary>
    /// 要求「正在錄的這一段」立刻收尾，讓它變成可回放的正式檔。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 回放視窗開啟時用它把「剛剛發生的事」變成看得到的檔案。錄影是「一段一個檔」，
    /// 正在寫的那一段在分段時間（預設 60 秒）到期前不存在於索引裡——操作員按了回放，
    /// 看到的最後一段其實是一分鐘前的事，而畫面上沒有任何線索說明少了什麼。
    /// </para>
    /// <para>
    /// 收檔走的是與「分段時間到期」完全相同的路徑（請 ffmpeg 寫完當前 fragment 再結束），
    /// 所以不會產生第三種檔案狀態；下一段由背景迴圈立刻接上，錄影中斷時間與正常換段相同。
    /// </para>
    /// </remarks>
    /// <param name="timeout">等這一段寫檔、雜湊與索引回寫完成的上限。</param>
    /// <returns>這一段已成為 final 時為 true；沒在錄、剛起段或逾時為 false（回放仍應照常開啟）。</returns>
    Task<bool> CutAsync(TimeSpan timeout);
}