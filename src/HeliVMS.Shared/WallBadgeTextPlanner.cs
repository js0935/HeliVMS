namespace HeliVMS.Shared;

/// <summary>
/// 監看牆格子左下角徽章的文字（純函式）。
/// </summary>
/// <remarks>
/// <para>
/// 徽章是操作員唯一能從「畫面本身」回答「這台會不會錄、現在為什麼沒錄」的地方：
/// 設定頁的錄影方式要點進頻道管理才看得到，而監看牆上有 16 台在跑。過去徽章只寫
/// 名稱與 <c>●REC</c>，於是「排程外」與「全時卻沒錄」在畫面上長得一模一樣——前者是正常，
/// 後者是故障，卻都得點進設定頁才能分辨。
/// </para>
/// <para>
/// 抽成純函式是為了能行為測試：桌面端沒有 WPF 執行期測試，而組字的先後
/// （錄影方式在名稱之後、<c>●REC</c> 最後、沒錄的原因只在沒錄時出現）正是
/// 只能靠掃原始碼才驗得到的部分。
/// </para>
/// </remarks>
public static class WallBadgeTextPlanner
{
    /// <summary>組出徽章文字：<c>名稱 · 錄影方式 ●REC（沒錄的原因）</c>。</summary>
    /// <param name="channelName">頻道名稱。</param>
    /// <param name="modeLabel">
    /// 錄影方式的短標籤（全時／排程／事件／手動）。<see langword="null"/> ＝ 查不到頻道或未知模式，
    /// 此時只寫名稱：徽章上的資訊是操作員據以判斷的依據，猜一個比不寫更糟。
    /// </param>
    /// <param name="recording">目前是否真的有錄影工作階段（<c>ChannelManager.IsRecording</c>）。</param>
    /// <param name="blockLabel">
    /// 這個頻道現在「為什麼沒錄」（排程外／頻道已停用…）。錄影中一律忽略——兩者同時出現
    /// 只會讓操作員懷疑哪一句才是真的。與錄影方式重複的原因（事件模式的「僅事件錄影」）
    /// 由呼叫端先濾掉，這裡只負責排版。
    /// </param>
    public static string Compose(
        string channelName,
        string? modeLabel,
        bool recording,
        string? blockLabel)
    {
        var text = channelName;
        if (!string.IsNullOrEmpty(modeLabel))
        {
            text += $" · {modeLabel}";
        }

        if (recording)
        {
            return text + " ●REC";
        }

        return string.IsNullOrEmpty(blockLabel) ? text : $"{text}（{blockLabel}）";
    }
}
