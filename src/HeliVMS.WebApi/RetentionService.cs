using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.WebApi;

/// <summary>
/// 一次保留清理結果；Skipped 為因檔案仍存在或未設定錄影根目錄而保留的段數，
/// AuditPurged／AudioPurged 為稽核日誌與事件錄音各自清掉的筆數。
/// </summary>
public sealed record RetentionRun(
    int AgePurged,
    int WatermarkPurged,
    long BytesFreed,
    long AlarmPurged,
    int Skipped,
    long AuditPurged = 0,
    long AudioPurged = 0);

/// <summary>
/// 錄影保留策略（M132）：保留天數＋浮水印（GB）雙重配額清理。
/// 清除時一併刪除實體檔案，且不得動到保存鎖定（M66）涵蓋的時段。
/// </summary>
public sealed class RetentionService : BackgroundService
{
    public const string DaysKey = "recording.retention.days";
    public const string WatermarkGbKey = "recording.retention.watermark_gb";

    /// <summary>警報事件（連同事件錄音）保管天數；與桌面保留迴圈共用同一把鍵（見 <see cref="MetadataRetentionPruner"/>）。</summary>
    public const string AlarmDaysKey = MetadataRetentionPruner.AlarmDaysKey;

    /// <summary>
    /// 稽核日誌保管天數。稽核是資安治理證據（§14.1），預設留一年；設 0 表示永久保留。
    /// 與桌面保留迴圈共用同一把鍵（見 <see cref="MetadataRetentionPruner"/>）。
    /// </summary>
    public const string AuditDaysKey = MetadataRetentionPruner.AuditDaysKey;

    /// <summary>錄影根目錄；未設定時不刪檔，僅清除檔案已不存在之段，避免產生孤兒檔。</summary>
    public const string RecordingsRootKey = RecordingStorage.RootVariable;

    private const int MaxSweep = 200_000;
    private const int BatchSize = 16;

    private readonly SegmentRepository _segments;
    private readonly SqliteStore _store;
    private readonly SettingsRepository _settings;
    private readonly LegalHoldRepository? _legalHolds;
    private readonly string? _recordingsRoot;
    private readonly TimeSpan _interval;
    private readonly ILogger<RetentionService>? _logger;

    public RetentionService(
        SqliteStore store,
        LegalHoldRepository? legalHolds = null,
        string? recordingsRoot = null,
        TimeSpan? interval = null,
        ILogger<RetentionService>? logger = null)
    {
        _store = store;
        _segments = new SegmentRepository(store);
        _settings = new SettingsRepository(store);
        _legalHolds = legalHolds;
        _recordingsRoot = string.IsNullOrWhiteSpace(recordingsRoot) ? null : Path.GetFullPath(recordingsRoot);
        _interval = interval ?? TimeSpan.FromMinutes(30);
        _logger = logger;
    }

    public int RetentionDays => (int)_settings.GetDoubleOrDefault(DaysKey, 30);

    public double WatermarkGb => _settings.GetDoubleOrDefault(WatermarkGbKey, 0);

    public int AlarmRetentionDays => (int)_settings.GetDoubleOrDefault(AlarmDaysKey, 365);

    /// <summary>稽核日誌保管天數；0 表示永久保留（預設一年）。</summary>
    public int AuditRetentionDays => (int)_settings.GetDoubleOrDefault(AuditDaysKey, 365);

    public long UsageBytes => _segments.GetTotalUsage();

    /// <summary>
    /// 顯數前先把「索引說有、磁碟上沒有」的段標成 missing，再回傳用量。
    ///
    /// <para>
    /// <see cref="UsageBytes"/> 只讀 <c>segments</c> 表的 final 段；外部刪檔之後那個數字會一直
    /// 偏高，直到每小時的背景對帳跑完（<see cref="SegmentReconcileService"/>）。用量是操作員
    /// 拿來判斷「離配額還有多遠」的數字，慢一小時才對會直接導致誤判。這裡跑的是資料夾層級
    /// （<see cref="SegmentIndexReconciler.ReconcileFolders"/>）：整段歷史幾百到幾千次
    /// <c>Directory.Exists</c>，不逐檔 stat，所以掛在一個查詢端點上成本可接受。
    /// </para>
    /// <para>
    /// 對帳失敗（磁碟忙碌、庫被鎖）不該讓用量查詢整個 500：退回未對帳的數字，
    /// 下一輪背景對帳仍會修正索引。
    /// </para>
    /// </summary>
    public long ReconcileUsage()
    {
        try
        {
            new SegmentIndexReconciler(_segments).ReconcileFolders();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "顯數前的資料夾層級對帳失敗，回報未對帳的用量。");
        }

