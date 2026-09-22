using HeliVMS.Storage;

namespace HeliVMS.WebApi;

/// <summary>錄影保留策略（M132）：保留天數＋浮水印（GB）雙重配額清理。</summary>
public sealed class RetentionService : BackgroundService
{
    public const string DaysKey = "recording.retention.days";
    public const string WatermarkGbKey = "recording.retention.watermark_gb";
    public const string AlarmDaysKey = "alarm.retention.days";

    private readonly SegmentRepository _segments;
    private readonly AlarmEventRepository _alarms;
    private readonly SettingsRepository _settings;
    private readonly TimeSpan _interval;

    public RetentionService(SqliteStore store, TimeSpan? interval = null)
    {
        _segments = new SegmentRepository(store);
        _alarms = new AlarmEventRepository(store);
        _settings = new SettingsRepository(store);
        _interval = interval ?? TimeSpan.FromMinutes(30);
    }

    public int RetentionDays => (int)_settings.GetDoubleOrDefault(DaysKey, 30);

    public double WatermarkGb => _settings.GetDoubleOrDefault(WatermarkGbKey, 0);

    public int AlarmRetentionDays => (int)_settings.GetDoubleOrDefault(AlarmDaysKey, 365);

    public long UsageBytes => _segments.GetTotalUsage();

    public (int AgePurged, int WatermarkPurged, long BytesFreed, long AlarmPurged) RunOnce(DateTime nowUtc)
    {
        var days = RetentionDays;
        var watermark = WatermarkGb;
        var alarmDays = AlarmRetentionDays;
        int agePurged = 0;
        int wmPurged = 0;
        long freed = 0;
        long alarmPurged = alarmDays > 0
            ? _alarms.DeleteOlderThan(nowUtc.AddDays(-alarmDays))
            : 0;
        var stopped = false;
        var cutoff = nowUtc.AddDays(-days);

        while (!stopped && agePurged + wmPurged < 200_000)
        {
            if (days > 0)
            {
                var retired = _segments.ListRetired(cutoff, 1);
                if (retired.Count == 1)
                {
                    _segments.Delete(retired[0].Id);
                    freed += retired[0].SizeBytes;
                    agePurged++;
                    continue;
                }
            }

            if (watermark > 0)
            {
                var watermarkBytes = (long)(watermark * 1073741824);
                var over = _segments.GetTotalUsage() - watermarkBytes;
                if (over > 0)
                {
                    var oldest = _segments.ListOldestFinal(1);
                    if (oldest.Count == 1)
                    {
                        _segments.Delete(oldest[0].Id);
                        freed += oldest[0].SizeBytes;
                        wmPurged++;
                        continue;
                    }
                }
            }

            stopped = true;
        }

        return (agePurged, wmPurged, freed, alarmPurged);
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