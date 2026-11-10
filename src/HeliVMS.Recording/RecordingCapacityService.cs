using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>單一錄影磁碟的量測結果。</summary>
/// <param name="Root">磁碟根（<c>D:\</c>）。</param>
/// <param name="TotalBytes">總容量。</param>
/// <param name="FreeBytes">目前可用空間。</param>
/// <param name="WrittenBytes">量測窗內這顆碟寫入的錄影量。</param>
/// <param name="Basis">速率的分母（見 <see cref="RecordingCapacity.MeasurementBasis"/>）。</param>
/// <param name="Estimate">剩餘可錄時間估算。</param>
public sealed record DriveCapacity(
    string Root,
    long TotalBytes,
    long FreeBytes,
    long WrittenBytes,
    TimeSpan Basis,
    RecordingCapacityEstimate Estimate);

/// <summary>一輪容量量測的完整結果（供設定頁與 API 共用）。</summary>
/// <param name="ReserveBytes">保留水位（<c>recording.min_free_bytes</c>）。</param>
/// <param name="Window">量測窗長。</param>
/// <param name="Drives">逐顆磁碟（依設定的根目錄順序，同一顆只留一筆）。</param>
/// <param name="Total">合計估算（多顆時是加總後再估）。</param>
public sealed record CapacityReport(
    long ReserveBytes,
    TimeSpan Window,
    IReadOnlyList<DriveCapacity> Drives,
    RecordingCapacityEstimate Total);

/// <summary>
/// 剩餘可錄時間的量測入口（桌面設定頁與 Web API 共用）。
/// </summary>
/// <remarks>
/// <para>
/// 輸入全部是實測值：可用空間來自磁碟、寫入量來自 <c>segments</c> 索引的 <c>final</c> 段。
/// 不從「頻道數 × 假設碼率」推——現場攝影機送的是變動位元率，同一台不同段可以差兩倍。
/// </para>
/// <para>
/// 量測窗預設一小時（<see cref="DefaultWindow"/>，與壓力汰除的補充水位同一個窗）：
/// 短於一小時的窗會被 GOP 邊界與分段長度影響（預設分段 300 秒，窗內可能只有 12 段），
/// 長於一小時則對「剛剛開始大量寫入」反應太慢。
/// </para>
/// </remarks>
public static class RecordingCapacityService
{
    /// <summary>量測窗：最近一小時（與 <see cref="RetentionService.ApplyPressure"/> 的補充水位同一個窗）。</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// 量測目前所有錄影磁碟的剩餘可錄時間。<paramref name="usageFor"/> 可注入以固定磁碟組態
    /// （測試不必碰真實磁碟）；<paramref name="nowUtc"/> 決定量測窗的位置。
    /// </summary>
    public static CapacityReport Measure(
        SqliteStore store,
        string? fallbackRoot,
        DateTime nowUtc,
        TimeSpan? window = null,
        Func<string, (long TotalBytes, long FreeBytes)>? usageFor = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var span = window ?? DefaultWindow;
        var settings = new SettingsRepository(store);
        var reserve = RecordingStorage.ParseMinFreeBytes(settings.Get(RecordingStorage.MinFreeSettingKey));
        var repo = new SegmentRepository(store);
        var from = nowUtc - span;

        var drives = new List<DriveCapacity>();
        foreach (var (root, total, free) in DiskPressureCoordinator.ConfiguredDriveUsageDetailed(store, fallbackRoot, usageFor))
        {
            var volume = RecordingVolume.NormalizeRoot(root);
            var write = RecordingVolume.Writes(repo, volume, from, nowUtc);

            // 基準不是「量測窗」而是「實際有寫入涵蓋的範圍」：半小時前才開始錄的機器，
            // 寫入量只有半小時，除以一小時會把速率算成一半、剩餘時間算成兩倍。
            var basis = RecordingCapacity.MeasurementBasis(write.EarliestStartUtc, nowUtc, span);
            drives.Add(new DriveCapacity(
                root,
                total,
                free,
                write.WrittenBytes,
                basis,
                RecordingCapacity.Estimate(free, reserve, write.WrittenBytes, basis)));
        }

        // 合計用逐顆自己的基準（見 EstimateAll 的說明）。
        var overall = RecordingCapacity.EstimateAll(
            drives.Select(d => (d.FreeBytes, d.WrittenBytes, d.Basis)),
            reserve);

        return new CapacityReport(reserve, span, drives, overall);
    }
}