        return UsageBytes;
    }

    public RetentionRun RunOnce(DateTime nowUtc)
    {
        _allowedRoots = BuildAllowedRoots();

        // 汰除前先做一次資料夾層級對帳：下面浮水印分支會依 GetTotalUsage 的數字刪「真實」檔案，
        // 而那個數字只讀 segments 表的 final 段，外部刪檔後會虛高。不對帳就直接刪，等於為了把
        // 索引數字壓到浮水印以下，去刪磁碟上根本還在的錄影。對帳失敗不影響清理主線。
        try
        {
            new SegmentIndexReconciler(_segments).ReconcileFolders();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "清理前的資料夾層級對帳失敗，以現有索引用量繼續清理。");
        }

        var days = RetentionDays;
        var watermark = WatermarkGb;
        var alarmDays = AlarmRetentionDays;
        var auditDays = AuditRetentionDays;
        int agePurged = 0;
        int wmPurged = 0;
        int skipped = 0;
        long freed = 0;

        // 中繼資料保管期限清理（警報事件／事件錄音／稽核日誌）與桌面端共用同一個入口；
        // 稽核日誌過去只寫不刪、事件錄音又因沒有外鍵而成為孤兒，長期只增不減。
        var meta = MetadataRetentionPruner.Prune(_store, nowUtc, alarmDays, auditDays);
        var alarmPurged = meta.AlarmPurged;
        var audioPurged = meta.AudioPurged;
        var auditPurged = meta.AuditPurged;

        var cutoff = nowUtc.AddDays(-days);

        // 用量只查一次，之後在記憶體裡扣掉已刪除的位元組。舊寫法在浮水印分支裡每刪一筆就
        // 重算一次 SUM(size_bytes)：百萬列下這是一次又一次的整表掃描，清理永遠追不上寫入。
        long usage = _segments.GetTotalUsage();
        var watermarkBytes = (long)(watermark * 1073741824);

        // 保留期清理：以 (start_time, id) 為游標單調往新方向掃，刪光早於截止點的 final 區段。
        // 用游標而非「每輪重取最舊一批」，才能在受保存鎖定的舊區段夾在最舊端時繼續往後找，
        // 而不是卡在鎖住的前綴、讓之後所有過期區段都刪不掉（見 ListRetiredAfter）。
        if (days > 0)
        {
            var afterStart = DateTime.MinValue;
            long afterId = 0;
            while (agePurged + wmPurged + skipped < MaxSweep)
            {
                var retired = _segments.ListRetiredAfter(cutoff, afterStart, afterId, BatchSize);
                if (retired.Count == 0)
                {
                    break;
                }

                afterStart = retired[^1].StartUtc;
                afterId = retired[^1].Id;

                foreach (var segment in retired)
                {
                    if (IsLocked(segment))
                    {
                        skipped++;
                        continue;
                    }

                    if (Purge(segment))
                    {
                        freed += segment.SizeBytes;
                        usage -= segment.SizeBytes;
                        agePurged++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
        }

        // 浮水印（容量）清理：年齡清理不足以壓到浮水印以下時，續刪最舊的 final 區段，同樣以游標前進。
        if (watermark > 0)
        {
            var afterStart = DateTime.MinValue;
            long afterId = 0;
            while (agePurged + wmPurged + skipped < MaxSweep && usage - watermarkBytes > 0)
            {
                var oldest = _segments.ListOldestFinalAfter(afterStart, afterId, BatchSize);
                if (oldest.Count == 0)
                {
                    break;
                }

                afterStart = oldest[^1].StartUtc;
                afterId = oldest[^1].Id;

                foreach (var segment in oldest)
                {
                    if (usage - watermarkBytes <= 0)
                    {
                        break;
                    }

                    if (IsLocked(segment))
                    {
                        skipped++;
                        continue;
                    }

                    if (Purge(segment))
                    {
                        freed += segment.SizeBytes;
                        usage -= segment.SizeBytes;
                        wmPurged++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
        }

        return new RetentionRun(agePurged, wmPurged, freed, alarmPurged, skipped, auditPurged, audioPurged);
    }

    private bool IsLocked(SegmentRecord segment)
        => _legalHolds?.IsLocked(segment.ChannelId, segment.StartUtc, segment.EndUtc ?? segment.StartUtc) == true;

    /// <summary>目前允許刪除檔案的錄影根目錄集合（本次 RunOnce 期間固定）。</summary>
    private IReadOnlyList<string> _allowedRoots = [];

    /// <summary>
    /// 允許刪除的錄影根目錄：目前根目錄＋設定中心切換磁碟後保留的舊根。
    /// 區段檔以絕對路徑存在資料庫，少了舊根會讓切換磁碟後的舊錄影無法汰除。
    /// </summary>
    private IReadOnlyList<string> BuildAllowedRoots()
    {
        var roots = new List<string>();
        if (_recordingsRoot is not null)
        {
            roots.Add(_recordingsRoot);
        }

        foreach (var root in RecordingStorage.ParseKnownRoots(_settings.Get(RecordingStorage.KnownRootsSettingKey)))
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

        return roots;
    }

    /// <summary>
    /// 清除單一段：檔案不存在則直接刪列；檔案存在時必須能確認路徑位於已知錄影根目錄下且刪除成功，
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
                if (_allowedRoots.Count == 0 || !_allowedRoots.Any(root => SharePath.IsWithinRoot(root, full)))
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
            RunOnceGuarded(DateTime.UtcNow);
            await Task.Delay(_interval, stoppingToken);
        }
    }

    /// <summary>
    /// 執行一輪保留清理；例外只記錄、不往外拋。回傳是否可繼續下一輪。
    ///
    /// 迴圈若讓例外逃逸就會整個結束：保留清理悄悄停止，磁碟被灌滿卻沒有人知道，
    /// 稽核與健康面板都不會說話。單輪失敗（資料庫暫時鎖住、目錄權限異動）留給下一輪重試，
    /// 但例外必須進 log——否則「一直失敗」與「一直沒事」在維運眼裡長得一模一樣。
    /// 空 catch 的版本正是如此，現場只能看到磁碟慢慢滿起來。
    /// </summary>
    internal bool RunOnceGuarded(DateTime nowUtc)
    {
        try
        {
            RunOnce(nowUtc);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "錄影保留清理迴圈發生例外，下一輪會再試。");
            return true;
        }
    }
}
