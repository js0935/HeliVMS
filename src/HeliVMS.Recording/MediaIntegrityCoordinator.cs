using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 完整性檢查的協調入口（M318）。
///
/// <para>
/// 桌面與 Headless Agent 共用同一個入口，理由與 <see cref="RetentionCoordinator"/> 相同：
/// 兩者各自實作一次，遲早會有一邊漏掉。這裡把「用真實 ffprobe、寫稽核、依共用設定決定每輪段數」
/// 收斂成一個方法，兩個部署模式都只呼叫它。
/// </para>
/// </summary>
public static class MediaIntegrityCoordinator
{
    /// <summary>單輪抽查段數設定鍵（0 或負值＝停用自我檢查）。</summary>
    public const string BatchSizeKey = "recording.integrity.batch";

    /// <summary>未設定時的預設抽查段數。</summary>
    public const int DefaultBatchSize = MediaIntegrityChecker.DefaultBatchSize;

    /// <summary>
    /// 以真實 ffprobe 驗證、寫入稽核，並依共用設定決定每輪抽查段數。
    /// 設定為 0 或負值時整支停用（回傳空結果），供不想讓背景掃描佔用 I/O 的部署關掉。
    /// </summary>
    public static IntegrityCheckReport RunConfigured(
        SqliteStore store, DateTime nowUtc, Func<string, bool?>? isPlayable = null)
    {
        var batch = (int)new SettingsRepository(store)
            .GetDoubleOrDefault(BatchSizeKey, DefaultBatchSize);
        if (batch <= 0)
        {
            return new IntegrityCheckReport(0, 0, 0);
        }

        return Run(store, nowUtc, batch, isPlayable);
    }

    /// <summary>以指定段數執行一輪完整性檢查；測試可注入替身驗證器，脫離真實 ffprobe。</summary>
    public static IntegrityCheckReport Run(
        SqliteStore store,
        DateTime nowUtc,
        int batchSize,
        Func<string, bool?>? isPlayable = null)
    {
        var checker = new MediaIntegrityChecker(
            new SegmentRepository(store),
            isPlayable,
            new AuditLogRepository(store));
        return checker.Run(nowUtc, batchSize);
    }
}
