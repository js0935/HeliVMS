using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 失敗原因回填的協調入口：桌面與 Headless Agent 共用，理由與
/// <see cref="SegmentDurationRepairCoordinator"/> 相同——兩者各自實作一次，
/// 遲早有一邊漏掉「依共用設定決定每輪段數」。
/// </summary>
public static class SegmentFailureReasonBackfillCoordinator
{
    /// <summary>單輪掃描段數設定鍵（0 或負值＝停用回填）。</summary>
    public const string BatchSizeKey = "recording.failure_backfill.batch";

    /// <summary>未設定時的預設掃描段數。</summary>
    public const int DefaultBatchSize = SegmentFailureReasonBackfill.DefaultBatchSize;

    /// <summary>
    /// 依共用設定決定每輪掃描段數；0 或負值時整支停用（回傳空結果）。
    /// 分段秒數取自共用設定（歸因窗以它推導）。
    /// </summary>
    public static FailureReasonBackfillReport RunConfigured(SqliteStore store, DateTime nowUtc)
    {
        var batch = (int)new SettingsRepository(store)
            .GetDoubleOrDefault(BatchSizeKey, DefaultBatchSize);

        var backfill = new SegmentFailureReasonBackfill(
            new SegmentRepository(store),
            new AuditLogRepository(store),
            RecordingSegmentPolicy.Read(store));
        return backfill.Run(batch, nowUtc);
    }
}
