using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>一次保留清理的彙總結果（配額汰除／暫存隔離／快照汰除／中繼資料保管期限）。</summary>
public readonly record struct RetentionSweepResult(
    long UsedBytes,
    long FreedBytes,
    int DeletedSegments,
    int PurgedTmp,
    long SnapshotFreedBytes,
    int SnapshotDeletedFiles,
    long AlarmPurged,
    long AudioPurged,
    long AuditPurged,
    TemporaryRecoveryReport Recovery = default)
{
    /// <summary>本輪是否有任何東西被移除（供呼叫端決定要不要提示使用者）。</summary>
    public bool AnythingRemoved =>
        DeletedSegments > 0 || PurgedTmp > 0 || SnapshotDeletedFiles > 0 ||
        AlarmPurged > 0 || AudioPurged > 0 || AuditPurged > 0;

    /// <summary>本輪救回幾段「中斷收尾」的錄影（見 <see cref="TemporarySegmentRecovery"/>）。</summary>
    public int RecoveredTmp => Recovery.Recovered;
}

/// <summary>
/// 保留清理協調：把「配額汰除 ＋ 擱置暫存隔離 ＋ 快照汰除 ＋ 中繼資料保管期限」串成一次執行，
/// 讓桌面（<c>MainWindow</c> 每小時迴圈）與 Headless Agent（<c>RetentionWorker</c>）共用同一條路徑。
///
/// 為什麼要抽出來：Agent 過去完全沒有清理迴圈——只錄不刪，服務模式部署下磁碟會無上限成長；
/// 而三項清理若各自散落，任何一條部署路徑漏做其中一項都不會有錯誤、只會在磁碟被寫爆時才發現。
/// 抽成單一入口後，這條串接本身可被單元測試釘住。
/// </summary>
public static class RetentionCoordinator
{
    /// <summary>錄影配額（GB）設定鍵；桌面與 Headless Agent 共用。</summary>
    public const string QuotaGbKey = "recording.quota_gb";

    /// <summary>快照保管天數設定鍵；桌面與 Headless Agent 共用。</summary>
    public const string SnapshotDaysKey = "snapshots.retention_days";

    /// <summary>未設定時的預設配額（GB）。</summary>
    public const double DefaultQuotaGb = 10.0;

    /// <summary>未設定時的預設快照保管天數。</summary>
    public const int DefaultSnapshotDays = 30;

    /// <summary>
    /// 依共用設定（錄影根目錄、配額、快照／警報／稽核保管天數）組裝並執行一輪清理。
    /// 供 Headless Agent 使用：它沒有桌面既有的根目錄／區段索引快取，需每圈自設定重推。
    /// </summary>
    public static RetentionSweepResult RunConfigured(SqliteStore store, string dataRoot, DateTime nowUtc)
    {
        var settings = new SettingsRepository(store);
        var recordingsRoot = RecordingStorage.ResolveConfiguredRoot(
            dataRoot, settings.Get(RecordingStorage.RootSettingKey));

        var segments = new SegmentRepository(store);
        var service = new RetentionService(
            segments,
            recordingsRoot,
            new LegalHoldRepository(store),
            RecordingStorage.ParseKnownRoots(settings.Get(RecordingStorage.KnownRootsSettingKey)));

        var quotaBytes = (long)(settings.GetDoubleOrDefault(QuotaGbKey, DefaultQuotaGb) * 1024 * 1024 * 1024);
        var snapshotDays = (int)settings.GetDoubleOrDefault(SnapshotDaysKey, DefaultSnapshotDays);
        var alarmDays = (int)settings.GetDoubleOrDefault(MetadataRetentionPruner.AlarmDaysKey, MetadataRetentionPruner.DefaultDays);
        var auditDays = (int)settings.GetDoubleOrDefault(MetadataRetentionPruner.AuditDaysKey, MetadataRetentionPruner.DefaultDays);

        return Run(
            store,
            service,
            Path.Combine(dataRoot, "snapshots"),
            quotaBytes,
            snapshotDays,
            alarmDays,
            auditDays,
            nowUtc,
            segments);
    }

    /// <summary>
    /// 執行一輪保留清理。<paramref name="snapshotDays"/> &lt;= 0 時不快照汰除（保守：避免以
    /// 「now 之前全刪」誤刪），<paramref name="alarmDays"/>／<paramref name="auditDays"/> &lt;= 0
    /// 時該項中繼資料永久保留（見 <see cref="MetadataRetentionPruner"/>）。
    /// <para>
    /// <paramref name="segments"/> 為 null 時自 <paramref name="store"/> 建一個區段索引；
    /// 桌面傳入自己那一份（含分片登錄），才不會看不到寫在分片裡的暫存段。
    /// </para>
    /// </summary>
    public static RetentionSweepResult Run(
        SqliteStore store,
        RetentionService service,
        string snapshotsRoot,
        long quotaBytes,
        int snapshotDays,
        int alarmDays,
        int auditDays,
        DateTime nowUtc,
        SegmentRepository? segments = null)
    {
        // 先救援、再清理。順序是契約的一部分：PurgeStaleTmp 會把超過十分鐘的 .tmp 直接刪掉，
        // 先清再救等於每次關程式前的錄影都被判死；反過來先救，救回的段已經改名成正式檔，
        // 保留清理就再也看不到那個 .tmp。
        var recovery = new TemporarySegmentRecovery(segments ?? new SegmentRepository(store)).Run();

        var quota = service.Apply(quotaBytes, nowUtc);
        var snap = snapshotDays > 0
            ? service.PurgeSnapshots(snapshotsRoot, nowUtc.AddDays(-snapshotDays))
            : new SnapshotReport(0, 0);
        var meta = MetadataRetentionPruner.Prune(store, nowUtc, alarmDays, auditDays);

        return new RetentionSweepResult(
            quota.UsedBytes,
            quota.FreedBytes,
            quota.DeletedSegments,
            quota.PurgedTmp,
            snap.FreedBytes,
            snap.DeletedFiles,
            meta.AlarmPurged,
            meta.AudioPurged,
            meta.AuditPurged,
            recovery);
    }
}
