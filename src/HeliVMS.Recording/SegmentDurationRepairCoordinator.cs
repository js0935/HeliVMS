using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 段長修正的協調入口（M319）。
///
/// <para>
/// 桌面與 Headless Agent 共用同一個入口，理由與 <see cref="MediaIntegrityCoordinator"/> 相同：
/// 兩者各自實作一次，遲早會有一邊漏掉「依共用設定決定每輪段數」這件事。
/// </para>
/// </summary>
public static class SegmentDurationRepairCoordinator
{
    /// <summary>單輪掃描段數設定鍵（0 或負值＝停用段長修正）。</summary>
    public const string BatchSizeKey = "recording.duration_repair.batch";

    /// <summary>未設定時的預設掃描段數。</summary>
    public const int DefaultBatchSize = SegmentDurationRepair.DefaultBatchSize;

    /// <summary>
    /// 以真實 ffprobe 探測、寫入稽核，並依共用設定決定每輪掃描段數。
    /// 設定為 0 或負值時整支停用（回傳空結果），供不想讓背景掃描佔用 I/O 的部署關掉。
    /// </summary>
    public static DurationRepairReport RunConfigured(
        SqliteStore store, DateTime nowUtc, Func<string, double?>? probeDuration = null)
    {
        var batch = (int)new SettingsRepository(store)
            .GetDoubleOrDefault(BatchSizeKey, DefaultBatchSize);

        var repair = new SegmentDurationRepair(
            new SegmentRepository(store),
            probeDuration,
            new AuditLogRepository(store));
        return repair.Run(batch, nowUtc);
    }
}
