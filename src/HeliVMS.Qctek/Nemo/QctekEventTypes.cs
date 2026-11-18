namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// HeliVMS 事件 → QCTek CMS 的型別碼（<c>QC_Event_Base::GetEventTypeString</c> 認得的值）。
/// </summary>
/// <remarks>
/// <para>
/// CMS 只看型別碼決定「型別」欄位要顯示什麼字串，型別碼不在它認得的集合裡就是空白。
/// 所以對映必須落在 1–5 這五個「事件日誌」型別；200–205 是 VTMS／POS 標籤，HeliVMS
/// 沒有那種資料，查詢那幾個型別一律回 0 筆。
/// </para>
/// <para>
/// <b>不要</b>把攝影機 AI 事件對到 205（Object）看起來比較「像」：那會讓「全部事件」
/// 的查詢用事件日誌的型別 0 去問，而 205 不屬於事件日誌，AI 事件就整批消失在清單裡。
/// </para>
/// </remarks>
public static class QctekEventTypes
{
    /// <summary>系統（<c>[System]</c>）。</summary>
    public const int System = 1;

    /// <summary>操作（<c>[Operation]</c>）。</summary>
    public const int Operation = 2;

    /// <summary>網路（<c>[Network]</c>）。</summary>
    public const int Network = 3;

    /// <summary>事件（<c>[Event]</c>；動作／智慧分析）。</summary>
    public const int Event = 4;

    /// <summary>警報（<c>[Alarm]</c>；IO 乾接點）。</summary>
    public const int Alarm = 5;

    /// <summary>事件日誌型別的上界（CMS 的 <c>StartSearchLog</c> 只在型別 &lt; 0x65 時呼叫）。</summary>
    public const int MaxEventLogType = 100;

    /// <summary>查詢型別 0＝不限（事件日誌全部）。</summary>
    public const int Any = 0;

    /// <summary>是否是事件日誌的型別碼（1–100）。</summary>
    public static bool IsEventLog(int typeCode) => typeCode is >= System and <= MaxEventLogType;

    /// <summary>是否是 VTMS／POS 標籤型別（200–205）；HeliVMS 一律回 0 筆。</summary>
    public static bool IsPosTag(int typeCode) => typeCode is >= 200 and <= 205;

    /// <summary>
    /// 查詢型別是否接受這個事件型別。查詢型別 <see cref="Any"/> 代表「事件日誌全部」，
    /// 但<b>不含</b> POS 標籤（CMS 對 0 會同時查日誌與標籤；我們沒有標籤資料）。
    /// </summary>
    public static bool Matches(int queryType, int entryType)
        => queryType == Any ? IsEventLog(entryType) : queryType == entryType;

    /// <summary>型別碼的中文說明（只用在診斷日誌與測試描述，不進線路）。</summary>
    public static string Label(int typeCode) => typeCode switch
    {
        System => "系統",
        Operation => "操作",
        Network => "網路",
        Event => "事件",
        Alarm => "警報",
        _ => "其他",
    };
}
