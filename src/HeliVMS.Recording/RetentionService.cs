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

    /// <summary>執行一次配額清理與 tmp 隔離；回傳結果報告。保存鎖定（M66）所覆蓋時段豁免汰除。</summary>
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

        var purgedTmp = PurgeStaleTmp(now);
        return new RetentionReport(usage, freed, deleted, purgedTmp);
    }

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
