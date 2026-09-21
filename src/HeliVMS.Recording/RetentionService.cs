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
        _recordingsRoot = recordingsRoot;
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
            TryDeleteFile(chosen.FilePath);
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
        if (!Directory.Exists(_recordingsRoot))
        {
            return 0;
        }

        var purged = 0;
        foreach (var file in Directory.EnumerateFiles(_recordingsRoot, "*.tmp", SearchOption.AllDirectories))
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 檔案被占用或不存在，略過（DB 列仍刪除）
        }
        catch (UnauthorizedAccessException)
        {
            // 無權限，略過
        }
    }

    /// <summary>依檔案時間清理過期快照（snapshots/ 下所有檔案）；回傳清理報告。</summary>
    public SnapshotReport PurgeSnapshots(string snapshotsRoot, DateTime olderThanUtc)
    {
        if (!Directory.Exists(snapshotsRoot))
        {
            return new SnapshotReport(0, 0);
        }

        long freed = 0;
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(snapshotsRoot, "*.*", SearchOption.AllDirectories))
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
}