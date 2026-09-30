using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.WebApi;

/// <summary>一次保留清理結果；Skipped 為因檔案仍存在或未設定錄影根目錄而保留的段數。</summary>
public sealed record RetentionRun(int AgePurged, int WatermarkPurged, long BytesFreed, long AlarmPurged, int Skipped);

/// <summary>
/// 錄影保留策略（M132）：保留天數＋浮水印（GB）雙重配額清理。
/// 清除時一併刪除實體檔案，且不得動到保存鎖定（M66）涵蓋的時段。
/// </summary>
public sealed class RetentionService : BackgroundService
{
    public const string DaysKey = "recording.retention.days";
    public const string WatermarkGbKey = "recording.retention.watermark_gb";
    public const string AlarmDaysKey = "alarm.retention.days";

    /// <summary>錄影根目錄；未設定時不刪檔，僅清除檔案已不存在之段，避免產生孤兒檔。</summary>
    public const string RecordingsRootKey = "HELIVMS_RECORDINGS_ROOT";

    private const int MaxSweep = 200_000;
    private const int BatchSize = 16;

    private readonly SegmentRepository _segments;
    private readonly AlarmEventRepository _alarms;
    private readonly SettingsRepository _settings;
    private readonly LegalHoldRepository? _legalHolds;
    private readonly string? _recordingsRoot;
    private readonly TimeSpan _interval;

    public RetentionService(
        SqliteStore store,
        LegalHoldRepository? legalHolds = null,
        string? recordingsRoot = null,
        TimeSpan? interval = null)
    {
        _segments = new SegmentRepository(store);
        _alarms = new AlarmEventRepository(store);
        _settings = new SettingsRepository(store);
        _legalHolds = legalHolds;
        _recordingsRoot = string.IsNullOrWhiteSpace(recordingsRoot) ? null : Path.GetFullPath(recordingsRoot);
        _interval = interval ?? TimeSpan.FromMinutes(30);
    }

    public int RetentionDays => (int)_settings.GetDoubleOrDefault(DaysKey, 30);

    public double WatermarkGb => _settings.GetDoubleOrDefault(WatermarkGbKey, 0);

    public int AlarmRetentionDays => (int)_settings.GetDoubleOrDefault(AlarmDaysKey, 365);

    public long UsageBytes => _segments.GetTotalUsage();

    public RetentionRun RunOnce(DateTime nowUtc)
    {
        var days = RetentionDays;
        var watermark = WatermarkGb;
        var alarmDays = AlarmRetentionDays;
        int agePurged = 0;
        int wmPurged = 0;
        int skipped = 0;
        long freed = 0;
        var alarmPurged = alarmDays > 0
            ? _alarms.DeleteOlderThan(nowUtc.AddDays(-alarmDays))
            : 0;
        var cutoff = nowUtc.AddDays(-days);
        var attempted = new HashSet<long>();

        while (agePurged + wmPurged + skipped < MaxSweep)
        {
            var progressed = false;

            if (days > 0)
            {
                var retired = _segments.ListRetired(cutoff, BatchSize);
                var target = PickUnlocked(retired, attempted);
                if (target is not null)
                {
                    attempted.Add(target.Id);
                    if (Purge(target))
                    {
                        freed += target.SizeBytes;
                        agePurged++;
                    }
                    else
                    {
                        skipped++;
                    }

                    progressed = true;
                }
            }

            if (!progressed && watermark > 0)
            {
                var watermarkBytes = (long)(watermark * 1073741824);
                if (_segments.GetTotalUsage() - watermarkBytes > 0)
                {
                    var oldest = PickUnlocked(_segments.ListOldestFinal(BatchSize), attempted);
                    if (oldest is not null)
                    {
                        attempted.Add(oldest.Id);
                        if (Purge(oldest))
                        {
                            freed += oldest.SizeBytes;
                            wmPurged++;
                        }
                        else
                        {
                            skipped++;
                        }

                        progressed = true;
                    }
                }
            }

            if (!progressed)
            {
                break;
            }
        }

        return new RetentionRun(agePurged, wmPurged, freed, alarmPurged, skipped);
    }

    /// <summary>取第一段未受保存鎖定且本輪尚未嘗試者；皆略過時為 null（該輪結束）。</summary>
    private SegmentRecord? PickUnlocked(IReadOnlyList<SegmentRecord> candidates, HashSet<long> attempted)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (attempted.Contains(candidates[i].Id) || IsLocked(candidates[i]))
            {
                continue;
            }

            return candidates[i];
        }

        return null;
    }

    private bool IsLocked(SegmentRecord segment)
        => _legalHolds?.IsLocked(segment.ChannelId, segment.StartUtc, segment.EndUtc ?? segment.StartUtc) == true;

    /// <summary>
    /// 清除單一段：檔案不存在則直接刪列；檔案存在時必須能確認路徑位於錄影根目錄下且刪除成功，
    /// 否則保留索引列（檔案遭占用時重試），避免資料遺失與孤兒檔。
    /// </summary>
    private bool Purge(SegmentRecord segment)
    {
        var path = segment.FilePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
            {
                if (_recordingsRoot is null || !SharePath.IsWithinRoot(_recordingsRoot, full))
                {
                    return false;
                }

                if (!TryDeleteFile(full))
                {
                    return false;
                }
            }
        }

        _segments.Delete(segment.Id);
        return true;
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunOnce(DateTime.UtcNow);
            }
            catch
            {
                // 單輪失敗不影響後續排程
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
