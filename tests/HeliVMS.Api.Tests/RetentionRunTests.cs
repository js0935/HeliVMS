using HeliVMS.Storage;
using HeliVMS.WebApi;
using Microsoft.Extensions.Logging;

namespace HeliVMS.Api.Tests;

/// <summary>
/// 錄影保留策略（M132）回歸：清除必須一併刪除實體檔案、不得動到保存鎖定（M66），
/// 且不得因未設定根目錄而留下孤兒檔。
/// </summary>
public sealed class RetentionRunTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _root;
    private readonly string _outside;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly SettingsRepository _settings;
    private readonly LegalHoldRepository _holds;

    public RetentionRunTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-retention-{Guid.NewGuid():N}.db");
        _root = Path.Combine(Path.GetTempPath(), $"helivms-retention-rec-{Guid.NewGuid():N}");
        _outside = Path.Combine(Path.GetTempPath(), $"helivms-retention-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        _settings = new SettingsRepository(_store);
        _holds = new LegalHoldRepository(_store);

        _store.Execute(
            """
            INSERT INTO channels (device_id, name, main_rtsp, sub_rtsp, codec, audio_enabled, audio_encoder)
            VALUES (NULL, 'ch1', 'rtsp://127.0.0.1:8554/ch1', NULL, 'h264', 1, 'copy');
            """);
    }

    [Fact]
    public void RunOnce_AgeExpired_DeletesFileAndRow()
    {
        _settings.Set(RetentionService.DaysKey, "1");
        var (file, _, _) = SeedSegment(daysOld: 10, sizeBytes: 1_000);

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(1, run.AgePurged);
        Assert.Equal(0, run.Skipped);
        Assert.Equal(1_000L, run.BytesFreed);
        Assert.False(File.Exists(file));
        Assert.Empty(_segments.ListFinal(1));
    }

    [Fact]
    public void RunOnce_AgeExpiredButFileLockedByHold_KeepsFileAndRow()
    {
        _settings.Set(RetentionService.DaysKey, "1");
        var (file, start, end) = SeedSegment(daysOld: 10, sizeBytes: 1_000);
        _holds.Add(1, start.AddMinutes(-1), end.AddMinutes(1), "investigation", "tester", DateTime.UtcNow);

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(0, run.AgePurged);
        Assert.True(File.Exists(file));
        Assert.Single(_segments.ListFinal(1));
    }

    [Fact]
    public void RunOnce_FileOutsideRecordingsRoot_IsNeverDeleted()
    {
        _settings.Set(RetentionService.DaysKey, "1");
        var file = Path.Combine(_outside, "rogue.mp4");
        File.WriteAllBytes(file, new byte[1_000]);
        var start = DateTime.UtcNow.AddDays(-10);
        var id = _segments.BeginSegment(1, "main", file, start);
        _segments.CompleteSegment(id, start.AddSeconds(60), 1_000, 60, new string('a', 64));

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(0, run.AgePurged);
        Assert.True(run.Skipped >= 1);
        Assert.True(File.Exists(file));
        Assert.Single(_segments.ListFinal(1));
    }

    [Fact]
    public void RunOnce_WithoutRecordingsRoot_PurgesOnlyRowsWhoseFileIsGone()
    {
        _settings.Set(RetentionService.DaysKey, "1");
        var (present, _, _) = SeedSegment(daysOld: 10, sizeBytes: 1_000, channelDir: "ch001");
        var (gone, _, _) = SeedSegment(daysOld: 10, sizeBytes: 1_000, channelDir: "ch002", offsetSeconds: 2);
        File.Delete(gone);

        var run = new RetentionService(_store, _holds, recordingsRoot: null).RunOnce(DateTime.UtcNow);

        Assert.Equal(1, run.AgePurged);
        Assert.True(run.Skipped >= 1);
        Assert.True(File.Exists(present));
        var remaining = Assert.Single(_segments.ListFinal(1));
        Assert.Equal(present, remaining.FilePath);
    }

    [Fact]
    public void RunOnce_WatermarkExceeded_SkipsHeldOldestAndPurgesNext()
    {
        _settings.Set(RetentionService.DaysKey, "0");
        _settings.Set(RetentionService.WatermarkGbKey, "0.0000001");
        var (held, start, end) = SeedSegment(daysOld: 10, sizeBytes: 1_000, channelDir: "ch001");
        var (other, _, _) = SeedSegment(daysOld: 5, sizeBytes: 1_000, channelDir: "ch002");
        _holds.Add(1, start.AddMinutes(-1), end.AddMinutes(1), "investigation", "tester", DateTime.UtcNow);

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(1, run.WatermarkPurged);
        Assert.True(File.Exists(held));
        Assert.False(File.Exists(other));
        Assert.Single(_segments.ListFinal(1));
    }

    /// <summary>
    /// 最舊的一整批（剛好填滿一頁）全被保存鎖定時，年齡清理必須跳過它們、繼續往新的方向找，
    /// 而不是就此停手。
    ///
    /// 現場症狀：調查保存鎖住較舊的一段後，年齡清理每一輪都先取到那批鎖定的最舊區段、
    /// 發現一個都不能刪就整輪放棄——於是鎖定期間磁碟只增不減，而 log 全乾淨。
    /// </summary>
    [Fact]
    public void RunOnce_最舊的一整批受保存鎖定時仍會往後清理()
    {
        _settings.Set(RetentionService.DaysKey, "1");

        DateTime firstStart = default;
        DateTime lastEnd = default;
        string firstHeld = string.Empty;
        for (var i = 0; i < 16; i++)
        {
            var (file, start, end) = SeedSegment(
                daysOld: 10, sizeBytes: 1_000, channelDir: "ch001", offsetSeconds: i);
            if (i == 0)
            {
                firstStart = start;
                firstHeld = file;
            }

            lastEnd = end;
        }

        // 保存窗剛好蓋住這 16 筆最舊的區段。
        _holds.Add(1, firstStart.AddMinutes(-1), lastEnd.AddMinutes(1), "investigation", "tester", DateTime.UtcNow);

        // 較新的未鎖定區段（兩天前），必須被清掉。
        var (free, _, _) = SeedSegment(daysOld: 2, sizeBytes: 1_000, channelDir: "ch002");

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(1, run.AgePurged);
        Assert.True(run.Skipped >= 16);
        Assert.False(File.Exists(free));
        Assert.True(File.Exists(firstHeld), "受保存的舊區段被刪了");
    }

    /// <summary>
    /// 稽核日誌過去只寫不刪（PruneOlderThan 沒有正式呼叫端），長期只增不減。
    /// 保留清理必須依保管天數汰除過期項，同時保留期限內的項目。
    /// </summary>
    [Fact]
    public void 稽核日誌依保管天數清除過期項並保留近期項()
    {
        _settings.Set(RetentionService.AuditDaysKey, "30");
        var audit = new AuditLogRepository(_store);
        audit.Record("system", "config.change", AuditCategories.Config, occurredAtUtc: DateTime.UtcNow.AddDays(-60));
        audit.Record("system", "config.change", AuditCategories.Config, occurredAtUtc: DateTime.UtcNow.AddDays(-1));

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(1, run.AuditPurged);
        var kept = Assert.Single(audit.List(new AuditLogQuery { Action = "config.change" }));
        Assert.True(kept.OccurredAtUtc > DateTime.UtcNow.AddDays(-2));
    }

    /// <summary>
    /// 稽核是資安治理證據，設 0 表示永久保留：任何過期項目都不該被清掉。
    /// </summary>
    [Fact]
    public void 稽核保管天數設零時永久保留()
    {
        _settings.Set(RetentionService.AuditDaysKey, "0");
        var audit = new AuditLogRepository(_store);
        audit.Record("system", "config.change", AuditCategories.Config, occurredAtUtc: DateTime.UtcNow.AddDays(-3650));

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(0, run.AuditPurged);
        var kept = Assert.Single(audit.List(new AuditLogQuery { Action = "config.change" }));
        Assert.True(kept.OccurredAtUtc < DateTime.UtcNow.AddDays(-3000));
    }

    /// <summary>
    /// 事件錄音掛在警報事件上，但兩表之間沒有外鍵，刪事件不會連帶刪錄音。
    /// 保留清理必須用同一條警報保管期限清掉過期錄音（含事件已汰除的孤兒），否則 BLOB 只增不減。
    /// </summary>
    [Fact]
    public void 事件錄音依警報保管天數清除過期項並保留近期項()
    {
        _settings.Set(RetentionService.AlarmDaysKey, "1");
        var audio = new EventAudioRepository(_store);
        audio.Save(1, 1, DateTime.UtcNow.AddDays(-10), 1000, "audio/opus", [1, 2, 3]);
        audio.Save(2, 1, DateTime.UtcNow.AddHours(-1), 1000, "audio/opus", [4, 5, 6]);

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(1, run.AudioPurged);
        Assert.Null(audio.GetByEvent(1));
        Assert.NotNull(audio.GetByEvent(2));
    }

    /// <summary>
    /// 清理迴圈的一輪失敗必須被記錄，而且迴圈要活下來。
    ///
    /// 舊版是一個空的 catch：資料庫暫時鎖住、目錄權限被改動等原因讓 <c>RunOnce</c> 擲例外時，
    /// 迴圈會安靜地把每一輪都吞掉，保留清理再也不真正執行，磁碟被灌滿而維運毫無線索——
    /// 「一直失敗」與「一直沒事」在 log 上沒有任何差別。
    /// 這裡把資料庫關掉讓 <c>RunOnce</c> 擲例外，確定例外會進 log 且回報可繼續。
    /// </summary>
    [Fact]
    public void 單輪清理擲例外時會記錄且回報繼續()
    {
        var logger = new CapturingLogger<RetentionService>();
        var service = new RetentionService(_store, _holds, _root, logger: logger);

        // 關閉資料庫，讓下一輪 RunOnce 在讀設定／用量時擲例外。
        _store.Dispose();

        Assert.True(service.RunOnceGuarded(DateTime.UtcNow));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    /// <summary>
    /// 顯數前對帳：/config/usage 的用量必須反映磁碟實況，不能把已刪除的錄影一直算進去。
    ///
    /// 現場症狀：管理員在磁碟上把某天的錄影資料夾刪掉，用量頁仍顯示那些位元組，
    /// 直到最多一小時後的背景對帳才修正，期間操作員會誤判「離配額還很遠」。
    /// 這裡把整天的資料夾刪掉後直接呼叫 ReconcileUsage，用量必須立刻下降。
    /// </summary>
    [Fact]
    public void ReconcileUsage_整天資料夾被刪除後用量立刻反映()
    {
        var (file, _, _) = SeedSegment(daysOld: 1, sizeBytes: 5_000, channelDir: "ch001");
        var dir = Path.GetDirectoryName(file)!;
        Assert.Equal(5_000L, _segments.GetTotalUsage());

        Directory.Delete(dir, recursive: true);

        var usage = new RetentionService(_store, _holds, _root).ReconcileUsage();

        Assert.Equal(0L, usage);
        Assert.Empty(_segments.ListFinal(1));
    }

    /// <summary>
    /// 浮水印清理必須先對帳再判斷超額。
    ///
    /// 現場症狀：管理員在磁碟上刪掉某天的資料夾，索引的最終用量仍把那些位元組算進去。
    /// 浮水印分支依這個虛高數字刪最舊的 final 段，於是為了把「索引數字」壓到浮水印以下，
    /// 刪掉了另一天磁碟上還存在的真實錄影——磁碟從來沒滿，卻刪掉了調查證據。
    /// 對帳後幽靈段先被排除，真實段低於浮水印，不該被刪。
    /// </summary>
    [Fact]
    public void RunOnce_浮水印不為幽靈用量刪掉真實錄影()
    {
        _settings.Set(RetentionService.DaysKey, "0");

        // 兩個 4,000 bytes 的段；浮水印約 5,000 bytes：
        // 不對帳時用量 8,000 > 5,000 會刪最舊的真實段；對帳後用量 4,000 ≤ 5,000，不該刪。
        _settings.Set(
            RetentionService.WatermarkGbKey,
            (5_000 / 1073741824.0).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var (phantom, _, _) = SeedSegment(daysOld: 1, sizeBytes: 4_000, channelDir: "ch-phantom");
        var (real, _, _) = SeedSegment(daysOld: 2, sizeBytes: 4_000, channelDir: "ch-real");
        Directory.Delete(Path.GetDirectoryName(phantom)!, recursive: true);

        var run = new RetentionService(_store, _holds, _root).RunOnce(DateTime.UtcNow);

        Assert.Equal(0, run.WatermarkPurged);
        Assert.True(File.Exists(real), "磁碟上還存在的真實錄影不該為了幽靈用量被刪");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private (string File, DateTime Start, DateTime End) SeedSegment(int daysOld, long sizeBytes, string channelDir = "ch001", double offsetSeconds = 0)
    {
        var dir = Path.Combine(_root, channelDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"seg-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, new byte[sizeBytes]);

        // start_time 以毫秒精度寫入且 (channel_id, stream, start_time) 有 UNIQUE 索引；
        // 同一測試內多筆 seed 若在同一毫秒內取 UtcNow 會撞唯一鍵，故用 offsetSeconds 確保相接 seed 不同秒。
        var start = DateTime.UtcNow.AddDays(-daysOld).AddSeconds(offsetSeconds);
        var end = start.AddSeconds(60);
        var id = _segments.BeginSegment(1, "main", path, start);
        _segments.CompleteSegment(id, end, sizeBytes, 60, new string('c', 64));
        return (path, start, end);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_dbPath);
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }
        catch (IOException)
        {
        }

        foreach (var dir in new[] { _root, _outside })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
