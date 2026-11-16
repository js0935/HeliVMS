using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 「磁碟滿了就淘汰最舊錄影」的閘門（§11.1 磁碟滿策略第四階段，即時版）。
///
/// <para>
/// 錄影排程器每 30 秒問一次「現在能不能開新錄影」，而過去的答案是純查詢：低於寫入門檻就
/// <b>停新錄</b>，等每小時的保留迴圈（<see cref="RetentionCoordinator"/>）才第一次真的刪檔。
/// 實測現場：12:00:51 進入 <c>recording.disk_full</c>、12:39:55 才解除，也就是整台機器
/// 有 39 分鐘明明有可汰除的舊錄影、卻一段新的都沒錄——那 39 分鐘的畫面永遠補不回來。
/// </para>
/// <para>
/// 現在閘門自己會做第四階段：所有錄影磁碟都低於門檻時，先從最舊的 final 段開始淘汰
/// （<see cref="RetentionService.ApplyPressure"/>），再以<b>刪完之後的磁碟實況</b>回答。
/// 保留鎖定的證據仍然豁免——那是保全的錄影，不會為了騰空間被刪。
/// </para>
/// <para>
/// 刪不出空間時（全部受保存鎖定、檔案被佔用、真的沒東西可刪）才回報停新錄；這種情況
/// 以 <see cref="DefaultMinInterval"/> 節流，避免排程器每 30 秒重掃一次索引與磁碟——每次掃描
/// 都要走一次 SQLite 查詢與目錄 I/O，徒增負載卻不會改變結論。成功刪出空間的那一輪不節流。
/// </para>
/// </summary>
public sealed class DiskPressureRelief
{
    /// <summary>「刪不出空間」之後多快才能再試一次（成功淘汰的那一輪不受此限）。</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMinutes(5);

    /// <summary>稽核動作：因磁碟空間不足而淘汰最舊錄影。</summary>
    public const string EvictionAuditAction = "recording.pressure_evicted";

    private readonly Func<SqliteStore, string, RetentionService> _serviceFor;
    private readonly TimeSpan _minInterval;
    private readonly Func<DateTime> _clock;

    /// <summary>上一次「刪不出空間」的時刻；<see cref="DateTime.MinValue"/> 表示沒有待節流的嘗試。</summary>
    private DateTime _lastFutileAttemptUtc = DateTime.MinValue;

    /// <param name="serviceFor">
    /// 壓力汰除服務的建立方式；測試可注入以計數「有沒有真的嘗試淘汰」。
    /// </param>
    /// <param name="minInterval">刪不出空間後的重試間隔。</param>
    /// <param name="clock">時鐘（測試可固定）。</param>
    public DiskPressureRelief(
        Func<SqliteStore, string, RetentionService>? serviceFor = null,
        TimeSpan? minInterval = null,
        Func<DateTime>? clock = null)
    {
        _serviceFor = serviceFor ?? (static (store, root) => new RetentionService(
            new SegmentRepository(store),
            root,
            new LegalHoldRepository(store)));
        _minInterval = minInterval ?? DefaultMinInterval;
        _clock = clock ?? (static () => DateTime.UtcNow);
    }

    /// <summary>
    /// 是否需要「停新錄」。與 <see cref="DiskPressureCoordinator.ShouldStopNewRecording"/> 的
    /// 差別是：所有磁碟都低於門檻時，這裡會<b>先淘汰最舊的錄影</b>再回答。
    /// </summary>
    /// <param name="store">共用索引資料庫。</param>
    /// <param name="recordingsRoot">目前使用的錄影根目錄（磁碟清單未設定時的退回根）。</param>
    /// <param name="usageFor">磁碟容量讀取；注入可固定磁碟組態，測試不必碰真實磁碟。</param>
    /// <param name="audit">稽核寫入；null 時自 <paramref name="store"/> 建立。</param>
    public bool ShouldStopNewRecording(
        SqliteStore store,
        string recordingsRoot,
        Func<string, (long TotalBytes, long FreeBytes)>? usageFor = null,
        AuditLogRepository? audit = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingsRoot);

        var settings = new SettingsRepository(store);
        var critical = RecordingStorage.ParseMinFreeBytes(settings.Get(RecordingStorage.MinFreeSettingKey));
        var drives = DiskPressureCoordinator.ConfiguredDriveUsageDetailed(store, recordingsRoot, usageFor);

        // 只要還有一顆磁碟寫得下就不擋：多磁碟站台不該因為某一顆滿了就停掉全部錄影。
        if (!DiskPressurePolicy.AllCritical(
                drives.Select(d => (d.TotalBytes, d.FreeBytes)),
                critical))
        {
            return false;
        }

        var now = _clock();
        if (_lastFutileAttemptUtc != DateTime.MinValue && now - _lastFutileAttemptUtc < _minInterval)
        {
            return true;
        }

        RetentionService.PressureReport report;
        try
        {
            report = _serviceFor(store, recordingsRoot).ApplyPressure(now, critical, drives, usageFor);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            // 讀不到磁碟狀態／刪不掉檔案時保守地停新錄（與既有閘門同一立場），下一輪再試。
            _lastFutileAttemptUtc = now;
            return true;
        }

        if (report.DeletedSegments > 0)
        {
            // 錄影自己消失而沒有任何紀錄，操作員只會認為程式亂刪檔。稽核是唯一的痕跡。
            _lastFutileAttemptUtc = DateTime.MinValue;
            (audit ?? new AuditLogRepository(store)).Record(
                "system",
                EvictionAuditAction,
                AuditCategories.Recording,
                detail: $"segments={report.DeletedSegments} freed={report.FreedBytes}");
        }
        else
        {
            _lastFutileAttemptUtc = now;
        }

        // 以刪完之後的磁碟實況回答，而不是 ApplyPressure 的 StillCritical：
        // 那個旗標問的是「有沒有達到『門檻＋一小時寫入量』的補充水位」，而能不能開新錄影
        // 問的是「有沒有高於門檻」。只剩少量可汰除的段時前者到不了、後者卻已經成立，
        // 兩者混用會讓「明明已經騰出空間」的機器繼續停錄。
        return DiskPressureCoordinator.ShouldStopNewRecording(store, recordingsRoot, usageFor);
    }
}
