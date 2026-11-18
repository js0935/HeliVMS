namespace HeliVMS.Shared;

public static class EventTaxonomy
{
    public const string Manual = "manual";
    public const string Schedule = "schedule";
    public const string Motion = "motion";
    public const string Sensor = "sensor";
    public const string Shelter = "shelter";
    public const string Tripwire = "tripwire";
    public const string Perimeter = "perimeter";
    public const string SmartMotion = "smart_motion";
    public const string Offline = "offline";
    public const string Online = "online";
    public const string System = "system";

    public static readonly IReadOnlyList<string> All =
        [Motion, Sensor, Tripwire, Perimeter, SmartMotion, Shelter, Offline, Online, Manual, Schedule, System];

    // ─────────────────────────────────────────────────────────────────────
    // 事件平面分類（參考機 eventlog.type 1–5：System／Operation／Network／Event／Alarm）
    //
    // 這是「事件中心的分類欄」與「CMS 的型別碼」共用的唯一一份口徑。分類<b>衍生自既有資料</b>
    // （警報事件的 event_type、稽核的 category），不新增一欄可以推導的重複值——那種欄位
    // 一定會有一天和來源不一致，而畫面上看不出來。
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>系統（參考機 type 1）。</summary>
    public const string BucketSystem = "system";

    /// <summary>操作（參考機 type 2；人做了什麼）。</summary>
    public const string BucketOperation = "operation";

    /// <summary>網路（參考機 type 3；離線／恢復連線）。</summary>
    public const string BucketNetwork = "network";

    /// <summary>事件（參考機 type 4；動作／智慧分析）。</summary>
    public const string BucketEvent = "event";

    /// <summary>警報（參考機 type 5；IO 乾接點）。</summary>
    public const string BucketAlarm = "alarm";

    /// <summary>分類清單（順序即 UI 與型別碼的順序）。</summary>
    public static readonly IReadOnlyList<string> Buckets =
        [BucketSystem, BucketOperation, BucketNetwork, BucketEvent, BucketAlarm];

    /// <summary>分類的在地化鍵（<c>EventTaxonomy.Bucket.event</c>…）。</summary>
    public static string BucketLabelKey(string bucket) => "EventTaxonomy.Bucket." + bucket;

    /// <summary>
    /// <c>alarm_events.event_type</c> → 事件平面分類。
    /// </summary>
    /// <remarks>
    /// 走 <see cref="CategoryOf"/> 再分組，前綴規則（<c>ai_</c>／<c>io_</c>／<c>smart_</c>）
    /// 因此仍只有一份實作；未來新增的別名只要 CategoryOf 認得，分類就跟著對。
    /// </remarks>
    public static string BucketOf(string? eventType) => CategoryOf(eventType) switch
    {
        Sensor => BucketAlarm,
        Offline or Online => BucketNetwork,
        Manual or Schedule => BucketOperation,
        Motion or Tripwire or Perimeter or SmartMotion or Shelter => BucketEvent,
        _ => BucketSystem,
    };

    /// <summary>
    /// <c>audit_log.category</c> → 事件平面分類。
    /// </summary>
    /// <remarks>
    /// 「誰做了什麼」是操作，「機器自己做的（保留清理／段長修正／磁碟狀態）」是系統。
    /// 稽核目前沒有網路類別——離線／上線是警報事件，由 <see cref="BucketOf"/> 決定。
    /// 未知類別歸系統：寧可放進「系統」讓它出現在清單裡，也不要因為分類看不懂就消失。
    /// </remarks>
    public static string BucketOfAuditCategory(string? category)
    {
        var c = (category ?? "").Trim().ToLowerInvariant();
        return c switch
        {
            "network" => BucketNetwork,
            "auth" or "config" or "export" or "share" or "evidence" or "legal_hold" => BucketOperation,
            _ => BucketSystem,
        };
    }

    /// <summary>事件平面分類 → CMS 型別碼（與 <c>QctekEventTypes</c> 的 1–5 相同口徑）。</summary>
    public static int BucketToCmsTypeCode(string? bucket) => bucket switch
    {
        BucketOperation => 2,
        BucketNetwork => 3,
        BucketEvent => 4,
        BucketAlarm => 5,
        _ => 1,
    };

    public static string LabelKey(string category) => "EventTaxonomy." + category;

    public static string CategoryOf(string? eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return System;

        var e = eventType.Trim().ToLowerInvariant();

        if (e is Motion or Manual or Schedule or Offline or Online or Sensor or Shelter or Tripwire or Perimeter or SmartMotion or System) return e;
        if (e is "schedule_start" or "schedule_recording") return Schedule;
        if (e.StartsWith("io_")) return Sensor;
        if (e is "tamper" or "shelter" or "signal_shelter") return Shelter;
        if (e is "line_cross" or "tripwire" or "smart_pass_line") return Tripwire;
        if (e is "intrusion" or "perimeter" or "smart_aoi_entry" or "smart_aoi_leave") return Perimeter;
        if (e.StartsWith("ai_") || e.StartsWith("smart_") || e is "analytics" or "audio") return SmartMotion;
        if (e is "disconnected") return Offline;
        if (e is "reconnected") return Online;
        return System;
    }

    /// <summary>
    /// 這一筆事件是否只是「連線狀態」（離線／上線），而不是偵測到的事件。
    /// </summary>
    /// <remarks>
    /// 兩者的下游完全不同：偵測事件要進事件錄影閘（Event 模式的頻道靠它開錄）、要能觸發
    /// 智慧聚合與通知；連線狀態事件只屬於事件平面（牆上事件面板、事件中心、CMS 回報）。
    /// 把它們混在一起，最明顯的代價是「攝影機離線」本身會讓 Event 模式的頻道開錄，
    /// 錄到的必然是 corrupt 段；其次是重連時的通知洗版。
    /// <para>
    /// 別名（<c>disconnected</c>／<c>reconnected</c>）也在這裡統一判定——各處自己寫
    /// <c>== "offline"</c> 的比對遲早漏掉其中一種寫法，而症狀只是「某些連線事件照樣去開錄」。
    /// </para>
    /// </remarks>
    public static bool IsConnectivity(string? eventType)
        => CategoryOf(eventType) is Offline or Online;

    public static string ToQccEventType(string category) => category switch
    {
        Motion => "motion", Sensor => "sensor", Tripwire => "tripwire",
        Perimeter => "perimeter", SmartMotion => "smart_motion", Shelter => "shelter",
        Offline or Online => "osc", Manual => "manual", Schedule => "schedule",
        _ => "manual",
    };

    public static int ToCmsTypeCode(string category) => category switch
    {
        Motion or SmartMotion or Tripwire or Perimeter => 4,
        Sensor => 5,
        Offline or Online => 3,
        Manual or Schedule => 2,
        _ => 1,
    };
}