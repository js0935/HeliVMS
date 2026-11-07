namespace HeliVMS.Qctek.Alarm;

/// <summary>
/// NVR → CMS 警報訊息的文字組裝。
/// </summary>
/// <remarks>
/// <para>
/// 發送端格式由 QCV8001 <c>QC_Alarm</c>（<c>alarm_to_cms</c>）反編譯還原：
/// <c>"ALARM_HELLO %d =%s"</c>——連上 CMS 的 Alarm Service 後第一個訊息；
/// CMS 會回覆（NVR 側記錄為 <c>HelloPacketR</c>）。
/// </para>
/// <para>
/// 尚未實作的訊息（<c>ALARM_MSG</c>／<c>ALARM_VIDEO_LOSS</c>／<c>ALARM_DISK_FAILED</c>／
/// <c>ALARM_RESET</c>）欄位順序在靜態分析中無法完全確定（可變參數在反編譯中不完整），
/// <b>待動態驗證後再實作</b>——先回錯格式會讓 CMS 寫入錯的事件時間。
/// </para>
/// </remarks>
public static class QctekAlarmMessages
{
    /// <summary>上線問候：<c>ALARM_HELLO &lt;id&gt; =&lt;info&gt;</c>。</summary>
    /// <param name="id">連線識別（QCV8001 為每條連線的計數值）。</param>
    /// <param name="info">裝置資訊字串（QCV8001 由連線物件 +0x30 取出）。</param>
    public static string Hello(int id, string info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return $"ALARM_HELLO {id} ={info}";
    }

    /// <summary>
    /// 存活通知：形狀沿用 <see cref="Hello"/>（CMS 端解析器與 HELLO 走同一條路徑）。
    /// QCV8001 的發送端未在靜態分析中直接出現——待動態驗證。
    /// </summary>
    public static string KeepAlive(int id, string info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return $"ALARM_KEEP_ALIVE {id} ={info}";
    }
}
