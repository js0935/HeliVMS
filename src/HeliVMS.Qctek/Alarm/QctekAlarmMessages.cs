namespace HeliVMS.Qctek.Alarm;

/// <summary>
/// NVR → CMS 警報訊息的文字組裝。
/// </summary>
/// <remarks>
/// <para>
/// 發送端格式由 QCV8001 <c>QC_Alarm</c>（<c>alarm_to_cms.cpp</c>）反編譯還原，
/// 並與 CMS 端 <c>QC_RM_Alarm</c> 的解析器交叉校準：<c>"ALARM_HELLO %d =%s"</c>、
/// <c>"ALARM_MSG %d %d %d %d %d %d %d %d $Name:%s Describe:%s %s $Pri:%d $Live:-1 =%s"</c>。
/// </para>
/// <para>
/// <b>ALARM_MSG 欄位順序的證據鏈</b>（三方獨立吻合，不再「待驗證」）：
/// ①發送端（<c>QC_Alarm</c> 0x18150）以 <c>sprintf_s</c> 的可變參數槽送出
/// <c>[連線id, 相機號, 年(+1900), 月(+1), 日, 時, 分, 秒]</c>——槽序以 DISK_FAILED 的
/// 已知欄位（[id, 年, 月, 日, 時, 分, 秒]）校準；
/// ②事件記錄的來源欄位（+0xd34 秒…+0xd48 年）與 <c>alarm_to_cms</c> 寫入端一致；
/// ③接收端（<c>QC_RM_Alarm</c> 0x4a14 起的讀回段）把 8 個整數解讀成
/// [通道, 旗標, 年(-1900), 月(-1), 日, 時, 分, 秒]——兩邊逐項吻合。
/// </para>
/// <para>
/// <b>保活採「週期性重送 HELLO」</b>：QCV8001 的連線主控迴圈（0x180017a00）每 5 輪
/// 重送一次 HELLO，且每 61 輪檢查「這段期間有沒有送出任何訊息」（沒有就移除連線）。
/// 發送端二進位中<b>不存在</b> <c>ALARM_KEEP_ALIVE</c> 字串（該命令只出現在 CMS 的
/// 接收分派器中）——週期 HELLO 本身就足以維持連線。
/// </para>
/// </remarks>
public static class QctekAlarmMessages
{
    /// <summary>上線問候：<c>ALARM_HELLO &lt;id&gt; =&lt;info&gt;</c>——連上後立即送，之後週期重送當保活。</summary>
    /// <param name="id">連線識別（QCV8001 為每條連線的計數值；HELLO 與該連線上的 ALARM_MSG 用同一個）。</param>
    /// <param name="info">裝置資訊字串（QCV8001 由連線物件 +0x30 取出；對應 <c>=%s</c>）。</param>
    public static string Hello(int id, string info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return $"ALARM_HELLO {id} ={Clean(info)}";
    }

    /// <summary>
    /// 事件回報：<c>ALARM_MSG &lt;連線id&gt; &lt;相機號&gt; &lt;年 月 日 時 分 秒&gt; $Name:… Describe:… …</c>。
    /// </summary>
    /// <param name="connectionId">
    /// 連線識別——與該連線的 <see cref="Hello"/> 同一個值。CMS 用它找回連線格
    /// （<c>QC_RM_Alarm</c> 以 ALARM_MSG 的第一欄比對連線物件 +0x1c）。
    /// </param>
    /// <param name="cameraNumber">
    /// 相機號。QCV8001 送「相機索引 + 1」；HeliVMS 送頻道 id——它跨重啟穩定且唯一，
    /// 語意上就是「哪一台相機」（CMS 端將此欄存入事件記錄尾欄，不用於連線比對）。
    /// </param>
    /// <param name="localTime">事件時刻（本地時間；QCV8001 以 <c>qc_getLocalTime</c> 取當下本地時間）。</param>
    /// <param name="name">事件名稱。</param>
    /// <param name="describe">事件描述。</param>
    /// <param name="priority">優先級（CMS 端存成 0–4，超出會取模）。</param>
    /// <param name="deviceInfo">裝置資訊字串（結尾的 <c>=%s</c>；與 HELLO 用同一個）。</param>
    /// <param name="eventType">事件類型字串（QCV8001 放事件旗標字串；此欄無 <c>$</c> 前綴，CMS 不會誤解析）。</param>
    public static string Msg(
        int connectionId,
        int cameraNumber,
        DateTime localTime,
        string name,
        string describe,
        int priority,
        string deviceInfo,
        string eventType = "")
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(describe);
        ArgumentNullException.ThrowIfNull(deviceInfo);
        ArgumentNullException.ThrowIfNull(eventType);

        // 年份/月/日/時/分/秒：與 CMS 解析器的 pi 讀回段逐項對應（年 -1900、月 -1 的還原）。
        return $"ALARM_MSG {connectionId} {cameraNumber} " +
               $"{localTime.Year} {localTime.Month} {localTime.Day} " +
               $"{localTime.Hour} {localTime.Minute} {localTime.Second} " +
               $"$Name:{Clean(name)} Describe:{Clean(describe)} {Clean(eventType)} " +
               $"$Pri:{priority} $Live:-1 ={Clean(deviceInfo)}";
    }

    /// <summary>
    /// 文字欄位清理：CMS 以 <c>strstr</c> 掃 <c>$</c> 開頭的自訂欄位，
    /// 值裡出現 <c>$</c> 會被當成新欄位（事件名稱直接寫進報表，不能讓它把解析帶偏）；
    /// 換行則會讓單行協議的長度/顯示出錯。其餘字元原樣保留——頻道名可以含空格。
    /// </summary>
    private static string Clean(string value)
        => value.Replace("$", string.Empty, StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
}
