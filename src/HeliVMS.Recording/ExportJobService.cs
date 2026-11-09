using System.Globalization;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>匯出中心佇列處理結果。</summary>
public sealed record ExportJobBatchResult(int Processed, int Succeeded, int Failed);

/// <summary>
/// 匯出工作佇列執行器（§14.3(2) 匯出中心）：依序處理 queued 工作，
/// 每筆呼叫既有 <see cref="ExportService"/> 產 MP4；單一失敗標記 failed 不卡佇列。
/// 允許注入執行器（測試用，CI 無 ffmpeg 亦驗狀態流／命名）。
/// </summary>
public sealed class ExportJobService
{
    private readonly SqliteStore _store;
    private readonly Func<ExportRequest, CancellationToken, Task<ExportResult>>? _executor;

    public ExportJobService(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>注入自訂執行器（取代 <see cref="ExportService"/> 的 ffmpeg 片段）。</summary>
    public ExportJobService(SqliteStore store, Func<ExportRequest, CancellationToken, Task<ExportResult>> executor)
    {
        _store = store;
        _executor = executor;
    }

    /// <summary>循序執行所有 queued 工作，輸出至 outputRoot（檔名含 job id 供追溯）。</summary>
    public async Task<ExportJobBatchResult> ProcessQueuedAsync(
        string outputRoot,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputRoot);
        var jobs = new ExportJobRepository(_store);
        var queued = jobs.ListQueued();

        var succeeded = 0;
        var failed = 0;
        foreach (var job in queued)
        {
            ct.ThrowIfCancellationRequested();

            // ListQueued 與 MarkRunning 之間工作可能已被取消（操作員在另一邊按了取消）：
            // 只執行仍是 queued 的；跳過的不算失敗也不算成功。
            if (jobs.Get(job.Id)?.Status != "queued")
            {
                continue;
            }

            var now = DateTime.UtcNow;
            jobs.MarkRunning(job.Id, now);

            var from = job.StartUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var to = job.EndUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var outputPath = Path.Combine(
                outputRoot,
                $"export-job{job.Id}-ch{job.ChannelId}-{from}-{to}.mp4");

            try
            {
                ExportResult result;
                var exporter = new ExportService(_store);
                // §14.7 #4/#5：佇列匯出無逐筆 UI 可選，一律自動套用此頻道/範圍已登錄的遮蔽區域；
                // 無登錄時 LoadStoredRois 回空集合，等同維持原本不遮蔽行為。
                var redactionRois = exporter.LoadStoredRois(job.ChannelId, job.StartUtc, job.EndUtc);
                if (_executor is not null)
                {
                    result = await _executor(
                        new ExportRequest(job.ChannelId, job.StartUtc, job.EndUtc, outputPath, null, true, redactionRois),
                        ct);
                }
                else
                {
                    result = await exporter.ExportAsync(
                        new ExportRequest(job.ChannelId, job.StartUtc, job.EndUtc, outputPath, null, true, redactionRois),
                        progress,
                        ct);
                }

                jobs.SetResult(job.Id, result.OutputPath, result.Sha256Hash ?? string.Empty, result.FileSizeBytes, DateTime.UtcNow);
                succeeded++;
            }
            catch (Exception ex)
            {
                jobs.SetError(job.Id, ex.Message, DateTime.UtcNow);
                failed++;
            }
        }

        // Processed＝實際跑過的筆數（含失敗）；被取消而跳過的不在內，
        // 否則「取消 3 筆再執行」會報成 3 筆失敗，操作員會以為匯出壞了。
        return new ExportJobBatchResult(succeeded + failed, succeeded, failed);
    }
}