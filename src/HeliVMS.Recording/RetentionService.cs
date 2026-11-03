using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>配額清理執行報告。</summary>
public sealed record RetentionReport(long UsedBytes, long FreedBytes, int DeletedSegments, int PurgedTmp);

/// <summary>快照保留清理執行報告。</summary>
public sealed record SnapshotReport(long FreedBytes, int DeletedFiles);

/// <summary>
/// 錄影配額策略（§9 儲存管理）：總量超過配額時，依開始時間由最舊開始刪除 final
/// 區段（檔＋資料庫列），並清理擱置之 *.tmp 殘檔（異常錄影中斷遺留）。
/// 亦負責快照保留清理（§9：snapshots/ 依檔案時間刪除過期快照，M20）。
/// </summary>
public sealed class RetentionService
{
    private const double StaleTmpMinutes = 10;

    private readonly SegmentRepository _repo;
    private readonly IReadOnlyList<string> _recordingsRoots;
    private readonly string _recordingsRoot;
    private readonly LegalHoldRepository? _legalHolds;

    /// <param name="repo">區段索引。</param>
    /// <param name="recordingsRoot">目前使用的錄影根目錄。</param>
    /// <param name="legalHolds">保存鎖定查詢；null 表示不套用保存鎖定。</param>
    /// <param name="additionalRoots">
    /// 以前用過的錄影根目錄（切換磁碟後保留）。區段檔以絕對路徑存在資料庫中，若只認目前的根目錄，
    /// 切換磁碟後舊根上的錄影會被視為「不在根目錄內」而拒絕刪除，配額清理就永遠卡在超額。
    /// </param>
    public RetentionService(
        SegmentRepository repo,
        string recordingsRoot,
        LegalHoldRepository? legalHolds = null,
        IReadOnlyList<string>? additionalRoots = null)
    {
        _repo = repo;
        _recordingsRoot = Path.GetFullPath(recordingsRoot);
        var roots = new List<string> { _recordingsRoot };
        if (additionalRoots is not null)
        {
            foreach (var root in additionalRoots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var full = Path.GetFullPath(root);
                if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(full);
                }
            }
        }

