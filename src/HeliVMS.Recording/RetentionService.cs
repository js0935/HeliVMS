using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>配額清理執行報告。</summary>
public sealed record RetentionReport(long UsedBytes, long FreedBytes, int DeletedSegments, int PurgedTmp);

/// <summary>
/// 錄影配額策略（§9 儲存管理）：總量超過配額時，依開始時間由最舊開始刪除 final
/// 區段（檔＋資料庫列），並清理擱置之 *.tmp 殘檔（異常錄影中斷遺留）。
/// </summary>
public sealed class RetentionService
{
    private const double StaleTmpMinutes = 10;

    private readonly SegmentRepository _repo;
    private readonly string _recordingsRoot;

    public RetentionService(SegmentRepository repo, string recordingsRoot)
    {
        _repo = repo;
        _recordingsRoot = recordingsRoot;
    }

    /// <summary>執行一次配額清理與 tmp 隔離；回傳結果報告。</summary>
    public RetentionReport Apply(long quotaBytes, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        long usage = _repo.GetTotalUsage();
        long freed = 0;
        var deleted = 0;

        while (usage > quotaBytes)
        {
            var oldest = _repo.ListOldestFinal(1).FirstOrDefault();
            if (oldest is null || oldest.SizeBytes <= 0)
            {
                break;
            }

            TryDeleteFile(oldest.FilePath);
            _repo.Delete(oldest.Id);
            usage -= oldest.SizeBytes;
            freed += oldest.SizeBytes;
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
}