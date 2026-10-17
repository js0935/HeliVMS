using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 保留清理協調（M316）：桌面與 Headless Agent 共用的一次性清理路徑。
///
/// 這裡守的是「四件事會一起做」——配額汰除、暫存隔離、快照汰除、中繼資料保管期限。
/// 過去 Agent 完全沒有這條路徑（只錄不刪），而且任一項漏做都不會有錯誤訊息，
/// 只有在磁碟被寫爆、或稽核／事件錄音無限膨脹時才會被發現。
/// </summary>
public sealed class RetentionCoordinatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteStore _store;
    private readonly string _recordingsRoot;
    private readonly string _snapshotsRoot;
    private readonly SegmentRepository _segments;

    public RetentionCoordinatorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-coord-{Guid.NewGuid():N}.db");
        _recordingsRoot = Path.Combine(Path.GetTempPath(), $"helivms-coord-rec-{Guid.NewGuid():N}");
        _snapshotsRoot = Path.Combine(Path.GetTempPath(), $"helivms-coord-snap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_recordingsRoot);
        Directory.CreateDirectory(_snapshotsRoot);

        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        EnsureChannel(1);
    }

    [Fact]
    public void 一次協調會清除超量區段過期快照與過期中繼資料()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        // 區段總量 3000、配額 1500 → 從最舊刪到剩 1000（刪兩個）。
        var oldest = SeedSegment(now.AddHours(-3), 1000);
        SeedSegment(now.AddHours(-2), 1000);
        var newest = SeedSegment(now.AddHours(-1), 1000);

        var oldSnap = SeedSnapshot(now.AddDays(-40));
        var newSnap = SeedSnapshot(now.AddDays(-1));

        var alarms = new AlarmEventRepository(_store);
        var audio = new EventAudioRepository(_store);
        var audit = new AuditLogRepository(_store);
        var oldAlarm = alarms.Insert(1, "motion", now.AddDays(-100), detail: "old");
        var newAlarm = alarms.Insert(1, "motion", now.AddMinutes(-5), detail: "new");
        audio.Save(oldAlarm, 1, now.AddDays(-100), 1000, "audio/ogg", [1, 2, 3]);
        audio.Save(newAlarm, 1, now.AddMinutes(-5), 1000, "audio/ogg", [4, 5, 6]);
        audit.Record("system", "config.change", AuditCategories.Config, occurredAtUtc: now.AddDays(-100));
        audit.Record("system", "config.change", AuditCategories.Config, occurredAtUtc: now.AddMinutes(-5));

        var result = RetentionCoordinator.Run(
            _store, NewService(), _snapshotsRoot,
            quotaBytes: 1500, snapshotDays: 30, alarmDays: 30, auditDays: 30, nowUtc: now);

        Assert.Equal(2, result.DeletedSegments);
        Assert.Equal(1, result.SnapshotDeletedFiles);
        Assert.Equal(1, result.AlarmPurged);
        Assert.Equal(1, result.AudioPurged);
        Assert.Equal(1, result.AuditPurged);
        Assert.True(result.AnythingRemoved);

        Assert.False(File.Exists(oldest.FilePath));
        Assert.True(File.Exists(newest.FilePath));
        Assert.False(File.Exists(oldSnap));
        Assert.True(File.Exists(newSnap));
        Assert.Null(audio.GetByEvent(oldAlarm));
        Assert.NotNull(audio.GetByEvent(newAlarm));
        Assert.Equal(1, audit.Count(new AuditLogQuery { Action = "config.change" }));
    }

    [Fact]
    public void 快照天數為零時不快照汰除()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldSnap = SeedSnapshot(now.AddDays(-400));

        var result = RetentionCoordinator.Run(
            _store, NewService(), _snapshotsRoot,
            quotaBytes: 1_000_000, snapshotDays: 0, alarmDays: 0, auditDays: 0, nowUtc: now);

        Assert.Equal(0, result.SnapshotDeletedFiles);
        Assert.False(result.AnythingRemoved);
        Assert.True(File.Exists(oldSnap), "快照天數為 0 代表不汰除，不能把舊快照刪掉");
    }

    [Fact]
    public void RunConfigured會依共用設定解析根目錄並清理()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        // Headless Agent 走的就是這條：自己沒有根目錄／區段索引快取，每圈從共用設定重推。
        // 快照目錄固定落在 <dataRoot>\snapshots，錄影根目錄則讀 recording.root。
        var dataRoot = Path.Combine(Path.GetTempPath(), $"helivms-coord-data-{Guid.NewGuid():N}");
        var snapDir = Path.Combine(dataRoot, "snapshots");
        Directory.CreateDirectory(snapDir);

        var settings = new SettingsRepository(_store);
        settings.Set(RecordingStorage.RootSettingKey, _recordingsRoot);
        settings.Set(RetentionCoordinator.QuotaGbKey, "0.000001"); // 約 1KB，迫使汰除
        settings.Set(RetentionCoordinator.SnapshotDaysKey, "30");

        // 區段總量 3000、配額約 1KB → 從最舊刪。
        var oldest = SeedSegment(now.AddHours(-3), 1000);
        SeedSegment(now.AddHours(-2), 1000);
        var newest = SeedSegment(now.AddHours(-1), 1000);

        var oldSnap = Path.Combine(snapDir, "snap-old.jpg");
        File.WriteAllBytes(oldSnap, new byte[1]);
        File.SetLastWriteTimeUtc(oldSnap, now.AddDays(-40));

        var sweep = RetentionCoordinator.RunConfigured(_store, dataRoot, now);

        Assert.True(sweep.DeletedSegments > 0, "配額極小應觸發汰除");
        Assert.Equal(1, sweep.SnapshotDeletedFiles);
        Assert.False(File.Exists(oldest.FilePath));
        Assert.True(File.Exists(newest.FilePath));
        Assert.False(File.Exists(oldSnap));

        Directory.Delete(dataRoot, recursive: true);
    }

    private RetentionService NewService()
        => new(_segments, _recordingsRoot, new LegalHoldRepository(_store));
    private (long Id, string FilePath) SeedSegment(DateTime startUtc, long sizeBytes)
    {
        var file = Path.Combine(_recordingsRoot, $"seg-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(file, new byte[1]);
        var id = _segments.BeginSegment(1, "main", file, startUtc);
        _segments.CompleteSegment(id, startUtc.AddMinutes(10), sizeBytes, 600, "sha256");
        return (id, file);
    }

    private string SeedSnapshot(DateTime lastWriteUtc)
    {
        var file = Path.Combine(_snapshotsRoot, $"snap-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(file, new byte[1]);
        File.SetLastWriteTimeUtc(file, lastWriteUtc);
        return file;
    }

    private void EnsureChannel(int id)
    {
        _store.Execute(
            "INSERT OR IGNORE INTO channels (id, name, main_rtsp, recording_mode) VALUES ($id, $n, $u, 0);",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", $"ch-{id}");
                cmd.Parameters.AddWithValue("$u", $"rtsp://test/{id}");
            });
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var dir in new[] { _recordingsRoot, _snapshotsRoot })
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // 測試清理失敗不該讓測試紅掉
            }
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch (IOException)
            {
            }
        }
    }
}
