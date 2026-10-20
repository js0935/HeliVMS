namespace HeliVMS.Storage;

/// <summary>
/// 索引對帳的協調入口：桌面、Headless Agent 與獨立 WebApi 共用同一個入口，理由與
/// 錄影完整性檢查的協調器相同——兩邊各自實作一次，遲早會有一邊漏掉。
///
/// <para>
/// 這裡負責 <see cref="SegmentIndexReconciler"/> 刻意不管的事：依共用設定決定每輪逐檔檢查幾個段、
/// 以及真的標了東西時寫一筆稽核。
/// </para>
/// </summary>
public static class SegmentReconcileCoordinator
{
    /// <summary>
    /// 單輪逐檔檢查的段數設定鍵（0 或負值＝停用背景對帳，供不想讓背景掃描佔用錄影 I/O 的部署關掉）。
    ///
    /// <para>
    /// 沒有掃描游標這種設定：對帳是無狀態的（見 <see cref="SegmentIndexReconciler"/>），
    /// 每輪從最新的一端重來。把游標存進設定會讓每次寫入都留下一筆設定稽核，
    /// 而那個游標既不該被備份、也不該出現在設定版本清單裡。
    /// </para>
    /// </summary>
    public const string BatchSizeKey = "recording.reconcile.batch";

    /// <summary>未設定時的預設逐檔檢查段數。</summary>
    public const int DefaultBatchSize = SegmentIndexReconciler.DefaultBatchSize;

    /// <summary>
    /// 依共用設定跑一輪對帳，並在標了 missing 時寫稽核。
    /// 段數設為 0 或負值時整支停用（回傳空結果）。
    /// </summary>
    /// <param name="store">索引資料庫。</param>
    /// <param name="nowUtc">稽核時間。</param>
    /// <param name="fileExists">檔案存在判定；測試注入替身以脫離真實磁碟。</param>
    /// <param name="isVolumeReady">磁碟區就緒判定；測試注入替身。</param>
    public static ReconcileReport RunConfigured(
        SqliteStore store,
        DateTime nowUtc,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? isVolumeReady = null)
    {
        var settings = new SettingsRepository(store);
        var batch = (int)settings.GetDoubleOrDefault(BatchSizeKey, DefaultBatchSize);
        if (batch <= 0)
        {
            return new ReconcileReport(0, 0, 0);
        }

        var repo = new SegmentRepository(store);
        var report = new SegmentIndexReconciler(repo, isVolumeReady, fileExists).Run(batch);

        if (report.AnyMarked)
        {
            new AuditLogRepository(store).Record(
                "system",
                "recording.missing",
                AuditCategories.Recording,
                detail: $"foldersRemoved={report.FoldersRemoved} missing={report.MarkedMissing} "
                    + $"checked={report.Checked} unverified={report.Unverified} at={nowUtc:O}");
        }

        return report;
    }
}
