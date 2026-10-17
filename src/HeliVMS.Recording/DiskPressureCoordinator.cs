using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 磁碟滿策略的協調入口（§11.1）。把「目前錄影磁碟的壓力等級」算出來，並在等級<b>改變</b>時
/// 寫一筆稽核，讓磁碟將滿／已滿變成看得到的維運事件，而不是等到想回放時才發現好幾天沒錄。
///
/// <para>
/// 桌面（<c>MainWindow</c> 每小時保留迴圈）與 Headless Agent（<c>RetentionWorker</c>）共用，
/// 與保留清理同一條路徑、同一份設定（錄影根目錄、<c>recording.min_free_bytes</c>），
/// 確保三個程序看到的磁碟判斷一致。
/// </para>
///
/// <para>
/// 只在等級改變時寫稽核（去重）：保留迴圈每小時跑一次，若每圈都寫一筆，一台長期超過 85% 的
/// 機器會把稽核表灌滿無訊息的雜訊，反而把真正的事件淹掉。等級回復正常時同樣會寫一筆，
/// 讓「危機解除」也留得下痕跡。
/// </para>
/// </summary>
public static class DiskPressureCoordinator
{
    /// <summary>上一次回報的壓力等級（去重用的執行期狀態鍵）。</summary>
    public const string LevelKey = "recording.disk_pressure.level";

    /// <summary>稽核動作：磁碟壓力等級改變。</summary>
    public const string AuditAction = "recording.disk_pressure";

    /// <summary>
    /// 目前所有錄影根目錄（磁碟清單＋退回根）各自的容量與可用空間，同一顆磁碟只留一筆。
    /// <paramref name="usageFor"/> 可注入以固定磁碟組態，測試不必碰真實磁碟。
    /// </summary>
    public static IReadOnlyList<(long TotalBytes, long FreeBytes)> ConfiguredDriveUsage(
        SqliteStore store,
        string? fallbackRoot,
        Func<string, (long TotalBytes, long FreeBytes)>? usageFor = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var usage = usageFor ?? RecordingStorage.DriveUsageFor;
        var list = new List<(long, long)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in RecordingStorage.ConfiguredRoots(store, fallbackRoot))
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string key;
            try
            {
                key = Path.GetPathRoot(Path.GetFullPath(root)) ?? root;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                key = root;
            }

            if (seen.Add(key))
            {
                list.Add(usage(root));
            }
        }

        return list;
    }

    /// <summary>
    /// 依共用設定算出目前的壓力等級，不寫任何紀錄。供錄影排程器在每次調和前問「現在能不能開新錄影」。
    /// </summary>
    public static DiskPressureLevel EvaluateConfigured(
        SqliteStore store,
        string? fallbackRoot,
        Func<string, (long TotalBytes, long FreeBytes)>? usageFor = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var settings = new SettingsRepository(store);
        var critical = RecordingStorage.ParseMinFreeBytes(settings.Get(RecordingStorage.MinFreeSettingKey));
        return DiskPressurePolicy.EvaluateOverall(ConfiguredDriveUsage(store, fallbackRoot, usageFor), critical);
    }

    /// <summary>
    /// 是否應該「停新錄」（§11.1 第三階段）：所有設定中的錄影磁碟都低於寫入門檻。
    /// 只要還有一顆寫得下就回 <c>false</c>——多磁碟站台不該因某一顆滿了就停掉全部錄影。
    /// 供錄影排程器在每次調和時判斷。
    /// </summary>
    public static bool ShouldStopNewRecording(
        SqliteStore store,
        string? fallbackRoot,
        Func<string, (long TotalBytes, long FreeBytes)>? usageFor = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var settings = new SettingsRepository(store);
        var critical = RecordingStorage.ParseMinFreeBytes(settings.Get(RecordingStorage.MinFreeSettingKey));
        return DiskPressurePolicy.AllCritical(ConfiguredDriveUsage(store, fallbackRoot, usageFor), critical);
    }

    /// <summary>
    /// 算一次壓力等級，等級與上次不同時寫稽核並更新狀態；回傳本次等級。
    /// </summary>
    public static DiskPressureLevel RunConfigured(
        SqliteStore store,
        string? fallbackRoot,
        DateTime nowUtc,
        Func<string, (long TotalBytes, long FreeBytes)>? usageFor = null,
        AuditLogRepository? audit = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var settings = new SettingsRepository(store);
        var critical = RecordingStorage.ParseMinFreeBytes(settings.Get(RecordingStorage.MinFreeSettingKey));
        var drives = ConfiguredDriveUsage(store, fallbackRoot, usageFor);
        var level = DiskPressurePolicy.EvaluateOverall(drives, critical);

        // 狀態未設時視為「正常」：第一次跑且一切正常不該產生一筆無訊息的事件，
        // 否則全新站台一啟動就會有一筆看起來像告警的磁碟稽核。
        var previous = settings.Get(LevelKey) ?? DiskPressureLevel.Normal.ToString();
        if (!string.Equals(previous, level.ToString(), StringComparison.Ordinal))
        {
            var (total, free, usedPercent) = WorstDrive(drives);
            (audit ?? new AuditLogRepository(store)).Record(
                "system",
                AuditAction,
                AuditCategories.Recording,
                detail: $"level={level.ToString().ToLowerInvariant()} used={usedPercent:0}% free={free} total={total}");
            WriteLevel(store, level, nowUtc);
        }

        return level;
    }

    /// <summary>已用率最高的那顆磁碟（回報最有壓力的一顆）；空集合回 <c>(0,0,0)</c>。</summary>
    private static (long Total, long Free, double UsedPercent) WorstDrive(
        IReadOnlyList<(long TotalBytes, long FreeBytes)> drives)
    {
        var best = (Total: 0L, Free: 0L, UsedPercent: 0.0);
        foreach (var drive in drives)
        {
            var used = DiskPressurePolicy.UsedFraction(drive.TotalBytes, drive.FreeBytes) * 100;
            if (used > best.UsedPercent)
            {
                best = (drive.TotalBytes, drive.FreeBytes, used);
            }
        }

        return best;
    }

    /// <summary>
    /// 以原始 UPSERT 寫狀態，刻意不走 <see cref="SettingsRepository.Set"/>：後者每寫一次就記一筆
    /// <c>settings.set</c> 稽核（給「誰改了設定」用），而這裡是機器自身的執行期狀態，不是使用者變更，
    /// 記進去只會是雜訊。這與 <c>recording.known_roots</c> 等執行期鍵放在同一張表的既有做法一致。
    /// </summary>
    private static void WriteLevel(SqliteStore store, DiskPressureLevel level, DateTime nowUtc)
    {
        store.Execute(
            """
            INSERT INTO app_settings (key, value, updated_at)
            VALUES ($k, $v, $t)
            ON CONFLICT(key) DO UPDATE SET value = $v, updated_at = $t;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$k", LevelKey);
                cmd.Parameters.AddWithValue("$v", level.ToString());
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(nowUtc));
            });
    }
}
