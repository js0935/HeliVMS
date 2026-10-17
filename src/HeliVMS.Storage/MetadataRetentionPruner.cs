namespace HeliVMS.Storage;

/// <summary>中繼資料保留清理結果（警報事件／事件錄音／稽核日誌的刪除筆數）。</summary>
public readonly record struct MetadataRetentionResult(long AlarmPurged, long AudioPurged, long AuditPurged);

/// <summary>
/// 中繼資料保管期限清理（不涉及錄影檔）：依保管天數刪除過期的警報事件、事件錄音與稽核日誌。
///
/// 抽成單一入口是因為桌面與 WebApi 兩條保留迴圈都要做這件事，而它們各自持有不同的
/// <see cref="SqliteStore"/> 執行個體。放在 Storage 是因為這裡只碰資料庫、不碰檔案系統。
///
/// 事件錄音（event_audio）掛在警報事件上卻沒有指向 alarm_events 的外鍵，刪事件不會連帶刪錄音，
/// 所以兩者共用同一條警報保管期限；稽核日誌另有自己的保管期限（0＝永久保留）。
/// </summary>
public static class MetadataRetentionPruner
{
    /// <summary>警報事件（連同事件錄音）保管天數設定鍵。WebApi 與桌面保留迴圈共用同一把鍵。</summary>
    public const string AlarmDaysKey = "alarm.retention.days";

    /// <summary>稽核日誌保管天數設定鍵。WebApi 與桌面保留迴圈共用同一把鍵。</summary>
    public const string AuditDaysKey = "audit.retention.days";

    /// <summary>未設定時的預設保管天數（一年）。</summary>
    public const int DefaultDays = 365;

    /// <summary>
    /// 依 <paramref name="alarmDays"/> 清警報事件與事件錄音、依 <paramref name="auditDays"/> 清稽核日誌。
    /// 天數 &lt;= 0 時該項不清理（永久保留）。
    /// </summary>
    public static MetadataRetentionResult Prune(SqliteStore store, DateTime nowUtc, int alarmDays, int auditDays)
    {
        var alarmPurged = alarmDays > 0
            ? new AlarmEventRepository(store).DeleteOlderThan(nowUtc.AddDays(-alarmDays))
            : 0;

        // 事件錄音沒有外鍵，刪事件不會連帶刪錄音；用同一條保管期限按 started_at_utc 清，
        // 連事件早已汰除的孤兒錄音也一併帶走。
        var audioPurged = alarmDays > 0
            ? new EventAudioRepository(store).PruneOlderThan(nowUtc.AddDays(-alarmDays))
            : 0;

        var auditPurged = auditDays > 0
            ? new AuditLogRepository(store).PruneOlderThan(nowUtc.AddDays(-auditDays))
            : 0;

        return new MetadataRetentionResult(alarmPurged, audioPurged, auditPurged);
    }
}
