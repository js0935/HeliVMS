using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 「這顆磁碟」的共用判斷與量測：根目錄正規化、檔案是否落在某顆碟上、以及某顆碟在
/// 一段時間內實際寫進去的錄影量。
/// </summary>
/// <remarks>
/// <para>
/// 抽出來的理由是只有一份：壓力汰除（<see cref="RetentionService.ApplyPressure"/>）與
/// 剩餘可錄時間估算（<see cref="RecordingCapacityService"/>）問的是同一個問題
/// ——「這顆碟上一小時寫了多少」，兩份實作遲早會對同一顆碟給出不同數字，
/// 而畫面上的兩個數字互相矛盾時，操作員唯一能做的就是兩個都不信。
/// </para>
/// <para>
/// 根目錄一律用 <see cref="Path.GetPathRoot(string)"/> 的口徑（<c>D:\</c>），不是錄影資料夾；
/// 錄影資料夾可能換過（切換磁碟）而磁碟還是同一顆。
/// </para>
/// </remarks>
public static class RecordingVolume
{
    /// <summary>把任意路徑正規化成磁碟根（<c>D:\</c>）；無法解析時回原字串。</summary>
    public static string NormalizeRoot(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>檔案是否落在這顆磁碟上（大小寫不敏感）。</summary>
    public static bool IsOnVolume(string filePath, string volumeRoot)
        => string.Equals(NormalizeRoot(filePath), volumeRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 這顆碟在 <paramref name="fromUtc"/>～<paramref name="toUtc"/> 之間實際寫入的錄影 bytes
    /// （只算 <c>final</c> 段，與汰除候選同一份索引）。
    /// </summary>
    /// <param name="repo">區段索引。</param>
    /// <param name="volumeRoot">磁碟根（<see cref="NormalizeRoot"/> 的輸出）。</param>
    /// <param name="fromUtc">窗起（含）。</param>
    /// <param name="toUtc">窗迄（含）。</param>
    public static long WrittenBytes(
        SegmentRepository repo,
        string volumeRoot,
        DateTime fromUtc,
        DateTime toUtc)
        => Writes(repo, volumeRoot, fromUtc, toUtc).WrittenBytes;

    /// <summary>
    /// 同 <see cref="WrittenBytes"/>，但一併回報窗內<b>最早的段起點</b>。
    /// </summary>
    /// <remarks>
    /// 起點是「換算速率」的必要輸入：一台半小時前才開始錄的機器，寫入量只涵蓋半小時，
    /// 除以一小時會把速率算成真值的一半——「還能錄 8 小時」會在 4 小時後變成真的沒空間。
    /// </remarks>
    public static VolumeWrite Writes(
        SegmentRepository repo,
        string volumeRoot,
        DateTime fromUtc,
        DateTime toUtc)
    {
        ArgumentNullException.ThrowIfNull(repo);

        long written = 0;
        DateTime? earliest = null;
        foreach (var seg in repo.ListFinalBetween(fromUtc, toUtc))
        {
            if (!IsOnVolume(seg.FilePath, volumeRoot))
            {
                continue;
            }

            written += Math.Max(0, seg.SizeBytes);
            if (earliest is null || seg.StartUtc < earliest)
            {
                earliest = seg.StartUtc;
            }
        }

        return new VolumeWrite(written, earliest);
    }

    /// <summary>一顆碟的寫入量與窗內最早起點（見 <see cref="Writes"/>）。</summary>
    /// <param name="WrittenBytes">窗內寫入的錄影 bytes。</param>
    /// <param name="EarliestStartUtc">窗內最早的段起點；沒有段時為 <see langword="null"/>。</param>
    public readonly record struct VolumeWrite(long WrittenBytes, DateTime? EarliestStartUtc);
}
