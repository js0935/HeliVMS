using HeliVMS.Shared.Models;
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
    public void 預設不啟用小時合併_清理輪不會動錄影檔()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var old = SeedHourFolder(now.AddHours(-2));

        var result = RetentionCoordinator.Run(
            _store, NewService(), _snapshotsRoot,
            quotaBytes: 1_000_000, snapshotDays: 0, alarmDays: 0, auditDays: 0, nowUtc: now);

        Assert.Equal(0, result.MergedHours);
        Assert.True(File.Exists(old[0].FilePath));
        Assert.True(File.Exists(old[1].FilePath));
    }

    [Fact]
    public void 啟用小時合併時清理輪會把舊小時換成單一檔()
    {
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = SeedHourFolder(now.AddHours(-2));

        var remuxer = new FakeMergeRemuxer();
        var result = RetentionCoordinator.Run(
            _store, NewService(), _snapshotsRoot,
            quotaBytes: 1_000_000, snapshotDays: 0, alarmDays: 0, auditDays: 0, nowUtc: now,
            hourlyMergeEnabled: true,
            mergeRemuxer: remuxer);

        Assert.Equal(1, result.MergedHours);
        Assert.Equal(2, result.MergedDeletedFiles);
        Assert.False(File.Exists(rows[0].FilePath), "換列之後原檔要刪");
        Assert.False(File.Exists(rows[1].FilePath));

        // 清理輪的產物對回放可見：ListPlayableByRange 只會看到那個小時的一列。
        var playable = _segments.ListPlayableByRange(1, "main", now.AddHours(-2), now);
        Assert.Single(playable);
    }

    /// <summary>在同一個小時資料夾種兩段（真實檔案＋final 索引列）。</summary>
    private List<SegmentRecord> SeedHourFolder(DateTime hourStartUtc)
    {
        var dir = Path.Combine(_recordingsRoot, "ch1", hourStartUtc.ToString("yyyyMMdd"), hourStartUtc.ToString("HH"));
        Directory.CreateDirectory(dir);
        var rows = new List<SegmentRecord>();
        for (var i = 0; i < 2; i++)
        {
            var start = hourStartUtc.AddMinutes(i * 30);
            var file = Path.Combine(dir, $"seg-{start:HHmmssfff}.mp4");
            File.WriteAllBytes(file, new byte[64]);
            var id = _segments.BeginSegment(1, "main", file, start);
            _segments.CompleteSegment(id, start.AddMinutes(30), 64, 1800, $"sha-{i}");
            rows.Add(_segments.Get(id)!);
        }

        return rows;
    }

    /// <summary>remux 替身：抄第一個輸入當產物；探測回固定的合理長度。</summary>
    private sealed class FakeMergeRemuxer : HourlySegmentMerger.IRemuxer
    {
        public void Remux(IReadOnlyList<string> inputs, string outputTmp)
            => File.Copy(inputs[0], outputTmp, overwrite: true);

        public double? ProbeDurationSeconds(string file) => 3600;
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

    /// <summary>
    /// 清理必須「先救回暫存檔、再清除」。
    ///
    /// <para>
    /// 順序是契約的一部分：<see cref="RetentionService.PurgeStaleTmp"/> 會把超過十分鐘的
    /// <c>.tmp</c> 直接刪掉。先清再救，等於每次程式沒收乾淨的那一段錄影都會在第一次清理時
    /// 被永久丟棄——而它其實是完整的 fMP4，只差一個改名。
    /// </para>
    /// <para>
    /// 這裡刻意把檔案的 mtime 設成一小時前（對 <c>PurgeStaleTmp</c> 而言絕對是逾時檔），
    /// 斷言它被救成 final 而不是被清除。
    /// </para>
    /// </summary>
    [Fact]
    public void 清理會先救回中斷的暫存段再清除逾時暫存檔()
    {
        // 用真實時鐘：救援（TemporarySegmentRecovery）本來就以 DateTime.UtcNow 判斷「還在寫」，
        // 若這裡固定一個過去的日期，測試就會在系統時間較早的機器上變成「檔案來自未來」而不穩定。
        var now = DateTime.UtcNow;

        var finalPath = Path.Combine(_recordingsRoot, $"seg-{Guid.NewGuid():N}.mp4");
        var tmpPath = finalPath + ".tmp";
        var bytes = new byte[64];
        bytes[3] = 0x18;
        System.Text.Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
        System.Text.Encoding.ASCII.GetBytes("isom").CopyTo(bytes, 8);
        File.WriteAllBytes(tmpPath, bytes);
        File.SetLastWriteTimeUtc(tmpPath, now.AddHours(-1));

        var id = _segments.BeginSegment(1, "main", finalPath, now.AddMinutes(-30));

        var result = RetentionCoordinator.Run(
            _store, NewService(), _snapshotsRoot,
            quotaBytes: 1_000_000, snapshotDays: 0, alarmDays: 0, auditDays: 0, nowUtc: now);

        Assert.Equal(1, result.RecoveredTmp);
        Assert.Equal(0, result.PurgedTmp);
        Assert.False(File.Exists(tmpPath), "已救回的段不該留下 .tmp，否則下一輪會被當成逾時暫存檔清掉");
        Assert.True(File.Exists(finalPath));
        Assert.Equal(SegmentStatus.Final, _segments.Get(id)!.Status);
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