        _recordingsRoots = roots;
        _legalHolds = legalHolds;
    }

    /// <summary>目前使用的錄影根目錄（壓力汰除的預設磁碟來源）。</summary>
    public string RecordingsRoot => _recordingsRoot;

    /// <summary>
    /// 執行一次總量上限（<paramref name="quotaBytes"/>）汰除與 tmp 隔離；回傳結果報告。
    /// 保存鎖定（M66）所覆蓋時段豁免汰除。
    /// <para>
    /// <paramref name="quotaBytes"/> &lt;= 0 表示停用總量汰除：產品路徑（桌面與 Headless Agent）
    /// 現在都傳 0，空間治理改由 <see cref="ApplyPressure"/> 的「磁碟保留空間」負責。
    /// 這個參數保留給仍想以總量上限管理的呼叫端與測試。
    /// </para>
    /// </summary>
    public RetentionReport Apply(long quotaBytes, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;

        // 汰除前先做一次資料夾層級對帳（只認 final 資料夾，不逐檔 stat）。
        //
        // 為什麼一定要在「刪檔」前做：GetTotalUsage 只讀 segments 表的 final 段，外部刪檔後
        // 那個數字會虛高；若直接拿它判斷超額，就會為了把「索引數字」壓到配額以下而刪掉磁碟上
        // 真正還在的錄影——磁碟從來沒滿，卻刪掉了調查證據。先對帳讓用量反映磁碟實況，
        // 才不會用幽靈位元組逼真的檔案讓路。對帳失敗不影響清理主線，退回未對帳的口徑。
        try
        {
            new SegmentIndexReconciler(_repo).ReconcileFolders();
        }
        catch (Exception)
        {
            // 磁碟忙碌、權限或庫被鎖：用現有索引數字繼續，下一圈再對帳。
        }

        long usage = _repo.GetTotalUsage();
        long freed = 0;
        var deleted = 0;

        // 配額停用（quotaBytes <= 0）：空間治理改由「磁碟保留空間」（min_free 壓力汰除）接手後，
        // 總量上限不再是設定面的一部分。0 或負值＝不依總量汰除——否則 while (usage > 0)
        // 會把磁碟上的錄影汰到一滴不剩。仍保留對帳與暫存清理。
        if (quotaBytes > 0)
        {
            while (usage > quotaBytes)
            {
                // 汰除候選改依配額優先序（事件段先、全時段後，同層由舊至新；見 ListReclaimCandidates）。
                // 這是 §11.1 磁碟滿策略的第二階段；以前一律只看最舊，等於對事件段與全時段一視同仁。
                var candidates = _repo.ListReclaimCandidates(64);
                SegmentRecord? target = null;

                // 逐一挑第一個「大小已知、未受保存鎖定、且檔案真的刪得掉」的區段。
                //
                // 檔案刪不掉時（被防毒／播放器占用，或路徑汙染成不在錄影根目錄內）要「跳過」，
                // 不能就此結束整輪：最舊的一個搬不動的檔案不該讓配額永遠降不下來、磁碟被寫爆。
                // 刪不掉的索引列一律保留，下一輪再試。
                foreach (var seg in candidates)
                {
                    if (seg.SizeBytes <= 0)
                    {
                        continue;
                    }

                    if (_legalHolds?.IsLocked(seg.ChannelId, seg.StartUtc, seg.EndUtc ?? seg.StartUtc) == true)
                    {
                        continue;
                    }

                    if (!TryDeleteSegmentFile(seg.FilePath))
                    {
                        continue;
                    }

                    target = seg;
                    break;
                }

                if (target is null)
                {
                    // 這一批已無可刪目標（全數受鎖定、待收尾或刪不掉）：結束本輪。
                    break;
                }

                _repo.Delete(target.Id);
                usage -= target.SizeBytes;
                freed += target.SizeBytes;
                deleted++;
            }
        }

        var purgedTmp = PurgeStaleTmp(now);
        return new RetentionReport(usage, freed, deleted, purgedTmp);
    }

    /// <summary>壓力汰除的結果。</summary>
    /// <param name="DeletedSegments">本輪為騰出空間刪掉的最舊段數。</param>
    /// <param name="FreedBytes">本輪釋放的位元組數。</param>
    /// <param name="StillCritical">刪完所有可汰除的段之後，可用空間仍低於門檻（例如全被保存鎖定）。</param>
    public sealed record PressureReport(int DeletedSegments, long FreedBytes, bool StillCritical);

    /// <summary>壓力汰除的補充水位下限：連最近一小時都沒有寫入量可估時，至少騰出這麼多。</summary>
    public static readonly long PressureRefillBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// 壓力汰除（磁碟滿策略第四步）：可用空間低於寫入門檻的磁碟，從最舊的 final 段開始刪，
    /// 直到可用空間回升到「門檻 ＋ 最近一小時的錄影寫入量」。
    ///
    /// <para>
    /// 為什麼要有這一步：配額清理只看「錄影總量 vs 配額」——配額設得比磁碟大、或磁碟被
    /// 錄影以外的資料吃掉時，配額永遠不觸發，而第三階段的「停新錄」會讓系統卡在
    /// 「不刪舊檔、也不開新錄」，看起來就是壞掉。NVR 的標準行為是循環錄影：空間滿了
    /// 先淘汰最舊的。保存鎖定（M66）仍然豁免——被保全的錄影不會為了騰空間被刪。
    /// </para>
    /// <para>
    /// 目標不是「剛好脫離門檻」：只騰幾 MB 的話，下一個小時的錄影又把它吃回門檻以下，
    /// 每小時都要刪一次。以「最近一小時實際寫進這顆磁碟的量」為補充水位，一口氣騰出
    /// 能再撐一小時的空間；最近沒有寫入（事件錄影稀疏）時至少騰 <see cref="PressureRefillBytes"/>。
    /// 刪完全部可汰除的段仍不足時回報 <see cref="PressureReport.StillCritical"/>，
    /// 由既有的「停新錄」階段接手（這時真的沒有東西可刪了）。
    /// </para>
    /// </summary>
    /// <param name="nowUtc">現在時間（估計最近一小時寫入量與游標起點）。</param>
    /// <param name="criticalFreeBytes">寫入門檻（<c>recording.min_free_bytes</c>）。</param>
    /// <param name="drives">各錄影磁碟的（根路徑、容量、可用空間）。</param>
    /// <param name="usageFor">
    /// 可用空間讀取；預設真實磁碟。注入可固定磁碟組態，測試不必碰真實磁碟。
    /// </param>
    public PressureReport ApplyPressure(
        DateTime nowUtc,
        long criticalFreeBytes,
        IReadOnlyList<(string Root, long TotalBytes, long FreeBytes)> drives,
        Func<string, (long TotalBytes, long FreeBytes)>? usageFor = null)
    {
        ArgumentNullException.ThrowIfNull(drives);
        var usage = usageFor ?? RecordingStorage.DriveUsageFor;

        var deleted = 0;
        long freed = 0;
        var stillCritical = false;

        foreach (var (root, totalBytes, freeBytes) in drives)
        {
            if (DiskPressurePolicy.Evaluate(totalBytes, freeBytes, criticalFreeBytes)
                != DiskPressureLevel.Critical)
            {
                continue;
            }

            var volumeRoot = NormalizeRoot(root);
            var target = criticalFreeBytes + HourlyWriteEstimate(volumeRoot, nowUtc);
            var free = freeBytes;

            // 以（開始時間, Id）游標逐批掃最舊的 final 段。游標對「看過的每一列」前進，
            // 被保存鎖定或刪不掉的段才不會讓迴圈原地打轉。
            var cursorStart = DateTime.MinValue;
            long cursorId = 0;
            var exhausted = false;
            while (free <= target && !exhausted)
            {
                var batch = _repo.ListOldestFinalAfter(cursorStart, cursorId, 64);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var seg in batch)
                {
                    cursorStart = seg.StartUtc;
                    cursorId = seg.Id;

                    if (free > target)
                    {
                        break;
                    }

                    // 只汰除壓力磁碟上的段：另一顆還有空間的磁碟不該被連坐。
                    if (!IsOnVolume(seg.FilePath, volumeRoot))
                    {
                        continue;
                    }

                    if (seg.SizeBytes <= 0)
                    {
                        continue;
                    }

                    // 保存鎖定（M66）豁免：被保全的錄影是證據，不會為了騰空間被刪。
                    if (_legalHolds?.IsLocked(seg.ChannelId, seg.StartUtc, seg.EndUtc ?? seg.StartUtc) == true)
                    {
                        continue;
                    }

                    if (!TryDeleteSegmentFile(seg.FilePath))
                    {
                        continue;
                    }

                    _repo.Delete(seg.Id);
                    freed += seg.SizeBytes;
                    deleted++;

                    try
                    {
                        free = usage(volumeRoot).FreeBytes;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 讀不到容量就保守地用「已刪除量」推進，下一輪再校正。
                        free += seg.SizeBytes;
                    }
                }

                if (batch.Count < 64)
                {
                    exhausted = true;
                }
            }

            if (free <= target)
            {
                stillCritical = true;
            }
        }

        return new PressureReport(deleted, freed, stillCritical);
    }

    /// <summary>最近一小時實際寫進這顆磁碟的錄影量（沒有就回 <see cref="PressureRefillBytes"/>）。</summary>
    private long HourlyWriteEstimate(string volumeRoot, DateTime nowUtc)
    {
        long estimate = 0;
        foreach (var seg in _repo.ListFinalBetween(nowUtc.AddHours(-1), nowUtc))
        {
            if (IsOnVolume(seg.FilePath, volumeRoot))
            {
                estimate += Math.Max(0, seg.SizeBytes);
            }
        }

        return estimate > 0 ? estimate : PressureRefillBytes;
    }

    private static string NormalizeRoot(string path)
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

    private static bool IsOnVolume(string filePath, string volumeRoot)
        => string.Equals(NormalizeRoot(filePath), volumeRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>清除逾時未收尾之 *.tmp 錄影暫存檔；回傳清除數。</summary>
    public int PurgeStaleTmp(DateTime nowUtc)
    {
        var purged = 0;
        foreach (var file in EnumerateFilesSafe(_recordingsRoot, "*.tmp"))
        {
            try
            {
                if ((nowUtc - File.GetLastWriteTimeUtc(file)).TotalMinutes > StaleTmpMinutes)
                {
                    File.Delete(file);
                    purged++;
                }
            }
            catch (IOException)
            {
                // 使用中暫存，略過
            }
            catch (UnauthorizedAccessException)
            {
                // 無權限，略過
            }
        }

        return purged;
    }

    /// <summary>
    /// 刪除單一區段檔；回傳是否已不存在於磁碟。
    /// 檔案不存在時視為成功；路徑不在錄影根目錄內時拒絕刪除（避免資料庫遭污染時刪到任意檔案）。
    /// </summary>
    private bool TryDeleteSegmentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var full = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_recordingsRoot, path));
        if (!File.Exists(full))
        {
            return true;
        }

        // 只要檔案落在「目前或曾用過」的任何錄影根目錄底下就允許刪除；切換磁碟後
        // 舊根的絕對路徑仍必須能汰除，否則配額清理會卡住（見建構子說明）。
        if (!_recordingsRoots.Any(root => SharePath.IsWithinRoot(root, full)))
        {
            return false;
        }

        try
        {
            File.Delete(full);
        }
        catch (IOException)
        {
            // 檔案被占用，下一輪重試
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return !File.Exists(full);
    }

    /// <summary>依檔案時間清理過期快照（snapshots/ 下所有檔案）；回傳清理報告。</summary>
    public SnapshotReport PurgeSnapshots(string snapshotsRoot, DateTime olderThanUtc)
    {
        var root = Path.GetFullPath(snapshotsRoot);
        long freed = 0;
        var deleted = 0;
        foreach (var file in EnumerateFilesSafe(root, "*.*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < olderThanUtc)
                {
                    freed += new FileInfo(file).Length;
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (IOException)
            {
                // 使用中，略過
            }
            catch (UnauthorizedAccessException)
            {
                // 無權限，略過
            }
        }

        return new SnapshotReport(freed, deleted);
    }

    /// <summary>
    /// 安全列舉檔案：目錄不存在時回傳空集合，逐層跳過無權限目錄與 reparse point（避免沿連結離開根目錄）。
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files;
            string[] subdirs;
            try
            {
                files = Directory.GetFiles(dir, pattern);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var sub in subdirs)
            {
                try
                {
                    if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                pending.Push(sub);
            }
        }
    }
}
