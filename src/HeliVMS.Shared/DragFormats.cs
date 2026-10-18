namespace HeliVMS.Shared;

/// <summary>
/// 跨視窗拖放的自訂資料格式名。
/// </summary>
/// <remarks>
/// 放在 Shared 是為了讓拖放兩端（即時監看是來源、回放是目的）引用同一個常數。
/// 兩邊各自寫字串時，任一邊打錯字都是「拖了完全沒反應」：沒有錯誤、沒有日誌，
/// 而編譯器也不會說話——這正是契約測試要釘住的地方。
/// </remarks>
public static class DragFormats
{
    /// <summary>承載頻道 Id（十進位字串）；即時監看格拖到回放視窗時立即回放該台。</summary>
    public const string ChannelId = "HeliVMS.ChannelId";
}
