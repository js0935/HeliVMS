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
    private readonly string _recordingsRoot;
    private readonly LegalHoldRepository? _legalHolds;

    public RetentionService(SegmentRepository repo, string recordingsRoot, LegalHoldRepository? legalHolds = null)
    {
        _repo = repo;
        _recordingsRoot = Path.GetFullPath(recordingsRoot);
        _legalHolds = legalHolds;
    }

    /// <summary>執行一次配額清理與 tmp 隔離；回傳結果報告。保存鎖定（M66）所覆蓋時段豁免汰除。</summary>
    public RetentionReport Apply(long quotaBytes, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        long usage = _repo.GetTotalUsage();
        long freed = 0;
        var deleted = 0;

        while (usage > quotaBytes)
        {
            var candidates = _repo.ListOldestFinal(64);
            SegmentRecord? target = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                var seg = candidates[i];
                if (seg.SizeBytes <= 0)
                {
                    continue;
                }

                if (_legalHolds?.IsLocked(seg.ChannelId, seg.StartUtc, seg.EndUtc ?? seg.StartUtc) == true)
                {
                    continue;
                }

                target = seg;
                break;
            }

            if (target is null)
            {
                break;
            }

            var chosen = target!;
            if (!TryDeleteSegmentFile(chosen.FilePath))
            {
                // 檔案仍在（遭占用或路徑不在錄影根目錄內）：保留索引列，待下一輪重試。
                break;
            }

            _repo.Delete(chosen.Id);
            usage -= chosen.SizeBytes;
            freed += chosen.SizeBytes;
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

        if (!SharePath.IsWithinRoot(_recordingsRoot, full))
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
