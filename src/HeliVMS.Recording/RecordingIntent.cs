using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>不該錄的原因分類（給操作員看的白話）。</summary>
public enum RecordingBlockReason
{
    /// <summary>沒有任何阻擋，本來就該錄。</summary>
    None,

    /// <summary>頻道被停用。</summary>
    ChannelDisabled,

    /// <summary>沒有 RTSP 位址，ffmpeg 會立刻失敗。</summary>
    NoStreamUrl,

    /// <summary>模式是手動：操作員按下去才錄。</summary>
    ManualOnly,

    /// <summary>模式是事件錄影，尚未接上偵測觸發。</summary>
    EventOnly,

    /// <summary>模式是排程，但當下不在任何時段內。</summary>
    OutsideSchedule,
}

/// <summary>
/// 「這個頻道現在該不該錄」的唯一決策來源（§15.1 依觸發模式）。
///
/// 這裡是純函式、不碰 I/O，所以「全時錄影到底有沒有實作」這種問題可以用單元測試回答，
/// 不必真的開 ffmpeg、不必接攝影機。
///
/// 存在的理由：<c>channels.recording_mode</c> 從資料庫建好以來一直沒有任何行為消費者。
/// 設定頁顯示「全時」、預設值也是 Always，但排程器只讀 <c>recording_schedule</c>，
/// 於是預設狀態（沒設任何排程）等於「永遠不錄」。操作員看到頻道勾「全時」就以為在錄，
/// 實際上一段都沒有，而且沒有任何錯誤、沒有警告、log 也是乾淨的。
/// 這是那種「靜靜地什麼都沒發生」的失效，比任何錯誤訊息都難查。
///
/// 決策收攏到這裡，排程器只負責執行結果，不再自己發明規則。
/// </summary>
public static class RecordingIntent
{
    /// <summary>
    /// 判斷頻道在 <paramref name="localNow"/> 這個時間點是否應該錄影。
    /// </summary>
    /// <remarks>
    /// 語意刻意嚴格：<see cref="RecordingMode.Manual"/> 與 <see cref="RecordingMode.Event"/>
    /// 一律回傳 <c>false</c>，就算該頻道剛好有命中當下的排程。
    /// 這不是偷懶，是因為「手動」的意思就是「操作員按下去才錄」；
    /// 讓排程蓋過它，使用者設了手動卻每天照樣被錄滿磁碟，還找不到是哪裡開的。
    /// <see cref="RecordingMode.Event"/> 則要等偵測觸發真的接上，
    /// 在那之前假裝它能錄，只會讓人以為事件錄影是好的。
    /// </remarks>
    public static bool ShouldRecord(
        ChannelInfo channel,
        IEnumerable<RecordingScheduleRecord> schedules,
        DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(schedules);

        // 停用的頻道、沒有 RTSP 位址的頻道都不能開錄：ffmpeg 會立刻失敗，
        // 而排程器每 30 秒就重試一次，等於每 30 秒生一個死掉的行程與一筆失敗記錄。
        if (!channel.Enabled || string.IsNullOrWhiteSpace(channel.MainStreamUrl))
        {
            return false;
        }

        return channel.RecordingMode switch
        {
            RecordingMode.Always => true,
            RecordingMode.Scheduled => schedules.Any(s =>
                s.ChannelId == channel.Id && s.IsActiveAt(localNow)),
            _ => false,
        };
    }

    /// <summary>
    /// 該次開錄寫進稽核的來源標籤。全時與排程必須分開記，
    /// 否則出事時分不出是「使用者設了全時所以本來就該錄」還是「排程把它開起來的」。
    /// </summary>
    public static string GateSourceFor(RecordingMode mode)
        => mode == RecordingMode.Always ? RecordingGateSources.Always : RecordingGateSources.Schedule;

    /// <summary>
    /// 說明某頻道現在為什麼沒在錄。
    /// </summary>
    /// <remarks>
    /// 這是「靜靜地什麼都沒發生」那一類失效唯一能讓操作員自己查出來的線索：
    /// 設定頁勾了「全時」而磁碟上沒有檔案時，需要立刻知道是模式不對、
    /// 頻道被停用、還是根本沒排程。沒有這個方法就只能去讀程式碼。
    /// </remarks>
    public static RecordingBlockReason BlockReasonFor(
        ChannelInfo channel,
        IEnumerable<RecordingScheduleRecord> schedules,
        DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(schedules);

        if (!channel.Enabled)
        {
            return RecordingBlockReason.ChannelDisabled;
        }

        if (string.IsNullOrWhiteSpace(channel.MainStreamUrl))
        {
            return RecordingBlockReason.NoStreamUrl;
        }

        return channel.RecordingMode switch
        {
            RecordingMode.Always => RecordingBlockReason.None,
            RecordingMode.Scheduled => schedules.Any(s => s.ChannelId == channel.Id && s.IsActiveAt(localNow))
                ? RecordingBlockReason.None
                : RecordingBlockReason.OutsideSchedule,
            RecordingMode.Manual => RecordingBlockReason.ManualOnly,
            RecordingMode.Event => RecordingBlockReason.EventOnly,
            _ => RecordingBlockReason.None,
        };
    }
}