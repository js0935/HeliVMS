namespace HeliVMS.Qctek.Alarm;

/// <summary>
/// 一筆要回報到 QCTek CMS 的事件（<c>ALARM_MSG</c> 的內容；連線識別由客戶端自行填入）。
/// </summary>
/// <param name="CameraNumber">
/// 相機號：QCV8001 送「相機索引 + 1」；HeliVMS 用頻道 id（穩定且唯一）。
/// </param>
/// <param name="LocalTime">事件時刻（本地時間）。</param>
/// <param name="Name">事件名稱（對應 <c>$Name:</c>）。</param>
/// <param name="Describe">事件描述（對應 <c>Describe:</c>）。</param>
/// <param name="Priority">優先級（對應 <c>$Pri:</c>；CMS 端存成 0–4）。</param>
/// <param name="EventType">事件類型字串（QCV8001 放事件旗標字串；無 <c>$</c> 前綴）。</param>
public sealed record QctekAlarmEvent(
    int CameraNumber,
    DateTime LocalTime,
    string Name,
    string Describe,
    int Priority = 0,
    string EventType = "");
