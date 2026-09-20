using System.Globalization;

namespace HeliVMS.Storage;

/// <summary>異地備援自動複製（M55）：定時將來源備份目錄之新檔複製至目標（NAS／雲端掛載）路徑。</summary>
public sealed class OffsiteReplicationService
{
    private readonly OffsiteReplicationRepository _repository;

    public OffsiteReplicationService(OffsiteReplicationRepository repository)
    {
        _repository = repository;
    }

    /// <summary>立即執行一筆複製工作（依 id）。</summary>
    public bool RunOnce(long jobId)
    {
        var job = _repository.List().FirstOrDefault(j => j.Id == jobId);
        return job is not null && RunOnce(job);
    }

    /// <summary>執行一筆複製工作：遞迴複製來源下新於上次執行時間之檔案，保留相對結構。</summary>
    public bool RunOnce(OffsiteJob job)
    {
        if (!Directory.Exists(job.SourcePath))
        {
            return Fail(job, "來源目錄不存在");
        }

        if (!Directory.Exists(job.DestinationPath))
        {
            return Fail(job, "目標目錄不存在");
        }

        var lastRunUtc = job.LastRunUtc ?? DateTime.MinValue;
        var copied = 0;
        var failed = 0;
        foreach (var file in Directory.EnumerateFiles(job.SourcePath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var relative = Path.GetRelativePath(job.SourcePath, file);
                var destFile = Path.Combine(job.DestinationPath, relative);
                var needsCopy = !File.Exists(destFile)
                                || File.GetLastWriteTimeUtc(file) >= lastRunUtc;
                if (!needsCopy)
                {
                    continue;
                }

                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                File.Copy(file, destFile, overwrite: true);
                copied++;
            }
            catch
            {
                failed++;
            }
        }

        if (failed > 0)
        {
            return Fail(job, $"複製失敗 {failed} 檔");
        }

        _repository.SetRunResult(job.Id, true, $"copied={copied}");
        return true;
    }

    /// <summary>該筆工作是否已畢於執行（啟用、且間隔已到或從未執行）。</summary>
    public static bool IsDue(OffsiteJob job, DateTime utcNow)
    {
        if (!job.Enabled)
        {
            return false;
        }

        if (job.LastRunUtc is null)
        {
            return true;
        }

        return (utcNow - job.LastRunUtc.Value).TotalMinutes >= job.IntervalMinutes;
    }

    /// <summary>定時掃描：回傳目前到期應執行的啟用工作。</summary>
    public IReadOnlyList<OffsiteJob> DueJobs(DateTime utcNow)
    {
        return _repository.ListEnabled().Where(j => IsDue(j, utcNow)).ToList();
    }

    private bool Fail(OffsiteJob job, string message)
    {
        _repository.SetRunResult(job.Id, false, message);
        return false;
    }

    /// <summary>取得以 InvariantCulture 解析之執行時間欄位（供測試用）。</summary>
    public static DateTime? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}